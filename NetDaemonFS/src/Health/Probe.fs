module Health.Probe

open System
open System.Net.NetworkInformation

// ── Active evidence gathering ────────────────────────────────────────────────
// Probing is a response to a failure, not a background habit. A device whose
// PURPOSE is being fulfilled needs no poking: the Pi Zero publishing thermal
// frames is proof enough that it has power, a radio and a working camera. Only
// once something is flagged does it become worth spending traffic to find out
// WHICH of the possible causes actually holds.
//
// This exists because the model used to answer that question by assertion. The
// Pi Zero's sensors froze and the dashboard reported "no power" - while the Pi
// answered every ping. "Stopped reporting" had been quietly restated as "no
// electricity", which is the one conclusion the evidence ruled out.

/// Outcome of one ICMP probe. `Unknown` matters: "we never checked" must stay
/// distinguishable from "we checked and it was down", or absence of evidence
/// turns into evidence of absence all over again.
type Reach =
    | Up      of roundTripMs: int64
    | Down
    | Unknown
    with
        member this.label =
            match this with
            | Up ms   -> $"up ({ms} ms)"
            | Down    -> "no reply"
            | Unknown -> "not checked"

        member this.isUp   = match this with Up _ -> true | _ -> false
        member this.isDown = match this with Down -> true | _ -> false

/// One probe result, kept with its timestamp so stale evidence can be re-taken
/// rather than silently reused to justify a later diagnosis.
type Evidence = {
    target : string
    reach  : Reach
    takenAt: DateTimeOffset
}

/// Ping one host, retrying before concluding it is down. A single timeout is
/// not evidence: a sleepy WiFi device can take seconds to answer its first
/// packet after idling, and that false "down" would then be used to argue for a
/// power cut. Only silence across every attempt counts as down.
let pingTimes (attempts: int) (timeoutMs: int) (ip: string) : Evidence =
    let rec attempt n =
        if n <= 0 then Down
        else
            try
                use p = new Ping()
                let r = p.Send(ip, timeoutMs)
                if r.Status = IPStatus.Success then Up r.RoundtripTime
                else attempt (n - 1)
            with _ ->
                // A malformed address or a blocked raw socket is not the
                // device's fault, so it must not be recorded as down.
                Unknown
    { target = ip; reach = attempt attempts; takenAt = DateTimeOffset.UtcNow }

/// Three attempts at two seconds is long enough for every device on this
/// network to answer when it is actually alive.
let ping (timeoutMs: int) (ip: string) : Evidence = pingTimes 3 timeoutMs ip

/// Ping several hosts concurrently. Corroboration usually needs a handful of
/// targets at once - the device, its circuit-mates, its router - and doing them
/// in series adds seconds for no reason.
let pingAll (timeoutMs: int) (ips: string list) : Map<string, Evidence> =
    ips
    |> List.distinct
    |> List.map (fun ip -> async { return ip, ping timeoutMs ip })
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Map.ofArray
