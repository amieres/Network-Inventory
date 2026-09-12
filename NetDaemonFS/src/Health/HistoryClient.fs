module Health.HistoryClient

open System
open System.Net.Http
open System.Text.Json
open Microsoft.Extensions.Logging

// Learns an entity's reporting cadence from HA's history API.
//   GET /api/history/period/<start>?end_time=<end>&filter_entity_id=a,b,c&minimal_response
// Returns an array-of-arrays: one inner array per entity, each element a state
// point. minimal_response keeps attributes out of the payload - essential here,
// since a single busy sensor can return 35k points for one day.

let private iso (t: DateTimeOffset) = t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss") + "Z"

/// Gaps (seconds) between consecutive state points, per entity id.
let fetchGaps
    (http  : HttpClient)
    (log   : ILogger)
    (baseUrl : string)
    (token : string)
    (entityIds : string list)
    (window : TimeSpan)
    : Async<Map<string, float list>> =
    async {
        if List.isEmpty entityIds then return Map.empty else

        let now   = DateTimeOffset.UtcNow
        let start = now - window
        let ids   = String.Join(",", entityIds)
        let url   = $"{baseUrl}/api/history/period/{iso start}?end_time={iso now}&filter_entity_id={Uri.EscapeDataString ids}&minimal_response"

        use req = new HttpRequestMessage(HttpMethod.Get, url)
        req.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)

        try
            let! resp = http.SendAsync req |> Async.AwaitTask
            if not resp.IsSuccessStatusCode then
                log.LogWarning("Health: history returned {Code} for {N} entities", int resp.StatusCode, List.length entityIds)
                return Map.empty
            else

            let! body = resp.Content.ReadAsStreamAsync() |> Async.AwaitTask
            use doc   = JsonDocument.Parse(body)
            log.LogDebug("Health: history parsed {N} series for a batch of {B}",
                         doc.RootElement.GetArrayLength(), List.length entityIds)

            let result =
                doc.RootElement.EnumerateArray()
                |> Seq.choose (fun series ->
                    let points = series.EnumerateArray() |> Seq.toArray
                    if points.Length = 0 then None else

                    // entity_id appears on the first point only under minimal_response
                    let entityId =
                        let mutable v = Unchecked.defaultof<JsonElement>
                        if points.[0].TryGetProperty("entity_id", &v) then v.GetString() else null

                    if isNull entityId then None else

                    let stamps =
                        points
                        |> Array.choose (fun (p: JsonElement) ->
                            let pick (name: string) =
                                let mutable v = Unchecked.defaultof<JsonElement>
                                if p.TryGetProperty(name, &v) && v.ValueKind = JsonValueKind.String
                                then Some (v.GetString()) else None
                            match pick "last_changed" |> Option.orElseWith (fun () -> pick "last_updated") with
                            | Some (s: string) ->
                                let mutable t = DateTimeOffset.MinValue
                                if DateTimeOffset.TryParse(s, &t) then Some t else None
                            | None -> None)
                        |> Array.sort

                    let gaps =
                        if stamps.Length < 2 then []
                        else [ for i in 1 .. stamps.Length - 1 -> (stamps.[i] - stamps.[i-1]).TotalSeconds ]

                    Some (entityId, gaps))
                |> Map.ofSeq

            return result
        with ex ->
            log.LogWarning(ex, "Health: history fetch failed for {N} entities: {Msg}",
                           List.length entityIds, ex.Message)
            return Map.empty
    }
