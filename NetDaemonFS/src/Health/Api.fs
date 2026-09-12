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
        Response.ofJson
            {| ready    = svc.IsReady
               total    = List.length realRows
               totalAll = List.length rows
               helpers  = (List.length rows) - (List.length realRows)
               ok       = pick "ok"
               stale    = pick "stale"
               unavailable = pick "unavailable"
               warmup   = pick "warmup"
               retired  = pick "retired"
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

        let verdicts = Correlate.analyse isFaulted

        // Raw entity states, for output/link indicators.
        let entRaw = svc.GetRawStates()

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
            Topology.nodes
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
            Topology.edges
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
            Topology.nodes
            |> List.filter (fun n -> n.blindSpot)
            |> List.map (fun n -> {| key = n.key; label = n.label; remedy = n.remedy |})

        // Areas, so the diagram can draw grouping rectangles.
        let areas =
            Topology.nodes
            |> List.choose (fun n -> n.area |> Option.map (fun a -> a, n.key))
            |> List.groupBy fst
            |> List.map (fun (a, xs) -> {| name = a; keys = xs |> List.map snd |})

        Response.ofJson {| nodes = nodesJ; edges = edgesJ; rootCauses = roots
                           blindSpots = blindSpots; areas = areas |} ctx

// ── Routes ───────────────────────────────────────────────────────────────────

let routes (svc: HealthService) : HttpEndpoint list = [
    get "/api/health/devices"  (getDevices  svc)
    get "/api/health/entities" (getEntities svc)
    get "/api/health/topology" (getTopology svc)
]
