module Health.Devices

open System
open Health

// ── Device-level rollup ──────────────────────────────────────────────────────
// You care about devices, not entities. This system has 1802 entities across
// 188 devices - the RainMachine alone has 39, the AC500s 37 each, EG4 batteries
// 34 each. A per-entity dashboard would be unreadable and would report one dead
// device 39 times.
//
// Rollup rule: a device is as healthy as its BEST entity. If anything on it is
// still reporting, the device is alive - a single stale sub-sensor is normal
// (many devices expose sensors that only update on an event). A device is only
// faulted when everything on it has stopped.

type DeviceHealth = {
    deviceId    : string
    name        : string
    area        : string option
    state       : HealthState
    entityCount : int
    liveCount   : int
    worstAge    : TimeSpan option   // longest time since any monitored entity reported
    entities    : string list       // the monitored ones, for drill-down
}

/// Entities that belong to no HA device are helpers, template sensors, alerts
/// and integration-level rows - not physical things. They are still monitored,
/// but the dashboard should not present them as devices.
let isSynthetic (deviceId: string) = deviceId.Contains "."

/// Roll per-entity monitors up to one row per device.
/// `deviceOf` maps entity id -> (deviceId, deviceName, area).
///
/// Grouping is by NAME, not device id: Bermuda registers its own HA device for
/// a thing the native integration already registered, so one physical device can
/// appear twice (e.g. SmartShunt HQ2451Z4HJ3 exists as both the Victron BLE
/// device and a Bermuda tracker). Merging on name collapses those. Entities with
/// no device keep their own id so nothing is silently dropped.
let rollup
    (deviceOf : string -> (string * string * string option) option)
    (monitors : Monitor seq)
    : DeviceHealth list =

    monitors
    |> Seq.groupBy (fun m ->
        match deviceOf m.entityId with
        | Some (_, name, area) -> name, name, area     // key on name to merge duplicates
        | None                 -> m.entityId, m.entityId, None)
    |> Seq.map (fun ((id, name, area), ms) ->
        let ms      = List.ofSeq ms
        let states  = ms |> List.map (fun m -> m.lastState)
        let live    = states |> List.filter (fun s -> not s.isFault) |> List.length

        // Best-of: Ok beats Stale beats Unavailable. Retired only if everything is.
        let state =
            if   states |> List.exists (fun s -> s = Ok)                              then Ok
            elif states |> List.forall (fun s -> match s with Retired _ -> true | _ -> false) then
                states |> List.pick (fun s -> match s with Retired t -> Some (Retired t) | _ -> None)
            elif states |> List.exists (fun s -> match s with Stale _ -> true | _ -> false) then
                states |> List.pick (fun s -> match s with Stale (a, t) -> Some (Stale (a, t)) | _ -> None)
            elif states |> List.exists (fun s -> match s with Unavailable _ -> true | _ -> false) then
                states |> List.pick (fun s -> match s with Unavailable t -> Some (Unavailable t) | _ -> None)
            else Warmup

        let worstAge =
            ms |> List.choose (fun m -> match m.lastState with Stale (age, _) -> Some age | _ -> None)
               |> function [] -> None | xs -> Some (List.max xs)

        { deviceId    = id
          name        = name
          area        = area
          state       = state
          entityCount = List.length ms
          liveCount   = live
          worstAge    = worstAge
          entities    = ms |> List.map (fun m -> m.entityId) |> List.sort })
    |> Seq.sortBy (fun d ->
        // faults first, then by name
        let rank = match d.state with
                   | Unavailable _ -> 0
                   | Stale _       -> 1
                   | Warmup        -> 2
                   | Retired _     -> 4
                   | Ok            -> 3
        rank, d.name)
    |> List.ofSeq
