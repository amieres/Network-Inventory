module Health.HealthService

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open MQTTnet
open MQTTnet.Client
open NetDaemon.HassModel
open NetDaemon.Extensions.MqttEntityManager
open Health

// ── Health monitor ───────────────────────────────────────────────────────────
// Detects entities that silently stop reporting.
//
// Why staleness and not `unavailable`: on 2026-09-11 the EG4 ESP32 dropped 25
// times for 3-15 min each and `sensor.eg4_battery_1_power` logged *zero*
// unavailable states - it simply stopped updating and resumed. Ping missed it
// too (the ESP32 keeps its DHCP lease and answers ICMP while publishing stalls).
//
// Why a classifier: sampling 120 entities showed only ~28% have a learnable
// cadence. The other 72% are event-driven or sparse, where "hasn't updated
// recently" is normal. Monitoring those would drown real faults in noise.

type HealthService
    ( log      : ILogger<HealthService>
    , opts     : IOptions<HealthConfig>
    , haOpts   : IOptions<HaConnection>
    , ha       : IHaContext
    , httpF    : IHttpClientFactory
    , entityManager : IMqttEntityManager
    , mqttConfig    : IOptions<MqttConfiguration>
    , invOpts  : IOptions<Inventory.InventoryConfig>
    ) =

    let cfg  = opts.Value
    let conn = haOpts.Value
    let mq   = mqttConfig.Value
    let monitors = Dictionary<string, Monitor>()
    let mutable lastLearn = DateTimeOffset.MinValue
    let mutable lastSummary = ""
    let mutable registries = Registry.empty

    // Topology edits live in the same SQLite file as the inventory, so a name,
    // SSID, area or size changed in the dashboard survives a redeploy instead of
    // being overwritten by the next build.
    let dbPath = invOpts.Value.DbPath
    let openDb () =
        let c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Foreign Keys=True")
        c.Open()
        c

    /// entity id -> (deviceId, deviceName, area), for the device-level rollup.
    let deviceOf (entityId: string) =
        registries.entities
        |> Map.tryFind entityId
        |> Option.bind (fun e -> e.deviceId)
        |> Option.bind (fun did -> registries.devices |> Map.tryFind did)
        |> Option.map  (fun d -> d.deviceId, d.name, d.area)

    /// Entities belonging to an integration that no longer runs (e.g. the 42
    /// `emporia_vue` entities orphaned when the Vue 2 was reflashed to ESPHome).
    let isOrphan (entityId: string) =
        registries.entities
        |> Map.tryFind entityId
        |> Option.map  (fun e -> Rules.isOrphanPlatform e.platform)
        |> Option.defaultValue false

    let isExcluded (id: string) =
        cfg.Exclude |> Array.exists (fun s -> not (String.IsNullOrWhiteSpace s) && id.Contains s)

    // ── Learn cadences from history ──────────────────────────────────────────
    let learn () = async {
        let http = httpF.CreateClient()
        http.Timeout <- TimeSpan.FromSeconds 120.0

        // Registry first - it supplies the entity->device map and the platform
        // used to spot orphans. Refreshed each learn so new devices are picked up.
        let! regs = Registry.fetch log conn.WsUrl conn.ApiToken
        if regs.entities.Count > 0 then registries <- regs

        // Candidates: entities with a usable current state, minus excluded domains
        // and minus orphans of dead integrations.
        let candidates =
            ha.GetAllEntities()
            |> Seq.map   (fun e -> e.EntityId)
            |> Seq.filter Rules.isCandidate
            |> Seq.filter (isExcluded >> not)
            |> Seq.filter (isOrphan   >> not)
            |> Seq.toList

        log.LogInformation("Health: learning cadence for {N} candidate entities", List.length candidates)

        let window = TimeSpan.FromHours(float cfg.LearnWindowHours)
        let mutable staleness = 0
        let mutable liveness  = 0
        let mutable skipped   = 0

        for batch in candidates |> List.chunkBySize cfg.BatchSize do
            let! gapsByEntity = HistoryClient.fetchGaps http log conn.BaseUrl conn.ApiToken batch window
            // History only returns entities that have points; anything missing still
            // deserves a liveness watch if it is currently reporting a real value.
            for entityId in batch do
                let gaps    = gapsByEntity |> Map.tryFind entityId |> Option.defaultValue []
                let cadence = Rules.classify gaps
                let current = match ha.GetState entityId with null -> null | s -> s.State
                match Rules.watchFor entityId cadence current with
                | Some watch ->
                    let existing = match monitors.TryGetValue entityId with | true, m -> Some m | _ -> None
                    // Re-learning is also how a device that changed purpose, moved or
                    // was re-flashed gets a fresh threshold: the cadence is recomputed
                    // from the last LearnWindowHours, so the old behaviour is forgotten.
                    // A retired entity that is reporting again is re-adopted here.
                    let carriedState =
                        match existing with
                        | Some m ->
                            match m.lastState with
                            | Retired _ -> Ok        // it is live again (watchFor returned Some)
                            | s         -> s
                        | None -> Ok
                    monitors.[entityId] <-
                        { entityId    = entityId
                          cadence     = cadence
                          watch       = watch
                          learnedAt   = DateTimeOffset.UtcNow
                          lastState   = carriedState
                          lastChanged = existing |> Option.map (fun m -> m.lastChanged) |> Option.defaultValue DateTimeOffset.UtcNow
                          faultCount  = existing |> Option.map (fun m -> m.faultCount)  |> Option.defaultValue 0 }
                    match watch with
                    | Staleness _ -> staleness <- staleness + 1
                    | Liveness    -> liveness  <- liveness  + 1
                | None ->
                    // Currently unavailable at learn time. If we were already watching
                    // it, keep the monitor (it may be a live fault or a retired device);
                    // only drop entities we never adopted.
                    if not (monitors.ContainsKey entityId) then skipped <- skipped + 1

        lastLearn <- DateTimeOffset.UtcNow
        log.LogInformation(
            "Health: watching {Stale} by staleness + {Live} by liveness ({Skipped} skipped - already unavailable)",
            staleness, liveness, skipped)
    }

    // ── Evaluate current state of every monitored entity ─────────────────────
    let check () =
        let now      = DateTimeOffset.UtcNow
        let faults   = ResizeArray<Fault>()
        let retireAfter = TimeSpan.FromHours(float cfg.RetireAfterHours)

        for entityId in monitors.Keys |> Seq.toList do
            let m = monitors.[entityId]
            match ha.GetState entityId with
            | null -> ()
            | st ->
                let lastUpdated =
                    st.LastUpdated
                    |> Option.ofNullable
                    |> Option.map DateTimeOffset
                let age   = lastUpdated |> Option.map (fun t -> now - t) |> Option.defaultValue TimeSpan.Zero
                let fresh = Rules.evaluate st.State age m.watch lastUpdated

                // Lifecycle: a fault that has persisted past RetireAfterHours is no
                // longer an incident - the device has been retired, moved or shelved.
                // Stop alerting, but keep watching so it is re-adopted if it returns.
                let newState =
                    match m.lastState, fresh with
                    | Retired _, f when f.isFault      -> m.lastState              // stay retired
                    | Retired _, _                     -> Ok                       // came back to life
                    | prev, f when f.isFault
                                  && prev.isFault
                                  && now - m.lastChanged > retireAfter -> Retired m.lastChanged
                    | _, f                             -> f

                let faultCount = if newState.isFault then m.faultCount + 1 else 0
                let changed    = newState.label <> m.lastState.label

                monitors.[entityId] <-
                    { m with lastState   = newState
                             faultCount  = faultCount
                             // lastChanged tracks when the CURRENT condition began, so the
                             // retirement clock measures how long the fault has run.
                             lastChanged = if changed then now else m.lastChanged }

                // Report on the exact confirmation count so each fault alerts once.
                if newState.isFault && faultCount = Rules.faultConfirmations then
                    faults.Add { entityId = entityId; state = newState; detectedAt = now; lastSeen = lastUpdated }
                elif changed then
                    match m.lastState, newState with
                    | prev, Retired _ when prev.isFault ->
                        log.LogInformation("Health: {Entity} retired after {Hours:F0}h faulted - no longer alerting",
                                           entityId, (now - m.lastChanged).TotalHours)
                    | Retired _, Ok ->
                        log.LogInformation("Health: {Entity} is reporting again - re-adopted", entityId)
                    | prev, _ when prev.isFault && m.faultCount >= Rules.faultConfirmations ->
                        log.LogInformation("Health: {Entity} recovered ({Was})", entityId, prev.label)
                    | _ -> ()

        faults |> List.ofSeq

    let describe (f: Fault) =
        match f.state with
        | Stale (age, threshold) ->
            let fmt (t: TimeSpan) = if t.TotalMinutes < 60.0 then $"{t.TotalMinutes:F0}m" else $"{t.TotalHours:F1}h"
            $"{f.entityId}: no update for {fmt age} (expected every {fmt threshold})"
        | Unavailable _ -> $"{f.entityId}: unavailable"
        | _ -> f.entityId

    // Notifications are off by default: the dashboard is the primary surface, and a
    // flurry of persistent notifications for things you cannot act on immediately
    // just trains you to dismiss them. Kept behind a flag for genuinely urgent use.
    let notify (faults: Fault list) =
        if cfg.Notify && not (List.isEmpty faults) then
            // Report per DEVICE, not per entity - one dead RainMachine is 39 entities.
            let deviceNames =
                faults
                |> List.map (fun f -> deviceOf f.entityId |> Option.map (fun (_, n, _) -> n) |> Option.defaultValue f.entityId)
                |> List.distinct
            let title = if deviceNames.Length = 1 then "Health: 1 device stopped reporting"
                        else $"Health: {deviceNames.Length} devices stopped reporting"
            ha.CallService("notify", "persistent_notification",
                data = {| title = title; message = String.Join("\n", deviceNames) |})

    // ── Publish a summary sensor so health is visible in HA/history ───────────
    // IHaContext cannot create entities, so this goes through MQTT discovery the
    // same way EG4Battery does: explicit unique_id/object_id/state_topic, with
    // state published to a separate tree by a raw MQTTnet client.
    let stateTopic = "health/state/summary"
    let attrsTopic = "health/state/summary_attrs"

    let mqtt = (MqttFactory()).CreateMqttClient()

    let mqttOpts =
        let port = if mq.Port > 0 then mq.Port else 1883
        let b = MqttClientOptionsBuilder().WithTcpServer(mq.Host, port).WithClientId("health-monitor")
        let b = if String.IsNullOrEmpty mq.UserName then b else b.WithCredentials(mq.UserName, mq.Password)
        b.Build()

    let publish (topic: string) (payload: string) =
        try
            if not mqtt.IsConnected then
                mqtt.ConnectAsync(mqttOpts) |> Async.AwaitTask |> Async.RunSynchronously |> ignore
            MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(true).Build()
            |> fun m -> mqtt.PublishAsync(m) |> Async.AwaitTask |> Async.RunSynchronously |> ignore
        with ex ->
            log.LogWarning(ex, "Health: MQTT publish to {Topic} failed", topic)

    let mutable entityCreated = false

    let ensureEntity () =
        if not entityCreated then
            let opts  = EntityCreationOptions(Name = "Health Monitor", UniqueId = "health_monitor")
            let extra = {| object_id = "health_monitor"; state_topic = stateTopic
                           json_attributes_topic = attrsTopic
                           icon = "mdi:heart-pulse"
                           unit_of_measurement = "faults"
                           device = {| identifiers = [| "health_monitor" |]
                                       name = "Health Monitor"
                                       model = "NetDaemon"
                                       manufacturer = "AbeFsDaemon" |} |}
            entityManager.CreateAsync("sensor.health_monitor", opts, extra)
            |> Async.AwaitTask |> Async.RunSynchronously
            entityCreated <- true

    let publishSummary () =
        let all      = monitors.Values |> Seq.toList
        let faulted  = all |> List.filter Rules.shouldReport
        let summary  = String.Join("|", faulted |> List.map (fun m -> m.entityId) |> List.sort)
        if summary <> lastSummary then
            lastSummary <- summary
            ensureEntity ()
            let attrs =
                {| monitored        = List.length all
                   faulted          = List.length faulted
                   faulted_entities = faulted |> List.map (fun m -> m.entityId) |> List.sort
                   stale            = faulted |> List.filter (fun m -> match m.lastState with Stale _       -> true | _ -> false) |> List.length
                   unavailable      = faulted |> List.filter (fun m -> match m.lastState with Unavailable _ -> true | _ -> false) |> List.length
                   by_staleness     = all |> List.filter (fun m -> match m.watch with Staleness _ -> true | _ -> false) |> List.length
                   by_liveness      = all |> List.filter (fun m -> match m.watch with Liveness    -> true | _ -> false) |> List.length
                   last_learned     = lastLearn.ToString("o") |}
            publish stateTopic (string (List.length faulted))
            publish attrsTopic (Text.Json.JsonSerializer.Serialize attrs)

    // ── Public API (consumed by the web dashboard) ───────────────────────────

    /// One row per device, faults first. This is what the dashboard renders.
    member _.GetDeviceHealth() : Devices.DeviceHealth list =
        Devices.rollup deviceOf (monitors.Values |> Seq.toList)

    /// Per-entity detail, for drill-down from a device row.
    member _.GetEntityHealth() =
        monitors.Values
        |> Seq.map (fun m ->
            {| entityId  = m.entityId
               state     = m.lastState.label
               watch     = m.watch.label
               cadence   = m.cadence.label
               threshold = (match m.watch with Staleness t -> Nullable t.TotalSeconds | _ -> Nullable())
               since     = m.lastChanged |})
        |> Seq.sortBy (fun r -> r.entityId)
        |> Seq.toList

    member _.IsReady = lastLearn > DateTimeOffset.MinValue

    /// Seed topology with stored user edits layered on top.
    member this.GetNodes() : Node list =
        try
            use c = openDb ()
            let ov = Store.loadOverrides c
            let custom, deleted = Store.loadCustomNodes c
            (Topology.nodes @ custom)
            |> List.filter (fun n -> not (deleted.Contains n.key))
            |> List.map (Store.applyOverrides ov)
        with ex ->
            log.LogWarning(ex, "Health: could not load topology overrides")
            Topology.nodes

    member this.GetEdges() : Edge list =
        // Edges implied by each node's powerFrom/lan, plus the hand-drawn ones.
        // Derived first so the two can never disagree.
        let ns = this.GetNodes()
        let live = ns |> List.map (fun n -> n.key) |> Set.ofList
        let all =
            try
                use c = openDb ()
                Topology.edges @ Topology.derivedEdges ns @ Store.loadUserEdges c
            with _ -> Topology.edges @ Topology.derivedEdges ns
        // Drop edges pointing at nodes that no longer exist.
        all
        |> List.filter (fun e -> live.Contains e.child && live.Contains e.parent)
        |> List.distinctBy (fun e -> e.child, e.parent, e.kind)

    /// Interfaces per inventory device name: one entry per physical interface,
    /// with its MAC, IP and kind. A device can legitimately have several - the
    /// Mac Studio has an ethernet and a wifi interface with DIFFERENT MACs and
    /// IPs - so the diagram shows an icon per interface rather than one per
    /// device. Bluetooth addresses come through the same way, which is how a
    /// BLE-capable device gets its bluetooth icon.
    member _.GetDeviceIfaces() : Map<string, Ifaces.Iface list> =
        try
            use c = openDb ()
            Ifaces.load c
        with ex ->
            log.LogWarning(ex, "Health: could not load device interfaces")
            Map.empty

    member _.AddNode(key, label, kind, area, device, entity, powerFrom, lan, size) =
        use c = openDb ()
        Store.migrate c
        Store.addCustomNode c key label kind area device entity powerFrom lan size

    /// Value + unit for a power sensor, so the caller can normalise kW to W.
    member _.GetPowerReading(entityId: string) : (float * string) option =
        match ha.GetState entityId with
        | null -> None
        | st ->
            match Double.TryParse(st.State, Globalization.NumberStyles.Float,
                                  Globalization.CultureInfo.InvariantCulture) with
            | true, v ->
                let unit =
                    match st.Attributes with
                    | null -> "W"
                    | attrs ->
                        match attrs.TryGetValue "unit_of_measurement" with
                        | true, u when not (isNull u) -> string u
                        | _ -> "W"
                Some (v, unit)
            | _ -> None

    /// AbeVue2 names its circuit sensors by BREAKER number
    /// (sensor.abevue2_<n>_<description>), so a breaker node can find its own.
    member _.FindVuePower(breakerNum: string) : string option =
        let prefix = "sensor.abevue2_" + breakerNum + "_"
        ha.GetAllEntities()
        |> Seq.map (fun e -> e.EntityId)
        |> Seq.filter (fun e -> e.StartsWith prefix && not (e.EndsWith "_daily_energy"))
        |> Seq.tryHead

    /// Operate a real smart switch. Returns the entity acted on, or why not.
    /// Power stations whose output the monitoring system depends on. These are
    /// read-only: the dashboard shows their AC-output state but offers no
    /// control, and OperateSwitch refuses them outright.
    static member val CriticalSupply = Set.ofList [ "ac500_1"; "ac500_2" ] with get

    member this.OperateSwitch(key: string, turnOn: bool) : Result<string, string> =
        let node = this.GetNodes() |> List.tryFind (fun n -> n.key = key)
        match node with
        | None -> Result.Error ("unknown node: " + key)
        | Some n when HealthService.CriticalSupply |> Set.contains n.key ->
            // NEVER actuate the AC500s. They feed circuits A-J, which power the
            // Game Room - Home Assistant itself, the modem and every router.
            // Switching one off would black out the system issuing the command,
            // leaving no way to switch it back on. Their AC-output switch is
            // read to know whether they are still supplying power, never
            // written. (The AC200M is NOT on this list: it feeds only the
            // window A/C and the battery fan, so it is safe to operate.)
            Result.Error (key + " is a critical power station - refusing to switch it")
        | Some n ->
            match n.outputEntity with
            | Some e when e.StartsWith "cover." ->
                // A cover is not a switch: it takes open_cover/close_cover, and
                // its state is open/closed rather than on/off.
                let ent = NetDaemon.HassModel.Entities.Entity(ha, e)
                ent.CallService(if turnOn then "open_cover" else "close_cover")
                log.LogInformation("Health: {Action} {Entity} (node {Key})",
                                   (if turnOn then "opened" else "closed"), e, key)
                Result.Ok e
            | Some e when e.StartsWith "switch." || e.StartsWith "light." || e.StartsWith "fan." ->
                // A breaker or transfer-switch POSITION is recorded, not actuated;
                // only a real controllable entity can be operated.
                let ent = NetDaemon.HassModel.Entities.Entity(ha, e)
                ent.CallService(if turnOn then "turn_on" else "turn_off")
                log.LogInformation("Health: {Action} {Entity} (node {Key})",
                                   (if turnOn then "turned on" else "turned off"), e, key)
                Result.Ok e
            | Some e -> Result.Error (e + " is not an operable entity")
            | None   -> Result.Error (key + " has no output entity to switch")

    member _.GetNotes() : Map<string, string> =
        try use c = openDb () in Store.loadNotes c
        with _ -> Map.empty

    member _.SaveNote(key, note) =
        use c = openDb ()
        Store.migrate c
        Store.saveNote c key note

    member _.RenameNode(oldKey, newKey) =
        use c = openDb ()
        Store.migrate c
        Store.renameNode c oldKey newKey

    member _.DeleteNode(key) =
        use c = openDb ()
        Store.migrate c
        Store.deleteNode c key

    member _.GetPositions() =
        try use c = openDb () in Store.loadPositions c
        with _ -> Map.empty

    member _.SaveOverride(o: Store.Override) =
        use c = openDb ()
        Store.migrate c
        Store.saveOverride c o

    member _.AddEdge(child, parent, kind, note) =
        use c = openDb ()
        Store.migrate c
        Store.addUserEdge c child parent kind note

    member _.RemoveEdge(child, parent, kind) =
        use c = openDb ()
        Store.removeUserEdge c child parent kind

    member _.SavePositions(ps) =
        use c = openDb ()
        Store.migrate c
        Store.savePositions c ps

    /// Raw HA state strings for arbitrary entities, used for output/link
    /// indicators that are not themselves health signals.
    member this.GetRawStates() : Map<string, string> =
        this.GetNodes()
        |> List.collect (fun n -> [ n.outputEntity; n.socEntity ] |> List.choose id)
        |> List.distinct
        |> List.choose (fun e ->
            match ha.GetState e with
            | null -> None
            | st   -> Some (e, st.State))
        |> Map.ofList

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                if not cfg.Enabled then
                    log.LogInformation("Health: disabled by configuration")
                else
                // Topology-override tables live alongside the inventory DB.
                try
                    use c = openDb ()
                    Store.migrate c
                with ex -> log.LogWarning(ex, "Health: could not migrate override tables")

                let loop = async {
                    // Let NetDaemon finish connecting and the entity list populate.
                    do! Async.Sleep 30_000
                    try do! learn () with ex -> log.LogError(ex, "Health: initial learn failed")

                    while not ct.IsCancellationRequested do
                        try
                            if DateTimeOffset.UtcNow - lastLearn > TimeSpan.FromHours(float cfg.RelearnHours) then
                                do! learn ()
                            let faults = check ()
                            if not (List.isEmpty faults) then
                                for f in faults do log.LogWarning("Health fault: {Desc}", describe f)
                                notify faults
                            publishSummary ()
                        with ex ->
                            log.LogError(ex, "Health: check cycle failed")
                        do! Async.Sleep (cfg.CheckIntervalSec * 1000)
                }
                Async.Start(loop, ct)
            }

        member _.StopAsync(_ct: CancellationToken) = task { () }

// ── DI registration ──────────────────────────────────────────────────────────
// Lives here (not WebHost.fs) because F# compiles files in order and the Health
// modules come after Inventory.
let addHealthServices
    (cfg: Microsoft.Extensions.Configuration.IConfiguration)
    (services: Microsoft.Extensions.DependencyInjection.IServiceCollection) =
    Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions
        .Configure<HealthConfig>(services, cfg.GetSection "HealthMonitor") |> ignore
    Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions
        .Configure<HaConnection>(services, cfg.GetSection "HomeAssistant") |> ignore
    // Singleton + hosted service (same pattern as ScanService) so the web API can
    // inject it to serve the dashboard.
    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
        .AddSingleton<HealthService>(services) |> ignore
    Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions
        .AddHostedService<HealthService>(services, fun sp ->
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<HealthService>(sp)) |> ignore

