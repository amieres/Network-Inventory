namespace Health

// Bound to appsettings.json["HealthMonitor"] and ["HomeAssistant"].
// Classes (not records) so ASP.NET Options binding can set the properties.

type HealthConfig() =
    /// Master switch - false leaves the hosted service registered but idle.
    member val Enabled          : bool     = true with get, set
    /// How often to evaluate every monitored entity.
    member val CheckIntervalSec : int      = 60   with get, set
    /// How often to re-learn cadences from history (they drift as devices change).
    member val RelearnHours     : int      = 24   with get, set
    /// History window used to learn cadence.
    member val LearnWindowHours : int      = 24   with get, set
    /// Entities per history request - the API returns a lot of points, so batch modestly.
    member val BatchSize        : int      = 25   with get, set
    /// Raise an HA persistent notification when a fault is confirmed.
    member val Notify           : bool     = true with get, set
    /// Substrings; any entity whose id contains one of these is never monitored.
    member val Exclude          : string[] = [||] with get, set
    /// Stop watching an entity that has been faulted this long without recovering.
    /// Devices get retired, repurposed or put away for a season; without this, one
    /// decommissioned device alarms forever and trains you to ignore the monitor.
    /// The entity is re-adopted automatically if it ever starts reporting again.
    member val RetireAfterHours : int      = 48   with get, set
    /// An entity must have been reporting for at least this long before a liveness
    /// watch is armed, so a device seen briefly mid-move isn't treated as permanent.
    member val AdoptAfterHours  : int      = 6    with get, set

/// Mirrors the existing HomeAssistant section so the monitor can call the REST API
/// directly (NetDaemon's IHaContext gives state but not the history endpoint).
///
/// Running as a supervised add-on, the long-lived token in appsettings.json is
/// REJECTED for direct API calls - measured 2026-09-12: websocket 401, history
/// 403 - even though the same token works from outside. Add-ons are expected to
/// use SUPERVISOR_TOKEN against the internal `supervisor/core` endpoint, which is
/// injected into the container environment. Fall back to the configured host and
/// token when that variable is absent (i.e. running outside the add-on).
type HaConnection() =
    member val Host  : string = "localhost" with get, set
    member val Port  : int    = 8123        with get, set
    member val Ssl   : bool   = false       with get, set
    member val Token : string = ""          with get, set

    member _.SupervisorToken =
        match System.Environment.GetEnvironmentVariable "SUPERVISOR_TOKEN" with
        | null | "" -> None
        | t         -> Some t

    /// Token to send, preferring the supervisor's when present.
    member this.ApiToken =
        this.SupervisorToken |> Option.defaultValue this.Token

    member this.BaseUrl =
        match this.SupervisorToken with
        | Some _ -> "http://supervisor/core"       // add-on -> HA core via supervisor proxy
        | None   ->
            let scheme = if this.Ssl then "https" else "http"
            $"{scheme}://{this.Host}:{this.Port}"

    /// Websocket endpoint matching BaseUrl.
    member this.WsUrl =
        match this.SupervisorToken with
        | Some _ -> "ws://supervisor/core/websocket"
        | None   -> this.BaseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/api/websocket"
