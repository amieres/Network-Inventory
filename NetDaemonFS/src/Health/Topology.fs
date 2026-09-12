namespace Health

open System

// ── Dependency graph ─────────────────────────────────────────────────────────
// Encodes what depends on what, so a failure can be explained rather than just
// listed. Without this, one tripped circuit shows up as six unrelated red rows;
// with it, five of them are Suppressed and the parent is the incident.
//
// Edges are entered by hand. They cannot be inferred reliably: nothing on the
// network reveals that the Pi Zero draws power from a particular Shelly plug,
// or which manual-transfer-switch circuits feed from which AC500.

type EdgeKind =
    /// Child loses power when the parent is off/unpowered.
    | Power
    /// Child is unreachable when the parent (AP, router, switch) is down.
    | Network
    /// Child is a process/integration hosted on the parent machine.
    | Host
    /// Parent is the BLE gateway through which the child is seen.
    | BtHost
    with
        member this.label =
            match this with
            | Power -> "power" | Network -> "network" | Host -> "host" | BtHost -> "bt-host"

/// A node is referenced by a stable key, not an IP - devices move addresses
/// (the EG4 monitor has used 16). Prefer the inventory device name; an HA
/// device name or entity id also works when there is no inventory row.
type Node = {
    key      : string              // stable id used by edges
    label    : string              // display name
    kind     : string              // battery | plug | pi | camera | esp32 | ap | circuit | appliance | opener
    /// Inventory device name, when this node maps to a scanned device.
    device   : string option
    /// HA entity that reports this node's own health, when one exists.
    entity   : string option
    /// Physical grouping for diagram layout.
    area     : string option
    /// Losing this node takes Home Assistant itself down, so the health monitor
    /// cannot observe or report the failure - the dashboard simply goes dark.
    /// Marked so the UI can say "you will not be told" rather than implying
    /// detection that cannot happen.
    blindSpot : bool
    /// What to actually do when this node fails, when it is not obvious.
    remedy   : string option
}

type Edge = {
    child  : string      // Node.key
    parent : string      // Node.key
    kind   : EdgeKind
    note   : string option
}

module Topology =

    // Nodes that are not network-visible (circuits, the transfer switch) still
    // need to exist so power edges can point at them.
    let nodes : Node list = [
        // ── Power sources ────────────────────────────────────────────────────
        yield { key = "ac500_1";      label = "BLUETTI AC500 #1";  kind = "battery"; device = Some "BLUETTI AC500 #1"; entity = Some "binary_sensor.ac500_connected";   area = Some "Garage"; blindSpot = false; remedy = None }
        yield { key = "ac500_2";      label = "BLUETTI AC500 #2";  kind = "battery"; device = Some "BLUETTI AC500 #2"; entity = Some "binary_sensor.ac500_connected_2"; area = Some "Garage"; blindSpot = false; remedy = None }
        yield { key = "ac200m";       label = "BLUETTI AC200M";    kind = "battery"; device = Some "BLUETTI AC200M";   entity = Some "binary_sensor.ac200m_connected";  area = Some "Garage"; blindSpot = false; remedy = None }

        // ── Manual transfer switch circuits ──────────────────────────────────
        // A C E G H -> AC500 #1 ; B D F I -> AC500 #2
        // Circuit D carries the routers, the modem and the game room, so losing it
        // takes Home Assistant itself offline - the monitor cannot report its own
        // death. The fix is manual and quick: flip the transfer switch for D from
        // Battery to Line, which restores WiFi, LAN and internet.
        for c in [ "A"; "C"; "E"; "G"; "H"; "B"; "D"; "F"; "I" ] ->
            { key       = $"circuit_{c.ToLowerInvariant()}"
              label     = $"Circuit {c}"
              kind      = "circuit"
              device    = None
              entity    = None
              area      = Some "Garage"
              blindSpot = (c = "D")
              remedy    = if c = "D" then Some "Switch circuit D to LINE on the manual transfer switch - restores WiFi, LAN and internet" else None }

        // ── Smart plugs ──────────────────────────────────────────────────────
        yield { key = "kauf_xx";      label = "Kauf_XX (PLF12)";   kind = "plug"; device = Some "Kauf_XX (PLF12)"; entity = Some "switch.kauf_xx";                      area = None; blindSpot = false; remedy = None }
        yield { key = "shelly_us";    label = "Shelly Plug US";    kind = "plug"; device = Some "Shelly Plug US";  entity = Some "switch.shellyplugus_048308deba94";    area = Some "Garage"; blindSpot = false; remedy = None }

        // ── Compute ──────────────────────────────────────────────────────────
        yield { key = "raspi4";       label = "AbeRaspi4";         kind = "pi"; device = Some "AbeRaspi4"; entity = None; area = None; blindSpot = false; remedy = None }
        // The Pi Zero has no inventory name (Avahi name conflict); keyed by IP-bearing row.
        yield { key = "raspi_zero";   label = "Pi Zero 2 W (thermal)"; kind = "pi"; device = None; entity = Some "sensor.thermal_master_p2_thermal_low"; area = Some "Garage"; blindSpot = false; remedy = None }

        // ── Devices ──────────────────────────────────────────────────────────
        yield { key = "kasa_garage";  label = "Kasa Garage camera"; kind = "camera"; device = Some "Kasa Garage";       entity = None; area = Some "Garage"; blindSpot = false; remedy = None }
        yield { key = "cam_driveway"; label = "CloudEdge Driveway"; kind = "camera"; device = Some "CloudEdge Driveway"; entity = None; area = Some "Driveway"; blindSpot = false; remedy = None }
        yield { key = "garage_opener";label = "Garage Opener";      kind = "opener"; device = Some "Garage Opener";      entity = Some "cover.garage_door"; area = Some "Garage"; blindSpot = false; remedy = None }
        yield { key = "midea_ac";     label = "Midea window A/C";   kind = "appliance"; device = Some "Midea AC";        entity = None; area = Some "Garage"; blindSpot = false; remedy = None }

        // ── WiFi APs / SSIDs ─────────────────────────────────────────────────
        // Netgear does not report per-client SSID, so these are manual nodes.
        //
        // ABWNETG_GAR is a separate **Qbit** router, not the NETGEAR. Its clients
        // therefore show as "Wired" in the inventory: the NETGEAR only sees the
        // Qbit's uplink, not the clients' own radios. (Same artifact as eero
        // reporting connection_type=wired for 76 of 100 devices - that just means
        // "not on my radios".) So do NOT infer this edge from scan data; a garage
        // device reading "Wired" is the signature of being behind the Qbit.
        yield { key = "ssid_abewnetg";     label = "ABEWNETG (NETGEAR RAX80)"; kind = "ap"; device = Some "NETGEAR RAX80"; entity = None; area = None; blindSpot = false; remedy = None }
        yield { key = "qbit_gar";          label = "Qbit router (garage)";     kind = "ap"; device = None; entity = None; area = Some "Garage"; blindSpot = false; remedy = None }
        yield { key = "ssid_abewnetg_gar"; label = "ABWNETG_GAR (on Qbit)";    kind = "ap"; device = None; entity = None; area = Some "Garage"; blindSpot = false; remedy = None }
        yield { key = "eero";              label = "eero";                     kind = "ap"; device = Some "eero"; entity = None; area = None; blindSpot = false; remedy = None }
        yield { key = "modem";             label = "Internet modem";           kind = "modem"; device = None; entity = None; area = Some "Game Room"; blindSpot = false; remedy = None }
        yield { key = "game_room";         label = "Game Room (everything)";   kind = "zone"; device = None; entity = None; area = Some "Game Room"; blindSpot = false; remedy = None }

        // Home Assistant runs on the LAN that circuit D powers. If D goes, HA goes,
        // and with it this monitor - hence blindSpot. Nothing will be reported.
        yield { key = "homeassistant"; label = "Home Assistant"; kind = "host"
                device = Some "AbeHomeAssistant"; entity = None; area = None
                blindSpot = true
                remedy = Some "If HA is unreachable, suspect circuit D - switch it to LINE" }

        // The AC OUTPUT of AC500 #2 is a distinct failure point from the AC500
        // itself: the unit can be perfectly healthy and reachable over WiFi while
        // its AC output is off. That is what kills Kauf_XX -> Pi 4 -> all Bluetti
        // BLE data, while the AC500s themselves still answer ping.
        yield { key = "ac500_2_acout"; label = "AC500 #2 AC output"; kind = "outlet"
                device = None; entity = None; area = Some "Garage"
                blindSpot = false
                remedy = Some "AC500 #2 may be fine and pingable while its AC output is off - check the unit's AC OUT, not its connectivity" }
    ]

    let edges : Edge list = [
        // ── Power: transfer-switch circuits ──────────────────────────────────
        for c in [ "a"; "c"; "e"; "g"; "h" ] ->
            { child = $"circuit_{c}"; parent = "ac500_1"; kind = Power; note = Some "manual transfer switch" }
        for c in [ "b"; "d"; "f"; "i" ] ->
            { child = $"circuit_{c}"; parent = "ac500_2"; kind = Power; note = Some "manual transfer switch" }

        // ── Power: devices on plugs / battery outputs ────────────────────────
        // The Pi 4 collects Bluetooth data for the Bluettis - if Kauf_XX has WiFi
        // and is on, the Pi is receiving power, which distinguishes "Pi crashed"
        // from "Pi lost power" (see RebootRaspi.fs, which power-cycles on that basis).
        // Kauf_XX is fed from the AC OUTPUT of AC500 #2. This is the one exception
        // to "circuit D explains everything": if AC500 #2's AC out is off then
        // Kauf_XX is off, the Pi 4 is off, and NO Bluetti appears connected -
        // yet the AC500s themselves are still on WiFi, answer ping, and remain
        // reachable through the Bluetti app (given internet).
        yield { child = "ac500_2_acout"; parent = "ac500_2"; kind = Power; note = Some "AC output of the unit" }
        yield { child = "kauf_xx";       parent = "ac500_2_acout"; kind = Power; note = Some "fed from AC500 #2 AC out" }
        yield { child = "raspi4";       parent = "kauf_xx";   kind = Power; note = Some "bluetti-mqtt host" }
        yield { child = "raspi_zero";   parent = "shelly_us"; kind = Power; note = Some "shared with Kasa garage camera" }
        yield { child = "kasa_garage";  parent = "shelly_us"; kind = Power; note = Some "shared with Pi Zero" }
        yield { child = "garage_opener";parent = "ac500_2";   kind = Power; note = Some "on AC500 #2 output" }
        yield { child = "midea_ac";     parent = "ac200m";    kind = Power; note = Some "cools the space the thermal camera measures" }

        // ── Network ──────────────────────────────────────────────────────────
        // The Qbit hangs off the NETGEAR, so a NETGEAR outage takes the garage
        // SSID with it - two hops the diagram can show.
        yield { child = "ssid_abewnetg_gar"; parent = "qbit_gar";      kind = Network; note = None }
        yield { child = "qbit_gar";          parent = "ssid_abewnetg"; kind = Network; note = Some "Qbit uplink to NETGEAR" }
        yield { child = "raspi_zero";        parent = "ssid_abewnetg_gar"; kind = Network; note = None }
        yield { child = "cam_driveway";      parent = "ssid_abewnetg_gar"; kind = Network; note = None }
        // ── Circuit D is the single point of failure for the whole network ───
        // The WiFi routers AND the internet modem are plugged into circuit D, as
        // is everything in the game room. Losing D therefore takes out all WiFi,
        // all internet, and the game room at once - which would otherwise look
        // like dozens of unrelated device failures. Circuit D feeds from AC500 #2.
        yield { child = "ssid_abewnetg"; parent = "circuit_d"; kind = Power; note = Some "NETGEAR on circuit D" }
        yield { child = "qbit_gar";      parent = "circuit_d"; kind = Power; note = Some "Qbit on circuit D" }
        yield { child = "eero";          parent = "circuit_d"; kind = Power; note = Some "eero on circuit D" }
        yield { child = "modem";         parent = "circuit_d"; kind = Power; note = Some "internet modem on circuit D" }
        yield { child = "game_room";     parent = "circuit_d"; kind = Power; note = Some "whole game room on circuit D" }
        // The APs route through the modem for internet (not for LAN reachability).
        yield { child = "ssid_abewnetg"; parent = "modem"; kind = Network; note = Some "WAN uplink" }
        yield { child = "homeassistant"; parent = "circuit_d"; kind = Power
                note = Some "HA dies with circuit D - this monitor cannot report it" }

        // Cloud cameras need INTERNET, not just LAN: losing the modem drops them
        // even though WiFi still works. Distinct from a WiFi failure.
        yield { child = "cam_driveway"; parent = "modem"; kind = Network; note = Some "cloud camera needs internet" }
        yield { child = "kasa_garage";  parent = "modem"; kind = Network; note = Some "cloud camera needs internet" }

        // Most smart plugs and sensors are on the main SSID.
        yield { child = "kauf_xx";      parent = "ssid_abewnetg"; kind = Network; note = None }
        yield { child = "shelly_us";    parent = "ssid_abewnetg"; kind = Network; note = None }
        yield { child = "raspi4";       parent = "ssid_abewnetg"; kind = Network; note = None }
        yield { child = "garage_opener";parent = "ssid_abewnetg"; kind = Network; note = None }
        yield { child = "kasa_garage";  parent = "ssid_abewnetg"; kind = Network; note = None }

        // ── BLE ──────────────────────────────────────────────────────────────
        // The Pi 4 is the Bluetti BLE gateway: when it cannot connect, the AC500
        // data stops even though the power stations themselves are fine.
        yield { child = "ac500_1"; parent = "raspi4"; kind = BtHost; note = Some "bluetti-mqtt" }
        yield { child = "ac500_2"; parent = "raspi4"; kind = BtHost; note = Some "bluetti-mqtt" }
        yield { child = "ac200m";  parent = "raspi4"; kind = BtHost; note = Some "bluetti-mqtt" }
    ]

    let nodeByKey = nodes |> List.map (fun n -> n.key, n) |> Map.ofList

    /// Direct parents of a node, by edge kind.
    let parentsOf (key: string) = edges |> List.filter (fun e -> e.child = key)

    /// Direct children of a node.
    let childrenOf (key: string) = edges |> List.filter (fun e -> e.parent = key)

    /// All ancestors, nearest first. Cycle-safe.
    let ancestorsOf (key: string) =
        let rec walk seen k =
            parentsOf k
            |> List.collect (fun e ->
                if Set.contains e.parent seen then []
                else e.parent :: walk (Set.add e.parent seen) e.parent)
        walk (Set.singleton key) key |> List.distinct
