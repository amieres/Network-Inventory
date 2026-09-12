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

// ── Routes ───────────────────────────────────────────────────────────────────

let routes (svc: HealthService) : HttpEndpoint list = [
    get "/api/health/devices"  (getDevices  svc)
    get "/api/health/entities" (getEntities svc)
]
