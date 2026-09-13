module Health.Rules

open System
open Health

// ── Entity filter ────────────────────────────────────────────────────────────
// Domains that never carry a reporting cadence, plus browser_mod (dead browser
// sessions: 131 permanently-unavailable entities that would otherwise dominate).
let private excludedDomains =
    set [ "automation"; "script"; "scene"; "person"; "zone"; "sun"; "tts"
          "conversation"; "todo"; "update"; "button"; "input_boolean"
          "input_number"; "input_select"; "input_text"; "input_datetime" ]

let isCandidate (entityId: string) =
    let domain = entityId.Split('.').[0]
    not (excludedDomains.Contains domain) && not (entityId.Contains "browser_mod")

// ── Orphan detection ─────────────────────────────────────────────────────────
// An entity left behind by an integration that no longer runs is debris, not a
// fault. Real example: the Emporia Vue 2 was reflashed from stock firmware to
// ESPHome, so its 42 `emporia_vue` entities are permanently unavailable while
// the device itself is perfectly healthy and reporting through `abevue2_*`.
// Of 244 unavailable entities here, 173 are orphans (131 browser_mod + 42
// emporia_vue) - flagging them as faults would bury every real problem.
//
// Heuristic: every entity belonging to an integration is unavailable AND none
// has ever reported in the learn window. A live integration virtually always has
// at least one entity doing something.
let orphanPlatforms = set [ "browser_mod"; "emporia_vue" ]

let isOrphanPlatform (platform: string) = orphanPlatforms.Contains platform

// ── Cadence classification ───────────────────────────────────────────────────
// Thresholds calibrated against a 120-entity sample (2026-09-11 → 09-12):
//   55 sparse, 31 event-driven, 24 periodic, 10 bursty.
// Using the p95 gap rather than the max is essential - the max is usually the
// outage you are trying to detect (eg4_battery_1_power: median 5 s, p95 13 s,
// max 228 min, that max being the very stall we want to alarm on).
let minPoints   = 5
let periodicP95 = 300.0     // 5 min
let periodicRatio = 20.0    // p95/median - guards against bursty-but-spiky series
let burstyP95   = 3600.0    // 1 h

let classify (gapsSeconds: float list) : Cadence =
    let n = List.length gapsSeconds
    if n + 1 < minPoints then Sparse (n + 1)
    else
        let sorted = List.sort gapsSeconds
        let arr    = List.toArray sorted
        let pct p  = arr.[min (arr.Length - 1) (int (float arr.Length * p))]
        let p95    = pct 0.95
        let median = pct 0.50
        let ratio  = if median > 0.0 then p95 / median else infinity
        if   p95 <= periodicP95 && ratio < periodicRatio then Periodic (p95, median)
        elif p95 <= burstyP95                            then Bursty   (p95, median)
        else                                                  Event    p95

// ── Diurnal entities ─────────────────────────────────────────────────────────
// Solar sensors legitimately stop changing at night: PV power sits at 0 from
// sunset to sunrise, so "time since last change" makes them look dead every
// single night. Measured 2026-09-13 02:19 local: total_pv_power, total_net_power
// and the AC500 DC-input sensors all last changed at 19:38-19:45 (sunset) and
// were flagged stale - all false.
//
// These entities get a much wider threshold so they only alarm if they are still
// silent well into the day.
let diurnalHints = [ "pv_power"; "pv_energy"; "pv_daily"; "dc_input_power"; "solar"; "net_power" ]

let isDiurnal (entityId: string) =
    let id = entityId.ToLowerInvariant()
    diurnalHints |> List.exists id.Contains

/// Night-time slack for sun-driven sensors: long enough to span a night plus a
/// dull morning, so a genuinely dead PV sensor still surfaces within a day.
let diurnalThreshold = TimeSpan.FromHours 16.0

// ── Threshold derivation ─────────────────────────────────────────────────────
// Multiple of p95, floored so fast sensors don't alarm on a single missed beat.
// eg4_battery_1_power has p95 = 13 s; 4x = 52 s, floored to 3 min. The real
// stalls on 2026-09-11 ran 3-15 min, so a 3 min floor catches them while leaving
// room for ordinary jitter.
let staleMultiplier = 4.0
let minThreshold    = TimeSpan.FromMinutes 3.0
let maxThreshold    = TimeSpan.FromHours   6.0

let thresholdFor (cadence: Cadence) : TimeSpan option =
    match cadence with
    | Periodic (p95, _) | Bursty (p95, _) ->
        let t = TimeSpan.FromSeconds(p95 * staleMultiplier)
        Some (if t < minThreshold then minThreshold elif t > maxThreshold then maxThreshold else t)
    | Event _ | Sparse _ -> None

// ── Status flags ─────────────────────────────────────────────────────────────
// A binary "is it connected / is there a problem" sensor is healthy precisely
// when it does NOT change: `binary_sensor.ac500_connected_2` sat at `on` for
// 19 hours while the AC500 was fine and its power data updated every few
// seconds - yet staleness flagged it, painting two healthy Bluettis red.
//
// These get a LIVENESS watch instead: we care that HA still has a value, not
// that the value keeps moving. Whether the underlying device is really alive is
// answered by its own data sensors, which do have a cadence.
let statusHints =
    [ "_connected"; "_connection"; "_online"; "_status"; "_available"
      "_problem"; "_alarm"; "_fault"; "_restrictions"; "_enabled" ]

let isStatusFlag (entityId: string) =
    let id = entityId.ToLowerInvariant()
    id.StartsWith "binary_sensor." && statusHints |> List.exists id.Contains

/// Threshold for a specific entity: sun-driven sensors get night-time slack.
let thresholdForEntity (entityId: string) (cadence: Cadence) : TimeSpan option =
    thresholdFor cadence
    |> Option.map (fun t -> if isDiurnal entityId && t < diurnalThreshold then diurnalThreshold else t)

// ── What kind of watch (if any) an entity gets ───────────────────────────────
// `currentState` is the entity's state at learn time.
//   already unavailable  -> None. It is absent, not failing. 125 entities are in
//                           this state right now (phones, cars, long-dead kit);
//                           watching them would alarm forever.
//   learnable cadence    -> Staleness. Catches the EG4-ESP32 silent-stall class.
//   no cadence but live  -> Liveness. Catches the RainMachine class, where a
//                           working device goes straight to `unavailable`.
let watchFor (entityId: string) (cadence: Cadence) (currentState: string) : Watch option =
    match currentState with
    | "unavailable" | "unknown" | null -> None
    | _ when isStatusFlag entityId ->
        // Not changing is the HEALTHY state for a status flag.
        Some Liveness
    | _ ->
        match thresholdForEntity entityId cadence with
        | Some t -> Some (Staleness t)
        | None   -> Some Liveness

// ── State evaluation ─────────────────────────────────────────────────────────
// `state` is the raw HA state string; `age` is now - last_updated.
// Unavailable is a fault under BOTH watch kinds - an entity with a cadence that
// goes unavailable is just as broken as one without.
let evaluate (state: string) (age: TimeSpan) (watch: Watch) (lastSeen: DateTimeOffset option) : HealthState =
    match state with
    | "unavailable" | "unknown" -> Unavailable lastSeen
    | _ ->
        match watch with
        | Liveness      -> Ok            // it is reporting something; that is all we can ask
        | Staleness t   -> if age > t then Stale (age, t) else Ok

// ── Fault debounce ───────────────────────────────────────────────────────────
// Require N consecutive fault observations before reporting, so a single slow
// poll or a brief broker hiccup doesn't raise an alert. At a 60 s check interval
// this means a fault is reported ~2 min after it starts.
let faultConfirmations = 2

let shouldReport (m: Monitor) = m.lastState.isFault && m.faultCount >= faultConfirmations
