namespace Health

open System

// ── What "healthy" means, per device ─────────────────────────────────────────
// Health is not one binary. A camera that pings but records nothing is broken.
// A battery reporting fresh values that are out of range is broken. A BLE tag
// that is powered but unfindable is broken. Each device needs its own set of
// criteria, and diagnosis has to say WHICH one failed - "camera not recording"
// sends you somewhere completely different from "camera not pingable".
//
// Criteria are ANDed: a device is healthy only when every criterion it declares
// is satisfied. Each carries its own evidence source, so a failure names itself.

type Criterion =
    /// Has mains/battery power. Usually inferred from an upstream plug or
    /// circuit in the topology rather than measured directly - a plug that is
    /// on and reachable means its dependants are powered.
    | HasPower

    /// Answers ICMP. Necessary but NOT sufficient: the EG4 ESP32 pinged happily
    /// through all 25 of its publishing stalls, and StudioESP32x pinged for
    /// three weeks while contributing no BLE data at all.
    | Pingable of ip: string

    /// A named entity keeps updating within its learned cadence.
    /// This is the staleness check, scoped to one entity.
    | Reporting of entityId: string

    /// A camera is actually producing recordings. Pinging is not enough - the
    /// DVR case: the box is up, the stream is dead.
    | Recording of entityId: string

    /// Discoverable over BLE (seen by some proxy recently). For tags and
    /// BLE-only devices where radio presence IS the function.
    | Findable of entityId: string

    /// A numeric entity sits inside an acceptable band. Catches devices that
    /// report perfectly while being wrong - a battery at 5%, a freezer at 10 C.
    | InRange of entityId: string * min: float option * max: float option

    /// Reachable over the network at all (has a current IP in the inventory).
    | HasAddress

    with
        member this.label =
            match this with
            | HasPower        -> "power"
            | Pingable _      -> "pingable"
            | Reporting _     -> "reporting"
            | Recording _     -> "recording"
            | Findable _      -> "findable"
            | InRange _       -> "in-range"
            | HasAddress      -> "addressable"

        /// What to tell the user when this criterion is the one that failed.
        member this.failureText =
            match this with
            | HasPower           -> "no power"
            | Pingable ip        -> $"not answering ping ({ip})"
            | Reporting e        -> $"stopped reporting ({e})"
            | Recording e        -> $"not recording ({e})"
            | Findable e         -> $"not findable over BLE ({e})"
            | InRange (e, lo, hi) ->
                let bounds =
                    match lo, hi with
                    | Some a, Some b -> $"{a}..{b}"
                    | Some a, None   -> $">= {a}"
                    | None,   Some b -> $"<= {b}"
                    | None,   None   -> "range"
                $"out of range {bounds} ({e})"
            | HasAddress         -> "no network address"

/// Per-device health definition. `key` matches a Topology node key where one
/// exists, so the diagram and the table agree on what is broken and why.
type DeviceCriteria = {
    key      : string
    criteria : Criterion list
}

module Criteria =

    /// Explicit definitions for devices whose health is more than "it reports".
    /// Anything not listed falls back to the generic staleness/liveness watch.
    let definitions : DeviceCriteria list = [
        // Batteries: reporting is not enough - a pack sitting at a very low SOC
        // is a problem even though every sensor is fresh.
        { key = "ac500_1"
          criteria = [ Reporting "binary_sensor.ac500_connected"
                       InRange ("sensor.ac500_total_battery_percent", Some 10.0, None) ] }
        { key = "ac500_2"
          criteria = [ Reporting "binary_sensor.ac500_connected_2"
                       InRange ("sensor.ac500_total_battery_percent_2", Some 10.0, None) ] }
        { key = "ac200m"
          criteria = [ Reporting "binary_sensor.ac200m_connected"
                       InRange ("sensor.ac200m_total_battery_percent", Some 10.0, None) ] }

        // The Pi 4's job is BLE collection for the Bluettis: powered (via Kauf_XX),
        // on the network, and actually producing Bluetti data.
        { key = "raspi4"
          criteria = [ HasPower; Pingable "192.168.5.60"
                       Reporting "binary_sensor.ac500_connected" ] }

        // The Pi Zero: powered by the Shelly, on the garage SSID, and the thermal
        // camera actually producing frames.
        { key = "raspi_zero"
          criteria = [ HasPower; Pingable "192.168.5.233"
                       Reporting "sensor.thermal_master_p2_thermal_low" ] }

        // Cameras: pingable is necessary but not sufficient - recording is the job.
        { key = "kasa_garage"
          criteria = [ HasPower; Pingable "192.168.5.238" ] }
        { key = "cam_driveway"
          criteria = [ Pingable "192.168.5.9" ] }

        // Plugs: on the network and switched on, since their dependants rely on it.
        { key = "kauf_xx"    ; criteria = [ Pingable "192.168.5.235"; Reporting "switch.kauf_xx" ] }
        { key = "shelly_us"  ; criteria = [ Pingable "192.168.5.81";  Reporting "switch.shellyplugus_048308deba94" ] }

        // Routers: reachability is the whole function.
        { key = "ssid_abewnetg" ; criteria = [ Pingable "192.168.5.2" ] }
        { key = "eero"          ; criteria = [ Pingable "192.168.4.1" ] }

        { key = "garage_opener" ; criteria = [ Pingable "192.168.5.159"; Reporting "cover.garage_door" ] }
    ]

    let byKey = definitions |> List.map (fun d -> d.key, d) |> Map.ofList

    let forDevice (key: string) =
        byKey |> Map.tryFind key |> Option.map (fun d -> d.criteria) |> Option.defaultValue []
