module Inventory.NetgearClient

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging

// ── NETGEAR RAX80 browser-mode client ────────────────────────────────────────
// Auth flow (IP-based session, challenge-response):
//
//   1. POST /ajax/devices_table_result     → if 401, body contains form:
//                                            <form action="unauth.cgi?id={nonce}">
//   2. POST /unauth.cgi?id={nonce}         → empty body + Basic Auth header
//                                            establishes IP session (HTTP 200)
//   3. POST /ajax/devices_table_result     → JSON device list

type NetgearDevice = {
    ip        : string
    name      : string
    mac       : string
    connType  : string   // "Wired" | "2.4G Wireless" | "5G Wireless"
    ssid      : string
    signalDbm : int
}

// ── Helpers ───────────────────────────────────────────────────────────────────

let private chromeUA =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"

let private basicAuth (password: string) =
    let creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{password}"))
    $"Basic {creds}"

let private makePost (http: HttpClient) (auth: string) (url: string) (body: string) =
    async {
        use req = new HttpRequestMessage(HttpMethod.Post, url)
        req.Headers.Add("Authorization", auth)
        req.Headers.Add("User-Agent",    chromeUA)
        use content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded")
        req.Content <- content
        use! r = http.SendAsync(req) |> Async.AwaitTask
        let! b = r.Content.ReadAsStringAsync() |> Async.AwaitTask
        return r, b
    }

// ── Parse JSON device list ────────────────────────────────────────────────────

let private parseDevices (log: ILogger) (json: string) =
    use doc  = JsonDocument.Parse(json)
    let root = doc.RootElement
    let mutable arr = Unchecked.defaultof<JsonElement>
    if not (root.TryGetProperty("connDevices", &arr)) then
        log.LogDebug("NETGEAR: 'connDevices' missing; keys = {k}",
            root.EnumerateObject() |> Seq.map (fun p -> p.Name) |> String.concat ", ")
        []
    else
        let s (el: JsonElement) (name: string) =
            let mutable p = Unchecked.defaultof<JsonElement>
            if el.TryGetProperty(name, &p) && p.ValueKind = JsonValueKind.String
            then p.GetString()
            else ""
        let intOr (el: JsonElement) (name: string) =
            let mutable p = Unchecked.defaultof<JsonElement>
            if el.TryGetProperty(name, &p) then
                match p.ValueKind with
                | JsonValueKind.Number -> (let ok, v = p.TryGetInt32() in if ok then v else 0)
                | JsonValueKind.String -> (let ok, v = Int32.TryParse(p.GetString()) in if ok then v else 0)
                | _ -> 0
            else 0
        [ for el in arr.EnumerateArray() ->
            { ip       = s el "ip"
              name     = WebUtility.HtmlDecode(s el "name")
              mac      = (s el "mac").ToUpperInvariant()
              connType = s el "connection"
              ssid     = s el "ssid"
              signalDbm = intOr el "signal" } ]

// ── Public API ────────────────────────────────────────────────────────────────

let fetchAll
    (log      : ILogger)
    (http     : HttpClient)
    (host     : string)
    (password : string)
    : Async<NetgearDevice list> =
    async {
        try
            let auth    = basicAuth password
            let ajaxUrl = host + "/ajax/devices_table_result"

            // Attempt 1: POST AJAX directly
            let! resp1, body1 = makePost http auth ajaxUrl ""
            log.LogDebug("NETGEAR: attempt 1 → HTTP {code}", int resp1.StatusCode)

            // If challenged with 401, do the unauth handshake then retry
            let! resp, json =
                if int resp1.StatusCode <> 401 then
                    async.Return (resp1, body1)
                else
                    async {
                        // 401 body: <form method="post" action="unauth.cgi?id={nonce}" ...></form>
                        // Browser auto-submits the empty form; we do the same.
                        let m = Regex.Match(body1, """action="([^"]+)""")
                        if not m.Success then
                            log.LogWarning("NETGEAR: 401 but no form action found")
                            return resp1, body1
                        else
                            let actionRel = m.Groups.[1].Value    // e.g. "unauth.cgi?id=abc..."
                            let actionUrl = host + "/" + actionRel
                            log.LogDebug("NETGEAR: challenge → POST {cgi}", actionRel.Split('?').[0])
                            // POST empty body; Basic Auth header is what establishes the IP session
                            let! r2, _ = makePost http auth actionUrl ""
                            log.LogDebug("NETGEAR: handshake → HTTP {code}", int r2.StatusCode)
                            // Attempt 2: retry AJAX — should succeed now
                            return! makePost http auth ajaxUrl ""
                    }

            log.LogDebug("NETGEAR: final → HTTP {code}, {n} bytes", int resp.StatusCode, json.Length)

            if int resp.StatusCode <> 200 then
                log.LogWarning("NETGEAR: unexpected final status {code}", int resp.StatusCode)
                return []
            else
                let devices = parseDevices log json
                log.LogInformation("NETGEAR: {n} devices found", devices.Length)
                return devices
        with ex ->
            log.LogWarning("NETGEAR: fetchAll failed: {msg}", ex.Message)
            return []
    }
