module Inventory.EeroClient

open System
open System.Net.Http
open System.Text.Json
open Microsoft.Extensions.Logging

// ── Eero device data (pulled from direct Eero cloud API) ────────────────────
// Uses api-user.e2ro.com with a session cookie from email-verified login.
// Returns all devices on the network with accurate, real-time IP/MAC/band data.

[<CLIMutable>]
type EeroDevice = {
    mac          : string
    ip           : string
    hostname     : string
    connType     : string   // "wired" | "wireless"
    band         : string   // "2.4 GHz" | "5 GHz" | "6 GHz" | ""
    connected    : bool
    manufacturer : string
    connectedTo  : string   // eero node name, e.g. "Hallway"
    signalDbm    : int      // e.g. -43; 0 for wired
    ssid         : string
}

// ── Query Eero cloud API ────────────────────────────────────────────────────

let getDevices (log: ILogger) (http: HttpClient) (cfg: InventoryConfig) : Async<EeroDevice list> =
    async {
        if not cfg.Eero.Enabled || cfg.Eero.SessionCookie = "" || cfg.Eero.NetworkId = "" then
            log.LogDebug("EERO: disabled or missing session/network config, skipping")
            return []
        else
            try
                let url = $"https://api-user.e2ro.com/2.2/networks/{cfg.Eero.NetworkId}/devices"
                use req = new HttpRequestMessage(HttpMethod.Get, url)
                req.Headers.Add("Cookie", $"s={cfg.Eero.SessionCookie}")
                use! resp = http.SendAsync(req) |> Async.AwaitTask
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

                if not resp.IsSuccessStatusCode then
                    log.LogWarning("EERO: HTTP {code} — session may have expired", int resp.StatusCode)
                    return []
                else

                use doc = JsonDocument.Parse(body)

                let str (el: JsonElement) (name: string) =
                    let mutable v = Unchecked.defaultof<JsonElement>
                    if el.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.String
                    then v.GetString() else ""

                let strNested (el: JsonElement) (parent: string) (name: string) =
                    let mutable p = Unchecked.defaultof<JsonElement>
                    if el.TryGetProperty(parent, &p) then str p name else ""

                let intFromSignal (s: string) =
                    // "−43 dBm" → -43
                    let cleaned = s.Replace(" dBm", "").Replace("−", "-").Replace("\u2212", "-").Trim()
                    match Int32.TryParse(cleaned) with
                    | true, v -> v
                    | _ -> 0

                let devices =
                    [ let mutable dataEl = Unchecked.defaultof<JsonElement>
                      if doc.RootElement.TryGetProperty("data", &dataEl) then
                          for el in dataEl.EnumerateArray() do
                              let ip  = str el "ip"
                              let mac = str el "mac"
                              if ip <> "" && mac <> "" then
                                  let freq     = strNested el "interface" "frequency"
                                  let freqUnit = strNested el "interface" "frequency_unit"
                                  let band =
                                      match freq, freqUnit with
                                      | f, u when f <> "" && u <> "" -> $"{f} {u}"  // "2.4 GHz", "5 GHz", "6 GHz"
                                      | _ -> ""
                                  let connectedEl =
                                      let mutable v = Unchecked.defaultof<JsonElement>
                                      if el.TryGetProperty("connected", &v) && v.ValueKind = JsonValueKind.True then true
                                      else false
                                  yield {
                                      mac          = mac.ToUpperInvariant()
                                      ip           = ip
                                      hostname     = str el "hostname"
                                      connType     = str el "connection_type"
                                      band         = band
                                      connected    = connectedEl
                                      manufacturer = str el "manufacturer"
                                      connectedTo  = strNested el "source" "location"
                                      signalDbm    = intFromSignal (strNested el "connectivity" "signal")
                                      ssid         = str el "ssid"
                                  } ]

                let connected = devices |> List.filter (fun d -> d.connected) |> List.length
                log.LogInformation("EERO: {n} devices ({c} connected)", devices.Length, connected)
                return devices
            with ex ->
                log.LogWarning("EERO: fetch failed: {msg}", ex.Message)
                return []
    }
