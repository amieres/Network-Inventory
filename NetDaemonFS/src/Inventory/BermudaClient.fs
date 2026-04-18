module Inventory.BermudaClient

open System
open System.Net.Http
open System.Text
open System.Text.Json
open Microsoft.Extensions.Logging

// ── BERMUDA BLE Trilateration client ─────────────────────────────────────────
// Calls the HA bermuda.dump_devices service via the Supervisor REST API.
// Requires SUPERVISOR_TOKEN env var (available inside any HA add-on).

type BleDevice = {
    mac            : string      // upper-case colon-separated MAC, e.g. "C8:38:30:34:68:51"; "" if not a MAC
    irk            : string      // lower-case 32-char hex IRK key for Private BLE Devices; "" otherwise
    name           : string      // best available name; may be empty
    isHome         : bool        // zone = "home" → currently visible
    lastSeen       : DateTimeOffset option // when Bermuda last received a BLE advertisement (absolute)
    rawAttrs       : Map<string, string>   // all scalar Bermuda fields, keyed as "bermuda.<field>"
}

let private validMac (s: string) =
    s.Length = 17 && s.[2] = ':' && s.[5] = ':' && s.[8] = ':' && s.[11] = ':' && s.[14] = ':'

// 32 lower/upper hex chars, no separators — Bermuda's encoding of the 128-bit IRK
let private validIrk (s: string) =
    s.Length = 32 && s |> Seq.forall (fun c ->
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))

let fetchDevices (log: ILogger) (http: HttpClient) : Async<BleDevice list> =
    async {
        let token = Environment.GetEnvironmentVariable("SUPERVISOR_TOKEN")
        if String.IsNullOrEmpty(token) then
            log.LogDebug("BERMUDA: SUPERVISOR_TOKEN not set, skipping BLE scan")
            return []
        else
            try
                let url = "http://supervisor/core/api/services/bermuda/dump_devices?return_response"
                use req = new HttpRequestMessage(HttpMethod.Post, url)
                req.Headers.Add("Authorization", $"Bearer {token}")
                req.Content <- new StringContent("{}", Encoding.UTF8, "application/json")
                use! resp = http.SendAsync(req) |> Async.AwaitTask
                let! body = resp.Content.ReadAsStringAsync() |> Async.AwaitTask

                use doc = JsonDocument.Parse(body)
                let root = doc.RootElement
                let mutable svcResp = Unchecked.defaultof<JsonElement>
                if not (root.TryGetProperty("service_response", &svcResp)) then
                    log.LogWarning("BERMUDA: no service_response; body={b}",
                        (if body.Length <= 200 then body else body.[..199]))
                    return []
                else
                    let str (el: JsonElement) (name: string) =
                        let mutable p = Unchecked.defaultof<JsonElement>
                        if el.TryGetProperty(name, &p) && p.ValueKind = JsonValueKind.String
                        then p.GetString() else ""
                    let boolProp (el: JsonElement) (name: string) =
                        let mutable p = Unchecked.defaultof<JsonElement>
                        el.TryGetProperty(name, &p) && p.ValueKind = JsonValueKind.True

                    let intProp (el: JsonElement) (name: string) =
                        let mutable p = Unchecked.defaultof<JsonElement>
                        if el.TryGetProperty(name, &p) && p.ValueKind = JsonValueKind.Number
                        then Some (p.GetInt32()) else None
                    let floatProp (el: JsonElement) (name: string) =
                        let mutable p = Unchecked.defaultof<JsonElement>
                        if el.TryGetProperty(name, &p) && p.ValueKind = JsonValueKind.Number
                        then Some (p.GetDouble()) else None
                    let strOpt (el: JsonElement) (name: string) =
                        let s = str el name
                        if s <> "" then Some s else None

                    // Pass 1: collect monotonic last_seen values (seconds since boot).
                    // The maximum value ≈ "just now", so we can derive absolute timestamps:
                    //   absoluteTime = now - (maxMono - deviceMono)
                    let monoMap =
                        [ for prop in svcResp.EnumerateObject() do
                            let mutable lsEl = Unchecked.defaultof<JsonElement>
                            if prop.Value.TryGetProperty("last_seen", &lsEl) &&
                               lsEl.ValueKind = JsonValueKind.Number then
                                let v = lsEl.GetDouble()
                                if v > 0.0 then yield prop.Name, v ]
                        |> Map.ofList
                    let maxMono =
                        monoMap |> Map.values |> (fun vs -> if Seq.isEmpty vs then 0.0 else Seq.max vs)
                    let now = DateTimeOffset.UtcNow
                    let monoToAbs (key: string) : DateTimeOffset option =
                        match monoMap |> Map.tryFind key with
                        | Some mono when maxMono > 0.0 -> Some (now - TimeSpan.FromSeconds(maxMono - mono))
                        | _ -> None

                    let mutable totalRaw = 0
                    let mutable skippedScanner  = 0
                    let mutable skippedNoIdent  = 0

                    let devices =
                        [ for prop in svcResp.EnumerateObject() do
                            totalRaw <- totalRaw + 1
                            let d = prop.Value
                            if boolProp d "_is_scanner" || boolProp d "_is_remote_scanner" then
                                skippedScanner <- skippedScanner + 1
                            else
                                let rawAddr = str d "address"
                                let macAddr = if validMac rawAddr then rawAddr.ToUpperInvariant() else ""
                                let irkKey  = if macAddr = "" && validIrk rawAddr then rawAddr.ToLowerInvariant() else ""
                                let name =
                                    let n = str d "name"
                                    if n <> "" then n else str d "name_bt_local_name"

                                // Accept if we have a valid MAC, IRK key, or a non-empty name (iBeacon devices)
                                if macAddr = "" && irkKey = "" && name = "" then
                                    skippedNoIdent <- skippedNoIdent + 1
                                    log.LogDebug("BERMUDA: skipping entry '{key}' — no valid MAC/IRK and no name (address={addr})",
                                        prop.Name, rawAddr)
                                else
                                    if macAddr = "" then
                                        log.LogDebug("BERMUDA: non-MAC entry '{key}' name='{name}' irk={irk} address='{addr}'",
                                            prop.Name, name, irkKey, rawAddr)

                                    // Nearest scanner: prefer direct field, else derive from scanner_list
                                    let nearestScanner =
                                        match strOpt d "nearest_distance_scanner" with
                                        | Some _ as s -> s
                                        | None ->
                                            let mutable sl = Unchecked.defaultof<JsonElement>
                                            if d.TryGetProperty("scanner_list", &sl) &&
                                               sl.ValueKind = JsonValueKind.Object then
                                                sl.EnumerateObject()
                                                |> Seq.choose (fun sp ->
                                                    let mutable rEl = Unchecked.defaultof<JsonElement>
                                                    if sp.Value.TryGetProperty("rssi", &rEl) &&
                                                       rEl.ValueKind = JsonValueKind.Number
                                                    then Some (sp.Name, rEl.GetInt32())
                                                    else None)
                                                |> Seq.sortByDescending snd
                                                |> Seq.tryHead
                                                |> Option.map fst
                                            else None

                                    let absLastSeen = monoToAbs prop.Name

                                    // Collect all scalar fields generically; skip internal/complex ones
                                    let rawAttrs =
                                        [ for p in d.EnumerateObject() do
                                            if not (p.Name.StartsWith("_")) then
                                                let v =
                                                    match p.Value.ValueKind with
                                                    | JsonValueKind.String -> Some (p.Value.GetString())
                                                    | JsonValueKind.Number -> Some (p.Value.ToString())
                                                    | JsonValueKind.True   -> Some "true"
                                                    | JsonValueKind.False  -> Some "false"
                                                    | _                    -> None  // skip objects/arrays/null
                                                match v with
                                                | Some s -> yield $"bermuda.{p.Name}", s
                                                | None   -> () ]
                                        |> Map.ofList
                                        // Replace raw monotonic last_seen with computed absolute timestamp
                                        |> (fun m ->
                                            match absLastSeen with
                                            | Some dt -> m |> Map.add "bermuda.last_seen" (dt.ToString("o"))
                                            | None    -> m |> Map.remove "bermuda.last_seen")

                                    yield {
                                        mac      = macAddr
                                        irk      = irkKey
                                        name     = name
                                        isHome   = str d "zone" = "home"
                                        lastSeen = absLastSeen
                                        rawAttrs = rawAttrs
                                    } ]

                    let home = devices |> List.filter (fun d -> d.isHome) |> List.length
                    log.LogInformation(
                        "BERMUDA: {raw} raw entries — {sc} scanners, {ni} no-ident, {n} devices ({h} home)",
                        totalRaw, skippedScanner, skippedNoIdent, devices.Length, home)
                    return devices
            with ex ->
                log.LogWarning("BERMUDA: fetch failed: {msg}", ex.Message)
                return []
    }
