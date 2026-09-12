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
      entities    = d.entities }

// ── Handlers ─────────────────────────────────────────────────────────────────

let private getDevices (svc: HealthService) : HttpHandler =
    fun ctx ->
        let rows = svc.GetDeviceHealth() |> List.map project
        let counts =
            rows
            |> List.countBy (fun r -> r.state)
            |> List.map (fun (k, v) -> k, v)
            |> dict
        let pick k = match counts.TryGetValue k with | true, v -> v | _ -> 0
        Response.ofJson
            {| ready    = svc.IsReady
               total    = List.length rows
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
                   affected = affected |})

        let edgesJ =
            Topology.edges
            |> List.map (fun e -> {| child = e.child; parent = e.parent; kind = e.kind.label; note = e.note |})

        let roots =
            Correlate.rootCauses verdicts
            |> List.map (fun (v, affected) -> {| key = v.key; label = v.label; affected = affected |})

        Response.ofJson {| nodes = nodesJ; edges = edgesJ; rootCauses = roots |} ctx

// ── Routes ───────────────────────────────────────────────────────────────────

let routes (svc: HealthService) : HttpEndpoint list = [
    get "/api/health/devices"  (getDevices  svc)
    get "/api/health/entities" (getEntities svc)
    get "/api/health/topology" (getTopology svc)
]
