module Health.Api

open System
open Falco
open Falco.Routing
open Health
open Health.HealthService

// ── Flat JSON projections ────────────────────────────────────────────────────
// HealthState and Watch are DUs; System.Text.Json cannot serialize them, so
// everything crossing the wire is flattened here (same approach as Inventory.Api).

type DeviceHealthJ = {
    deviceId    : string
    name        : string
    area        : string option
    state       : string          // ok | stale | unavailable | warmup | retired
    detail      : string option   // human-readable reason, e.g. "no update for 14m"
    entityCount : int
    liveCount   : int
    staleSecs   : float option    // longest staleness across the device's entities
    entities    : string list
    /// "device" for a real HA device; "helper" for alerts/templates/integration
    /// rows that have no device registry entry. The dashboard hides helpers by
    /// default - they are monitored, but they are not things you can go fix.
    rowKind     : string
}

let private fmtAge (t: TimeSpan) =
    if   t.TotalMinutes <  1.0 then $"{t.TotalSeconds:F0}s"
    elif t.TotalHours   <  1.0 then $"{t.TotalMinutes:F0}m"
    elif t.TotalDays    <  1.0 then $"{t.TotalHours:F1}h"
    else                            $"{t.TotalDays:F1}d"

// F# forbids quoted strings inside interpolation holes, so format outside.
let private stamp (t: DateTimeOffset) = t.ToLocalTime().ToString "yyyy-MM-dd HH:mm"

let private project (d: Devices.DeviceHealth) : DeviceHealthJ =
    let detail =
        match d.state with
        | Stale (age, threshold) ->
            let a = fmtAge age
            let e = fmtAge threshold
            Some $"no update for {a} (expected every {e})"
        | Unavailable (Some t)   -> let s = stamp t     in Some $"unavailable since {s}"
        | Unavailable None       -> Some "unavailable"
        | Retired since          -> let s = stamp since in Some $"retired - faulted since {s}"
        | Warmup                 -> Some "learning"
        | Ok                     -> None
    { deviceId    = d.deviceId
      name        = d.name
      area        = d.area
      state       = d.state.label
      detail      = detail
      entityCount = d.entityCount
      liveCount   = d.liveCount
      staleSecs   = d.worstAge |> Option.map (fun t -> t.TotalSeconds)
      entities    = d.entities
      rowKind     = if Devices.isSynthetic d.deviceId then "helper" else "device" }

// ── Handlers ─────────────────────────────────────────────────────────────────

let private getDevices (svc: HealthService) : HttpHandler =
    fun ctx ->
        let rows = svc.GetDeviceHealth() |> List.map project
        let realRows = rows |> List.filter (fun r -> r.rowKind = "device")
        let counts =
            rows
            |> List.countBy (fun r -> r.state)
            |> List.map (fun (k, v) -> k, v)
            |> dict
        let pick k = match counts.TryGetValue k with | true, v -> v | _ -> 0
        // Fault counts must be over REAL devices: the table hides helpers by
        // default, so counting them in the banner reports problems the user
        // cannot see or act on.
        let realCounts = realRows |> List.countBy (fun r -> r.state) |> dict
        let pickReal k = match realCounts.TryGetValue k with | true, v -> v | _ -> 0
        Response.ofJson
            {| ready    = svc.IsReady
               total    = List.length realRows
               totalAll = List.length rows
               helpers  = (List.length rows) - (List.length realRows)
               ok       = pickReal "ok"
               stale    = pickReal "stale"
               unavailable = pickReal "unavailable"
               warmup   = pickReal "warmup"
               retired  = pickReal "retired"
               // Helper-only faults, surfaced separately so they are visible
               // without inflating the device count.
               helperFaults = (pick "stale" + pick "unavailable") - (pickReal "stale" + pickReal "unavailable")
               devices  = rows |} ctx

let private getEntities (svc: HealthService) : HttpHandler =
    fun ctx -> Response.ofJson (svc.GetEntityHealth()) ctx

// ── Topology + correlation (drives the diagram) ──────────────────────────────

let private getTopology (svc: HealthService) : HttpHandler =
    fun ctx ->
        // A topology node is faulted if the device or entity backing it is.
        let devHealth = svc.GetDeviceHealth()
        let byName    = devHealth |> List.map (fun d -> d.name, d) |> Map.ofList
        let entHealth = svc.GetEntityHealth() |> List.map (fun e -> e.entityId, e.state) |> Map.ofList

        let liveNodes = svc.GetNodes()
        let liveNodeByKey = liveNodes |> List.map (fun n -> n.key, n) |> Map.ofList

        let isFaulted (key: string) =
            match liveNodeByKey |> Map.tryFind key with
            | None -> false
            | Some n ->
                let byDevice =
                    n.device
                    |> Option.bind (fun d -> byName |> Map.tryFind d)
                    |> Option.map  (fun d -> d.state.isFault)
                let byEntity =
                    n.entity
                    |> Option.bind (fun e -> entHealth |> Map.tryFind e)
                    |> Option.map  (fun s -> s = "stale" || s = "unavailable")
                // Prefer a direct entity signal, fall back to the device rollup.
                match byEntity, byDevice with
                | Some f, _      -> f
                | None, Some f   -> f
                | None, None     -> false

        let liveEdges = svc.GetEdges()
        let liveByKey = liveNodes |> List.map (fun n -> n.key, n) |> Map.ofList
        // Analyse the LIVE graph (seed + overrides + derived + user edges), not the
        // empty hardcoded list - every edge is derived now, so the old call
        // explained nothing.
        let verdicts = Correlate.analyseWith liveNodes liveEdges isFaulted

        // Raw entity states, for output/link indicators.
        let entRaw = svc.GetRawStates()
        // Interfaces from the inventory: one icon per physical interface, so a
        // device with both ethernet and wifi (the Mac Studio) shows both, each
        // with its own MAC and IP, and a BLE-capable device gets a bluetooth icon.
        let deviceIfaces = svc.GetDeviceIfaces()
        // Live AC draw per node. AbeVue2 circuit sensors are numbered by BREAKER,
        // so a breaker node resolves its own sensor automatically. Units are
        // mixed (some W, some kW), so normalise to watts.
        let powerOf (n: Node) : float option =
            let fromEntity (e: string) =
                match svc.GetPowerReading e with
                | Some (v, unit) ->
                    let w = if unit = "kW" then v * 1000.0 else v
                    Some w
                | None -> None
            match n.powerEntity with
            | Some e -> fromEntity e
            | None ->
                // Breakers: match sensor.abevue2_<n>_* by number.
                if n.key.StartsWith "breaker_" then
                    let num = n.key.Substring 8
                    svc.FindVuePower num |> Option.bind (fun e -> fromEntity e)
                else None
        let socOf (n: Node) : float option =
            n.socEntity
            |> Option.bind (fun e ->
                 match entRaw |> Map.tryFind e with
                 | Some v ->
                     match System.Double.TryParse(v.Trim()) with
                     | true, pct -> Some pct
                     | _         -> None
                 | None -> None)

        let notes = svc.GetNotes()

        // ── Power propagation ────────────────────────────────────────────────
        // Power is TRANSITIVE: it flows source -> ... -> device, so a node is
        // dead when ANY link in the chain above it is dead. The old check read
        // `Topology.edges` (empty since edges became derived) and only looked
        // one hop up, so nothing downstream of a dead source ever went dark.
        // `powerFrom = area:X` targets the area-kind node covering area X.
        let areaNodeOf (area: string) =
            liveNodes
            |> List.tryFind (fun n -> n.kind = "area" && n.area = Some area)
            |> Option.map (fun n -> n.key)
        let parentKey (src: PowerSource) =
            match src with
            | FromDevice k -> Some k
            | FromArea a   -> areaNodeOf a
            | PowerUnknown -> None

        // A station/plug whose OWN output is switched off (or reporting no AC
        // watts) stops feeding everything downstream of it.
        let outputOffOf (key: string) =
            liveByKey
            |> Map.tryFind key
            |> Option.bind (fun n -> n.outputEntity)
            |> Option.map (fun e ->
                 match entRaw |> Map.tryFind e with
                 | Some v ->
                     match v.Trim().ToLowerInvariant() with
                     | "off" -> true
                     | "on"  -> false
                     | ""    -> false
                     // Cover states are a POSITION, never a power cut.
                     | "open" | "closed" | "opening" | "closing" -> false
                     // A watt reading: treat a true zero as output off, but do
                     // not read `unavailable` as off - a lost BLE connection to
                     // the station says nothing about its AC output, and
                     // blacking out the house on a dropped sensor would be a
                     // far worse error than missing a real shutdown.
                     | num ->
                         match System.Double.TryParse num with
                         | true, w -> w <= 0.0
                         | _       -> false
                 | None -> false)
            |> Option.defaultValue false

        // Which upstream feeds actually carry power INTO this node right now.
        // A transfer switch passes exactly one (chosen by its position); a 240 V
        // device needs both legs, so either one failing kills it.
        let feedsOf (n: Node) =
            match n.feedMode with
            | Both -> [ parentKey n.powerFrom; parentKey n.altFrom ] |> List.choose id, true
            | Selected ->
                match n.position with
                | PosOff  -> [], false          // switched off: no feed at all
                | PosLine -> [ parentKey n.altFrom   ] |> List.choose id, true
                | _       -> [ parentKey n.powerFrom ] |> List.choose id, true

        // Memoised depth-first walk up the power tree, so a 30-node chain is
        // resolved once rather than re-walked per node. Cycles resolve to
        // powered rather than looping forever.
        let powerCache = System.Collections.Generic.Dictionary<string, bool>()
        let rec hasPower (visiting: Set<string>) (key: string) : bool =
            match powerCache.TryGetValue key with
            | true, v -> v
            | _ ->
            if visiting.Contains key then true
            else
                let visiting = visiting.Add key
                let result =
                    match liveByKey |> Map.tryFind key with
                    | None -> true
                    | Some n ->
                        // Only switch positions and upstream supply decide this.
                        // A FAULTED node is deliberately not treated as dead:
                        // "stopped reporting" is not evidence of a power cut,
                        // and asserting one sends you to the wrong remedy.
                        if n.position = PosOff then false
                        elif n.kind = "power-station" || n.kind = "battery" then
                            // Its upstream feed only CHARGES it; the station
                            // keeps supplying its circuits from the cells.
                            true
                        else
                            let feeds, needsFeed = feedsOf n
                            if not needsFeed then false
                            elif List.isEmpty feeds then
                                // No upstream feed. A battery runs off its own
                                // cells, and grid/internet are tree roots, so
                                // all of these stand on their own. Anything else
                                // is merely unmapped - and unmapped must not
                                // read as dead.
                                true
                            else
                                // 240 V needs BOTH legs; everything else needs
                                // the single feed that is currently selected.
                                let alive k =
                                    hasPower visiting k && not (outputOffOf k)
                                match n.feedMode with
                                | Both     -> feeds |> List.forall alive
                                | Selected -> feeds |> List.exists alive
                powerCache.[key] <- result
                result

        let poweredOff (key: string) = not (hasPower Set.empty key)

        let radioDown (key: string) =
            liveEdges
            |> List.filter (fun e -> e.child = key && (e.kind = Network || e.kind = BtHost))
            |> List.exists (fun e -> isFaulted e.parent)

        // ── Evidence for the nodes that are actually broken ──────────────────
        // Only faulted nodes are probed. A device doing its job is its own proof
        // of health - the Pi Zero publishing thermal frames demonstrates power,
        // radio and camera all at once, and pinging it would tell us nothing we
        // did not already know. Once it stops, the question changes from "is it
        // ok" to "which of several causes is it", and that is worth traffic.
        let faultedKeys =
            verdicts
            |> List.filter (fun v -> v.faulted)
            |> List.map (fun v -> v.key)
            |> Set.ofList

        /// First current IP known for a node, from the inventory join.
        let ipOf (n: Node) =
            n.device
            |> Option.bind (fun d -> deviceIfaces |> Map.tryFind d)
            |> Option.defaultValue []
            |> List.tryPick (fun (i: Ifaces.Iface) -> i.ip)

        /// Nodes sharing a power source with this one - the comparison that
        /// separates "this device died" from "the circuit died". Deliberately
        /// prefers a mate on a DIFFERENT SSID: one that is up proves the power
        /// is fine without the radio confounding the answer.
        let circuitMates (n: Node) =
            match n.powerFrom with
            | PowerUnknown -> []
            | src ->
                liveNodes
                |> List.filter (fun m -> m.key <> n.key && m.powerFrom = src)
                |> List.sortBy (fun m -> if m.lan = n.lan then 1 else 0)

        let diagnoses =
            if Set.isEmpty faultedKeys then Map.empty
            else
                // Collect every address worth probing in ONE parallel batch.
                let targets =
                    faultedKeys
                    |> Set.toList
                    |> List.collect (fun k ->
                        match liveByKey |> Map.tryFind k with
                        | None -> []
                        | Some n ->
                            (ipOf n |> Option.toList)
                            @ (circuitMates n |> List.truncate 2 |> List.choose ipOf))
                    |> List.distinct
                let probes = Probe.pingAll 2000 targets

                faultedKeys
                |> Set.toList
                |> List.choose (fun k ->
                    liveByKey
                    |> Map.tryFind k
                    |> Option.map (fun n ->
                        let reachOf ip = probes |> Map.tryFind ip |> Option.map (fun e -> e.reach)
                        let findings = ResizeArray<Diagnose.Finding>()

                        // The symptom itself. It says something is wrong; on its
                        // own it says nothing about what.
                        findings.Add(Diagnose.support
                                        [ Diagnose.DeviceHung; Diagnose.PeripheralFailed
                                          Diagnose.IntegrationBroken ]
                                        (n.label + " stopped reporting"))

                        // The device's own reachability is the single most
                        // discriminating observation available.
                        match ipOf n |> Option.bind reachOf with
                        | Some (Probe.Up ms) ->
                            findings.Add(Diagnose.ruleOut
                                            [ Diagnose.PowerLost; Diagnose.NetworkDown ]
                                            $"answers ping in {ms} ms - so it has power and a working radio")
                            findings.Add(Diagnose.support
                                            [ Diagnose.DeviceHung; Diagnose.PeripheralFailed
                                              Diagnose.IntegrationBroken ]
                                            "reachable but not doing its job")
                        | Some Probe.Down ->
                            findings.Add(Diagnose.support
                                            [ Diagnose.PowerLost; Diagnose.NetworkDown
                                              Diagnose.DeviceHung ]
                                            "does not answer ping")
                        | Some Probe.Unknown | None ->
                            findings.Add(Diagnose.support [] "no address to probe")

                        // Circuit-mates separate a dead circuit from a dead device.
                        for m in circuitMates n |> List.truncate 2 do
                            match ipOf m |> Option.bind reachOf with
                            | Some (Probe.Up _) ->
                                let sameSsid = m.lan = n.lan
                                findings.Add(Diagnose.ruleOut
                                                [ Diagnose.PowerLost ]
                                                (m.label + " shares its power source and is up"
                                                 + (if sameSsid then "" else " (on a different SSID)")))
                            | Some Probe.Down ->
                                findings.Add(Diagnose.support
                                                [ Diagnose.PowerLost ]
                                                (m.label + " shares its power source and is also down"))
                            | _ -> ()

                        // Upstream power, from the model rather than a probe.
                        if not (hasPower Set.empty n.key) then
                            findings.Add(Diagnose.support [ Diagnose.PowerLost ]
                                            "its power source is off or unavailable")

                        k, Diagnose.rank k (List.ofSeq findings)))
                |> Map.ofList

        let nodesJ =
            liveNodes
            |> List.map (fun n ->
                let v = verdicts |> List.tryFind (fun x -> x.key = n.key)
                let verdict, because, affected =
                    match v |> Option.map (fun x -> x.verdict) with
                    | Some (Correlate.RootCause a)          -> "root-cause", None, a
                    | Some (Correlate.Suppressed (p, kind)) -> "suppressed", Some $"{p} ({kind.label})", []
                    | _                                     -> "healthy", None, []
                {| key      = n.key
                   label    = n.label
                   kind     = n.kind
                   area     = n.area
                   device   = n.device
                   entity   = n.entity
                   faulted  = v |> Option.map (fun x -> x.faulted) |> Option.defaultValue false
                   verdict  = verdict
                   because  = because
                   affected = affected
                   blindSpot = n.blindSpot
                   diagnosis =
                     diagnoses
                     |> Map.tryFind n.key
                     |> Option.map (fun ds ->
                          ds |> List.truncate 3
                             |> List.map (fun d ->
                                  {| cause  = d.cause.label
                                     score  = d.score
                                     remedy = d.remedy
                                     why    = d.findings |> List.map (fun f -> f.text) |})) 
                   remedy    = n.remedy
                   size      = n.size
                   link      = n.link
                   powerFrom = n.powerFrom.label
                   altFrom   = n.altFrom.label
                   position  = n.position.label
                   feedMode  = n.feedMode.label
                   watts     = powerOf n
                   soc       = socOf n
                   // True when HA can really switch this node, as opposed to a
                   // breaker/transfer-switch position that is only recorded.
                   outputEntity = n.outputEntity
                   powerEntity  = n.powerEntity
                   switchable =
                     not (HealthService.CriticalSupply |> Set.contains n.key) &&
                     (n.outputEntity
                      |> Option.map (fun e ->
                           e.StartsWith "switch." || e.StartsWith "cover." ||
                           e.StartsWith "light."  || e.StartsWith "fan.")
                      |> Option.defaultValue false)
                   lan       = n.lan.label
                   // One entry per physical interface, straight from the
                   // inventory - the topology does not re-declare MACs or IPs.
                   note      = notes |> Map.tryFind n.key
                   ifaces    =
                     n.device
                     |> Option.bind (fun d -> deviceIfaces |> Map.tryFind d)
                     |> Option.defaultValue []
                     |> List.map (fun i ->
                          {| kind = i.kind.label; mac = i.mac; ip = i.ip; conn = i.conn |})
                   // Output state: a station or plug can be healthy while its
                   // output is switched OFF, which is a different failure from
                   // the device itself being down.
                   outputOn  =
                     n.outputEntity
                     |> Option.map (fun e ->
                          match entRaw |> Map.tryFind e with
                          | Some v ->
                              let v = v.ToLowerInvariant()
                              not (v = "off" || v = "0" || v = "closed" ||
                                   v = "unavailable" || v = "unknown")
                          | None -> true)
                   // Raw state, so a cover can read OPEN/CLOSED rather than ON/OFF.
                   outputState =
                     n.outputEntity |> Option.bind (fun e -> entRaw |> Map.tryFind e)
                   // Powered / radio-up, so the UI can grey out the box or the
                   // radio icon independently.
                   powered   = not (poweredOff n.key)
                   radioUp   = not (radioDown n.key) |})

        let edgesJ =
            liveEdges
            |> List.map (fun e -> {| child = e.child; parent = e.parent; kind = e.kind.label; note = e.note |})

        let roots =
            Correlate.rootCauses verdicts
            |> List.map (fun (v, affected) ->
                let node = Topology.nodeByKey |> Map.tryFind v.key
                {| key      = v.key
                   label    = v.label
                   affected = affected
                   remedy   = node |> Option.bind (fun n -> n.remedy) |})

        // Nodes whose failure would take HA down with it, so nothing would be
        // reported. Surfaced so the dashboard can name its own blind spots.
        let blindSpots =
            liveNodes
            |> List.filter (fun n -> n.blindSpot)
            |> List.map (fun n -> {| key = n.key; label = n.label; remedy = n.remedy |})

        // Areas, so the diagram can draw grouping rectangles.
        let areas =
            liveNodes
            |> List.choose (fun n -> n.area |> Option.map (fun a -> a, n.key))
            |> List.groupBy fst
            |> List.map (fun (a, xs) -> {| name = a; keys = xs |> List.map snd |})

        let positions =
            svc.GetPositions()
            |> Map.toList
            |> List.map (fun (k, (x, y)) -> {| key = k; x = x; y = y |})

        Response.ofJson {| nodes = nodesJ; edges = edgesJ; rootCauses = roots
                           blindSpots = blindSpots; areas = areas
                           positions = positions
                           // Distinct SSIDs seen in the topology, so the editor can
                           // offer them instead of requiring free text.
                           links = liveNodes |> List.choose (fun n -> n.link) |> List.distinct |> List.sort
                           kinds =
                             // A fixed catalogue UNIONED with kinds in use: deriving
                             // the list purely from usage meant the last node of a
                             // kind changing type deleted that option permanently.
                             [ "grid"; "power-station"; "battery"; "breaker"; "triple-switch"; "circuit"
                               "plug"; "outlet"; "ap"; "modem"; "internet"; "switch"
                               "pi"; "computer"; "host"; "esp32"; "sensor"; "camera"
                               "ev"; "opener"; "appliance"; "zone"; "area"; "device" ]
                             @ (liveNodes |> List.map (fun n -> n.kind))
                             |> List.distinct
                             |> List.filter (fun k -> not (String.IsNullOrWhiteSpace k))
                             |> List.sort
                           // Only infrastructure can be a WIRED source - offering
                           // every device made the list unusable.
                           wiredSources =
                             liveNodes
                             |> List.filter (fun n -> [ "ap"; "modem"; "switch"; "internet" ] |> List.contains n.kind)
                             |> List.map (fun n -> n.key)
                             |> List.sort
                           knownAreas = liveNodes |> List.choose (fun n -> n.area) |> List.distinct |> List.sort |} ctx

// ── Editing ──────────────────────────────────────────────────────────────────
// Topology edits are stored, not hardcoded, so a name/SSID/area/size change in
// the dashboard survives the next deploy.

[<CLIMutable>]
type OverrideDto = {
    nodeKey : string
    label   : string
    area    : string
    link    : string
    size    : Nullable<int>
    acInput : Nullable<bool>
    kind    : string
    powerFrom : string
    lan       : string
    altFrom   : string
    position  : string
    feedMode  : string
    powerEnt  : string
    outputEnt : string
    device    : string
    socEnt    : string
}

let private saveNode (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        let! dto = Request.getJson<OverrideDto> ctx
        if String.IsNullOrWhiteSpace dto.nodeKey then
            return! (Response.withStatusCode 400 >> Response.ofJson {| error = "nodeKey required" |}) ctx
        else
            let opt (v: string) = if isNull v then None else Some v
            svc.SaveOverride
                { nodeKey = dto.nodeKey
                  label   = opt dto.label
                  area    = opt dto.area
                  link    = opt dto.link
                  size    = Option.ofNullable dto.size
                  acInput = Option.ofNullable dto.acInput
                  kind    = opt dto.kind
                  powerFrom = opt dto.powerFrom
                  lan       = opt dto.lan
                  altFrom   = opt dto.altFrom
                  position  = opt dto.position
                  feedMode  = opt dto.feedMode
                  powerEnt  = opt dto.powerEnt
                  outputEnt = opt dto.outputEnt
                  device    = opt dto.device
                  socEnt    = opt dto.socEnt }
            return! Response.ofJson {| ok = true |} ctx
    }

[<CLIMutable>]
type EdgeDto = { child : string; parent : string; kind : string; note : string }

let private addEdge (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        let! dto = Request.getJson<EdgeDto> ctx
        svc.AddEdge(dto.child, dto.parent, dto.kind, (if isNull dto.note then None else Some dto.note))
        return! Response.ofJson {| ok = true |} ctx
    }

let private deleteEdge (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        let! dto = Request.getJson<EdgeDto> ctx
        svc.RemoveEdge(dto.child, dto.parent, dto.kind)
        return! Response.ofJson {| ok = true |} ctx
    }

[<CLIMutable>]
type PosDto = { key : string; x : float; y : float }

[<CLIMutable>]
type NewNodeDto = {
    key       : string
    label     : string
    kind      : string
    area      : string
    device    : string
    entity    : string
    powerFrom : string
    lan       : string
    size      : Nullable<int>
}

let private addNode (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        let! dto = Request.getJson<NewNodeDto> ctx
        let opt (v: string) = if String.IsNullOrWhiteSpace v then None else Some v
        if String.IsNullOrWhiteSpace dto.key || String.IsNullOrWhiteSpace dto.label then
            return! (Response.withStatusCode 400 >> Response.ofJson {| error = "key and label required" |}) ctx
        else
            svc.AddNode(dto.key, dto.label,
                        (if String.IsNullOrWhiteSpace dto.kind then "device" else dto.kind),
                        opt dto.area, opt dto.device, opt dto.entity,
                        opt dto.powerFrom, opt dto.lan,
                        (if dto.size.HasValue then dto.size.Value else 2))
            return! Response.ofJson {| ok = true |} ctx
    }

[<CLIMutable>]
type KeyDto = { key : string }

[<CLIMutable>]
type SwitchDto = { key : string; on : bool }

/// Turn a real smart switch on or off. Only nodes that declare an
/// `outputEntity` in the switch domain can be operated - everything else is a
/// modelled position, not something HA can actuate.
let private operateSwitch (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        try
            let! dto = Request.getJson<SwitchDto> ctx
            match svc.OperateSwitch(dto.key, dto.on) with
            | Result.Ok entity ->
                return! Response.ofJson {| ok = true; entity = entity |} ctx
            | Result.Error msg ->
                return! (Response.withStatusCode 400 >> Response.ofJson {| error = msg |}) ctx
        with ex ->
            return! (Response.withStatusCode 400 >> Response.ofJson {| error = ex.Message |}) ctx
    }

[<CLIMutable>]
type RenameDto = { oldKey : string; newKey : string }

let private renameNode (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        try
            let! dto = Request.getJson<RenameDto> ctx
            if String.IsNullOrWhiteSpace dto.oldKey || String.IsNullOrWhiteSpace dto.newKey then
                return! (Response.withStatusCode 400 >> Response.ofJson {| error = "oldKey and newKey required" |}) ctx
            elif dto.oldKey = dto.newKey then
                return! Response.ofJson {| ok = true; unchanged = true |} ctx
            else
                svc.RenameNode(dto.oldKey, dto.newKey)
                return! Response.ofJson {| ok = true |} ctx
        with ex ->
            return! (Response.withStatusCode 400 >> Response.ofJson {| error = ex.Message |}) ctx
    }

[<CLIMutable>]
type NoteDto = { key : string; note : string }

let private saveNote (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        try
            let! dto = Request.getJson<NoteDto> ctx
            // A missing key arrives as null and SQLite rejects the unbound
            // parameter with "Value must be set" - a 500 for what is a bad request.
            if String.IsNullOrWhiteSpace dto.key then
                return! (Response.withStatusCode 400 >> Response.ofJson {| error = "key required" |}) ctx
            else
                svc.SaveNote(dto.key, (if isNull dto.note then "" else dto.note))
                return! Response.ofJson {| ok = true |} ctx
        with ex ->
            return! (Response.withStatusCode 400 >> Response.ofJson {| error = ex.Message |}) ctx
    }

let private deleteNode (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        let! dto = Request.getJson<KeyDto> ctx
        svc.DeleteNode dto.key
        return! Response.ofJson {| ok = true |} ctx
    }

let private savePositions (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        try
            let! ps = Request.getJson<PosDto[]> ctx
            // Reject non-finite coordinates rather than 500: a client-side NaN
            // used to take this endpoint down entirely.
            let good =
                ps
                |> Array.filter (fun p ->
                    not (String.IsNullOrWhiteSpace p.key)
                    && Double.IsFinite p.x && Double.IsFinite p.y)
                |> Array.toList
                |> List.map (fun p -> p.key, p.x, p.y)
            svc.SavePositions good
            return! Response.ofJson {| ok = true; saved = List.length good; rejected = ps.Length - List.length good |} ctx
        with ex ->
            return! (Response.withStatusCode 400 >> Response.ofJson {| error = ex.Message |}) ctx
    }

// ── Routes ───────────────────────────────────────────────────────────────────

let routes (svc: HealthService) : HttpEndpoint list = [
    get "/api/health/devices"  (getDevices  svc)
    get "/api/health/entities" (getEntities svc)
    get  "/api/health/topology"  (getTopology   svc)
    post "/api/health/node"      (saveNode      svc)
    post "/api/health/edge"      (addEdge       svc)
    post "/api/health/edge/del"  (deleteEdge    svc)
    post "/api/health/positions" (savePositions svc)
    post "/api/health/node/new"  (addNode       svc)
    post "/api/health/node/del"  (deleteNode    svc)
    post "/api/health/note"      (saveNote      svc)
    post "/api/health/node/rename" (renameNode  svc)
    post "/api/health/switch"      (operateSwitch svc)
]
