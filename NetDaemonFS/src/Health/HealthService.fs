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
    ) =

    let cfg  = opts.Value
    let conn = haOpts.Value
    let mq   = mqttConfig.Value
    let monitors = Dictionary<string, Monitor>()
    let mutable lastLearn = DateTimeOffset.MinValue
    let mutable lastSummary = ""

    let isExcluded (id: string) =
        cfg.Exclude |> Array.exists (fun s -> not (String.IsNullOrWhiteSpace s) && id.Contains s)

    // ── Learn cadences from history ──────────────────────────────────────────
    let learn () = async {
        let http = httpF.CreateClient()
        http.Timeout <- TimeSpan.FromSeconds 120.0

        // Candidates: entities with a usable current state, minus excluded domains.
        let candidates =
            ha.GetAllEntities()
            |> Seq.map   (fun e -> e.EntityId)
            |> Seq.filter Rules.isCandidate
            |> Seq.filter (isExcluded >> not)
            |> Seq.toList

        log.LogInformation("Health: learning cadence for {N} candidate entities", List.length candidates)

        let window = TimeSpan.FromHours(float cfg.LearnWindowHours)
        let mutable staleness = 0
        let mutable liveness  = 0
        let mutable skipped   = 0

        for batch in candidates |> List.chunkBySize cfg.BatchSize do
            let! gapsByEntity = HistoryClient.fetchGaps http log conn.BaseUrl conn.Token batch window
            // History only returns entities that have points; anything missing still
            // deserves a liveness watch if it is currently reporting a real value.
            for entityId in batch do
                let gaps    = gapsByEntity |> Map.tryFind entityId |> Option.defaultValue []
                let cadence = Rules.classify gaps
                let current = match ha.GetState entityId with null -> null | s -> s.State
                match Rules.watchFor cadence current with
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

    let notify (faults: Fault list) =
        if cfg.Notify && not (List.isEmpty faults) then
            let lines = faults |> List.map describe
            let title = if faults.Length = 1 then "Health: 1 device stopped reporting"
                        else $"Health: {faults.Length} devices stopped reporting"
            ha.CallService("notify", "persistent_notification",
                data = {| title = title; message = String.Join("\n", lines) |})

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

    interface IHostedService with
        member _.StartAsync(ct: CancellationToken) =
            task {
                if not cfg.Enabled then
                    log.LogInformation("Health: disabled by configuration")
                else
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
    Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions
        .AddHostedService<HealthService>(services) |> ignore
