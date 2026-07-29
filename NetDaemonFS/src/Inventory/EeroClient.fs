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
    mac         : string
    ip          : string
    connType    : string   // "wired" | "wireless"
    band        : string   // "2.4 GHz" | "5 GHz" | "6 GHz" | ""
    connected   : bool
    rawAttrs    : Map<string, string>
}

// ── Query Eero cloud API ────────────────────────────────────────────────────

let getDevices (log: ILogger) (http: HttpClient) (cfg: InventoryConfig) (debug: bool) : Async<EeroDevice list> =
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

                let scalarStr (el: JsonElement) =
                    match el.ValueKind with
                    | JsonValueKind.String -> let s = el.GetString() in if s <> "" then Some s else None
                    | JsonValueKind.Number -> Some (el.ToString())
                    | JsonValueKind.True   -> Some "true"
                    | JsonValueKind.False  -> Some "false"
                    | _                    -> None

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
                                      | f, u when f <> "" && u <> "" -> $"{f} {u}"
                                      | _ -> ""
                                  let connected =
                                      let mutable v = Unchecked.defaultof<JsonElement>
                                      el.TryGetProperty("connected", &v) && v.ValueKind = JsonValueKind.True

                                  // Generic scalar capture: top-level + one level of nested objects
                                  let rawAttrs =
                                      [ for p in el.EnumerateObject() do
                                            match scalarStr p.Value with
                                            | Some s -> yield $"eero.{p.Name}", s
                                            | None   ->
                                                if p.Value.ValueKind = JsonValueKind.Object then
                                                    for child in p.Value.EnumerateObject() do
                                                        match scalarStr child.Value with
                                                        | Some s -> yield $"eero.{p.Name}.{child.Name}", s
                                                        | None   -> ()
                                        if band <> "" then yield "eero.band", band ]
                                      |> Map.ofList

                                  yield {
                                      mac      = mac.ToUpperInvariant()
                                      ip       = ip
                                      connType = str el "connection_type"
                                      band     = band
                                      connected = connected
                                      rawAttrs = rawAttrs
                                  } ]

                let connected = devices |> List.filter (fun d -> d.connected) |> List.length
                log.LogInformation("EERO: {n} devices ({c} connected)", devices.Length, connected)
                if debug then
                    let mutable dataEl2 = Unchecked.defaultof<JsonElement>
                    if doc.RootElement.TryGetProperty("data", &dataEl2) then
                        dataEl2.EnumerateArray()
                        |> Seq.tryFind (fun el ->
                            let mutable v = Unchecked.defaultof<JsonElement>
                            el.TryGetProperty("connected", &v) && v.ValueKind = JsonValueKind.True)
                        |> Option.iter (fun el ->
                            let keys = el.EnumerateObject() |> Seq.map (fun p -> $"{p.Name}={p.Value}") |> String.concat " | "
                            log.LogInformation("EERO DEBUG first connected device: {keys}", keys))
                return devices
            with ex ->
                log.LogWarning("EERO: fetch failed: {msg}", ex.Message)
                return []
    }
