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

        let isFaulted (key: string) =
            match Topology.nodeByKey |> Map.tryFind key with
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

        let liveNodes = svc.GetNodes()
        let liveEdges = svc.GetEdges()
        let liveByKey = liveNodes |> List.map (fun n -> n.key, n) |> Map.ofList
        let verdicts = Correlate.analyse isFaulted

        // Raw entity states, for output/link indicators.
        let entRaw = svc.GetRawStates()
        // Interfaces from the inventory: one icon per physical interface, so a
        // device with both ethernet and wifi (the Mac Studio) shows both, each
        // with its own MAC and IP, and a BLE-capable device gets a bluetooth icon.
        let deviceIfaces = svc.GetDeviceIfaces()

        // A node has no power when an ancestor power-edge parent is faulted, or
        // when the plug/station feeding it reports its output off.
        let outputOffOf (key: string) =
            Topology.nodeByKey
            |> Map.tryFind key
            |> Option.bind (fun n -> n.outputEntity)
            |> Option.map (fun e ->
                 match entRaw |> Map.tryFind e with
                 | Some v ->
                     let v = v.ToLowerInvariant()
                     v = "off" || v = "0"
                 | None -> false)
            |> Option.defaultValue false

        let poweredOff (key: string) =
            Topology.edges
            |> List.filter (fun e -> e.child = key && e.kind = Power)
            |> List.exists (fun e -> isFaulted e.parent || outputOffOf e.parent)

        let radioDown (key: string) =
            Topology.edges
            |> List.filter (fun e -> e.child = key && (e.kind = Network || e.kind = BtHost))
            |> List.exists (fun e -> isFaulted e.parent)

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
                   remedy    = n.remedy
                   size      = n.size
                   link      = n.link
                   powerFrom = n.powerFrom.label
                   lan       = n.lan.label
                   // One entry per physical interface, straight from the
                   // inventory - the topology does not re-declare MACs or IPs.
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
                              not (v = "off" || v = "0" || v = "unavailable" || v = "unknown")
                          | None -> true)
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
                           kinds = liveNodes |> List.map (fun n -> n.kind) |> List.distinct |> List.sort
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
                  lan       = opt dto.lan }
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

let private deleteNode (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        let! dto = Request.getJson<KeyDto> ctx
        svc.DeleteNode dto.key
        return! Response.ofJson {| ok = true |} ctx
    }

let private savePositions (svc: HealthService) : HttpHandler =
    fun ctx -> task {
        let! ps = Request.getJson<PosDto[]> ctx
        svc.SavePositions(ps |> Array.toList |> List.map (fun p -> p.key, p.x, p.y))
        return! Response.ofJson {| ok = true; saved = ps.Length |} ctx
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
]
