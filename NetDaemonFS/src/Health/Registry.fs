module Health.Registry

open System
open System.Collections.Generic
open System.Net.WebSockets
open System.Buffers
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.Extensions.Logging

// Entity -> device mapping, plus the integration (platform) each entity belongs to.
// NetDaemon's IHaContext exposes states but not the registries, so this talks to
// the websocket API directly: config/entity_registry/list + config/device_registry/list.

type EntityInfo = {
    entityId : string
    deviceId : string option
    platform : string
}

type DeviceInfo = {
    deviceId : string
    name     : string
    area     : string option
}

type Registries = {
    entities : Map<string, EntityInfo>
    devices  : Map<string, DeviceInfo>
    areas    : Map<string, string>
}

let empty = { entities = Map.empty; devices = Map.empty; areas = Map.empty }

let private send (ws: ClientWebSocket) (payload: string) (ct: CancellationToken) = async {
    let bytes = Encoding.UTF8.GetBytes payload
    do! ws.SendAsync(ArraySegment bytes, WebSocketMessageType.Text, true, ct) |> Async.AwaitTask
}

let private receive (ws: ClientWebSocket) (ct: CancellationToken) = async {
    let buf = ArrayPool<byte>.Shared.Rent (1 <<< 20)
    try
        let sb = StringBuilder()
        let mutable go = true
        while go do
            let! r = ws.ReceiveAsync(ArraySegment buf, ct) |> Async.AwaitTask
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)) |> ignore
            go <- not r.EndOfMessage
        return sb.ToString()
    finally
        ArrayPool<byte>.Shared.Return buf
}

/// Fetch entity+device+area registries. Returns `empty` on any failure - the
/// caller degrades to per-entity rows rather than breaking.
let fetch (log: ILogger) (baseUrl: string) (token: string) : Async<Registries> = async {
    let wsUrl = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/api/websocket"
    use ws  = new ClientWebSocket()
    use cts = new CancellationTokenSource(TimeSpan.FromSeconds 45.0)
    let ct  = cts.Token
    try
        do! ws.ConnectAsync(Uri wsUrl, ct) |> Async.AwaitTask
        let! _auth = receive ws ct                                   // auth_required
        do! send ws (JsonSerializer.Serialize {| ``type`` = "auth"; access_token = token |}) ct
        let! authResult = receive ws ct
        if authResult.Contains "auth_invalid" then
            log.LogWarning("Health: registry auth failed")
            return empty
        else

        let call (id: int) (msgType: string) = async {
            do! send ws (JsonSerializer.Serialize {| id = id; ``type`` = msgType |}) ct
            // Responses can interleave with events; read until our id comes back.
            let mutable result = None
            while result.IsNone do
                let! raw = receive ws ct
                use doc = JsonDocument.Parse raw
                let root = doc.RootElement
                let mutable idEl = Unchecked.defaultof<JsonElement>
                if root.TryGetProperty("id", &idEl) && idEl.GetInt32() = id then
                    let mutable res = Unchecked.defaultof<JsonElement>
                    if root.TryGetProperty("result", &res) then result <- Some (res.Clone())
                    else result <- Some (JsonDocument.Parse("[]").RootElement.Clone())
            return result.Value
        }

        let! entRes  = call 1 "config/entity_registry/list"
        let! devRes  = call 2 "config/device_registry/list"
        let! areaRes = call 3 "config/area_registry/list"

        let str (e: JsonElement) (name: string) =
            let mutable v = Unchecked.defaultof<JsonElement>
            if e.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.String
            then Some (v.GetString()) else None

        let areas =
            areaRes.EnumerateArray()
            |> Seq.choose (fun a ->
                match str a "area_id", str a "name" with
                | Some id, Some n -> Some (id, n)
                | _ -> None)
            |> Map.ofSeq

        let devices =
            devRes.EnumerateArray()
            |> Seq.choose (fun d ->
                match str d "id" with
                | None -> None
                | Some id ->
                    let name =
                        str d "name_by_user"
                        |> Option.orElseWith (fun () -> str d "name")
                        |> Option.defaultValue id
                    let area = str d "area_id" |> Option.bind (fun a -> Map.tryFind a areas)
                    Some (id, { deviceId = id; name = name; area = area }))
            |> Map.ofSeq

        let entities =
            entRes.EnumerateArray()
            |> Seq.choose (fun e ->
                match str e "entity_id" with
                | None -> None
                | Some eid ->
                    Some (eid, { entityId = eid
                                 deviceId = str e "device_id"
                                 platform = str e "platform" |> Option.defaultValue "" }))
            |> Map.ofSeq

        log.LogInformation("Health: registry loaded - {E} entities, {D} devices, {A} areas",
                           entities.Count, devices.Count, areas.Count)
        return { entities = entities; devices = devices; areas = areas }
    with ex ->
        log.LogWarning(ex, "Health: registry fetch failed; falling back to per-entity view")
        return empty
}
