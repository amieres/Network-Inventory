module Health.Diagnose

open System
open Health

// ── From "something is wrong" to "here is what is wrong" ─────────────────────
// A failure has several possible causes and the first evidence - the device
// stopped doing its job - does not choose between them. The old model jumped
// straight to one: any faulted node was declared unpowered, so a wedged USB
// camera and a tripped breaker produced the same verdict, and the remedy sent
// you to the wrong place.
//
// Instead, enumerate the hypotheses that could explain the symptom and score
// each against evidence that can actually discriminate. Evidence that RULES OUT
// a cause is as valuable as evidence that supports one: a device answering ping
// eliminates "no power" and "radio down" in a single observation.

/// A candidate explanation. Ordered roughly from infrastructure outward, so a
/// tie is broken toward the cause with the widest blast radius.
type Hypothesis =
    | PowerLost
    | NetworkDown
    | DeviceHung
    | PeripheralFailed
    | IntegrationBroken
    | Unknown
    with
        member this.label =
            match this with
            | PowerLost         -> "power lost"
            | NetworkDown       -> "network down"
            | DeviceHung        -> "device hung"
            | PeripheralFailed  -> "peripheral failed"
            | IntegrationBroken -> "integration broken"
            | Unknown           -> "undiagnosed"

/// A single observation, in plain words, plus which way it points. Kept as text
/// because the whole purpose is to SHOW the reasoning rather than emit a verdict
/// the user has to take on trust.
type Finding = {
    text     : string
    supports : Hypothesis list
    rulesOut : Hypothesis list
}

let support  hs text = { text = text; supports = hs; rulesOut = [] }
let ruleOut  hs text = { text = text; supports = []; rulesOut = hs }

/// A ranked explanation with the evidence behind it.
type Diagnosis = {
    cause    : Hypothesis
    score    : int
    findings : Finding list
    /// What to actually do, when the device is one whose failure mode is known.
    remedy   : string option
}

// ── Device-specific knowledge ────────────────────────────────────────────────
// Generic advice is close to useless here. Rebooting clears most things, but
// the thermal camera on the Pi Zero is a known exception: it wedges at the USB
// layer and a reboot does NOT clear it - the device has to lose power, which
// means cycling the Shelly that feeds it. That is hard-won knowledge about one
// device and belongs in the diagnosis, not in a generic "try restarting".

/// Remedy for a (node, cause) pair. Falls back to a generic suggestion.
let remedyFor (nodeKey: string) (cause: Hypothesis) : string option =
    match nodeKey, cause with
    | "raspi_zero", PeripheralFailed
    | "raspi_zero", DeviceHung ->
        Some "Power-cycle the Pi Zero at Shelly Plug US - the thermal camera \
              wedges over USB and a reboot does not clear it."
    | "thermal_camera", _ ->
        Some "Power-cycle the Pi Zero at Shelly Plug US - a reboot does not \
              clear a wedged USB camera."
    | "raspi4", DeviceHung ->
        Some "Reboot the Pi 4 - a restart normally clears the bluetti-mqtt stall."
    | _, PowerLost         -> Some "Check the breaker / plug feeding it."
    | _, NetworkDown       -> Some "Check its access point and its IP lease."
    | _, DeviceHung        -> Some "Restart the device."
    | _, PeripheralFailed  -> Some "Check the attached hardware."
    | _, IntegrationBroken -> Some "Reload the integration in Home Assistant."
    | _, Unknown           -> None

// ── Scoring ──────────────────────────────────────────────────────────────────

/// Weigh the findings and rank the hypotheses. A hypothesis that any finding
/// rules out is discarded outright, however much other support it has -
/// evidence that eliminates a cause is stronger than evidence that merely
/// suggests one.
let rank (nodeKey: string) (findings: Finding list) : Diagnosis list =
    let eliminated =
        findings |> List.collect (fun f -> f.rulesOut) |> Set.ofList
    let counts =
        findings
        |> List.collect (fun f -> f.supports)
        |> List.filter (fun h -> not (eliminated.Contains h))
        |> List.countBy id
        |> Map.ofList
    let all = [ PowerLost; NetworkDown; DeviceHung; PeripheralFailed; IntegrationBroken ]
    let ranked =
        all
        |> List.filter (fun h -> not (eliminated.Contains h))
        |> List.map (fun h -> h, counts |> Map.tryFind h |> Option.defaultValue 0)
        |> List.filter (fun (_, n) -> n > 0)
        |> List.sortByDescending snd
    match ranked with
    | [] ->
        // Everything plausible was eliminated, or nothing pointed anywhere.
        // Saying "undiagnosed" and showing the evidence is more honest - and
        // more useful - than promoting a guess to a verdict.
        [ { cause = Unknown; score = 0; findings = findings
            remedy = remedyFor nodeKey Unknown } ]
    | _ ->
        ranked
        |> List.map (fun (h, n) ->
            { cause = h; score = n; findings = findings; remedy = remedyFor nodeKey h })
