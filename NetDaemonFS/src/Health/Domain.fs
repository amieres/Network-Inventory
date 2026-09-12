namespace Health

open System

// ── Monitorability class ─────────────────────────────────────────────────────
// Decided per entity by learning its reporting cadence from HA history.
// Measured over 120 sampled entities on 2026-09-12: only ~28% are Periodic or
// Bursty. Applying staleness detection to everything would produce constant
// false alarms, so the classifier is what makes this usable at all.
//   Periodic : steady heartbeat (p95 gap <= 5 min, p95/median < 20) - monitor
//   Bursty   : irregular but bounded (p95 gap <= 1 h)               - monitor, wide threshold
//   Event    : changes only when reality changes (p95 gap > 1 h)    - do not monitor
//   Sparse   : too few samples to learn anything                    - do not monitor
type Cadence =
    | Periodic of p95 : float * median : float   // seconds
    | Bursty   of p95 : float * median : float
    | Event    of p95 : float
    | Sparse   of points : int
    with
        member this.isMonitorable =
            match this with
            | Periodic _ | Bursty _ -> true
            | Event    _ | Sparse _ -> false
        member this.label =
            match this with
            | Periodic _ -> "periodic"
            | Bursty   _ -> "bursty"
            | Event    _ -> "event"
            | Sparse   _ -> "sparse"

// ── Health state ─────────────────────────────────────────────────────────────
// Ok         : reporting within its learned threshold
// Stale      : exceeded the threshold - stopped reporting without saying so.
//              This is the EG4-ESP32 failure mode: 25 stalls on 2026-09-11 with
//              zero `unavailable` states, invisible to both ping and unavailable-checks.
// Unavailable: HA itself says unavailable/unknown (RainMachine class)
// Warmup     : too new to have a learned threshold yet
// Retired    : faulted for so long that it is no longer news. Devices get fixed,
//              broken, moved, shelved for a season, or repurposed; a permanently
//              dead entity that alarms forever just trains you to ignore the
//              monitor. Retired entities stay listed but stop alerting, and are
//              re-adopted automatically if they ever report again.
type HealthState =
    | Ok
    | Stale       of age : TimeSpan * threshold : TimeSpan
    | Unavailable of since : DateTimeOffset option
    | Warmup
    | Retired     of since : DateTimeOffset
    with
        member this.isFault = match this with Stale _ | Unavailable _ -> true | _ -> false
        member this.label =
            match this with
            | Ok            -> "ok"
            | Stale _       -> "stale"
            | Unavailable _ -> "unavailable"
            | Warmup        -> "warmup"
            | Retired _     -> "retired"

// ── Why an entity is being watched ───────────────────────────────────────────
// Staleness  : it has a learned cadence, so "stopped updating" is detectable.
// Liveness   : no usable cadence, but it WAS reporting a real value when we
//              learned it - so a later transition to unavailable is a fault.
//              This is the RainMachine class: all 39 entities went straight to
//              `unavailable` on 2026-08-25 and never came back. Such entities are
//              invisible to staleness (no cadence to learn) but very much broken.
// An entity already unavailable at learn time gets NO watch at all - it is
// absent, not failing, and would otherwise alarm forever (125 entities are
// currently unavailable, many of them phones and cars that are simply away).
type Watch =
    | Staleness of threshold : TimeSpan
    | Liveness
    with
        member this.label = match this with Staleness _ -> "staleness" | Liveness -> "liveness"

// ── Per-entity monitor record ────────────────────────────────────────────────
// watch is derived from cadence + observed state (see Rules.watchFor) and stored
// so a restart doesn't lose it; learnedAt drives periodic re-learning.
type Monitor = {
    entityId    : string
    cadence     : Cadence
    watch       : Watch
    learnedAt   : DateTimeOffset
    lastState   : HealthState
    lastChanged : DateTimeOffset
    faultCount  : int              // consecutive checks in a fault state (debounce)
}

// ── A fault worth reporting ──────────────────────────────────────────────────
type Fault = {
    entityId  : string
    state     : HealthState
    detectedAt: DateTimeOffset
    lastSeen  : DateTimeOffset option
}
