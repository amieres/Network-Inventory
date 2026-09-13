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
    /// Entity reporting whether this node's OUTPUT is on. A power station or a
    /// smart plug can be perfectly healthy while its output is switched off -
    /// that is a state of the device, not a separate device.
    outputEntity : string option
    /// Which SSID / link this node is on, so the diagram can colour its radio
    /// icon. None = not a wireless node.
    link     : string option
    /// Relative box size on the diagram: 1 = small, 2 = normal, 3 = large.
    /// Grid-snapped in the UI.
    size     : int
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

    /// Terse constructor - most nodes only need a few fields, and 12-field
    /// literals are unreadable.
    let private node key label kind =
        { key = key; label = label; kind = kind
          device = None; entity = None; area = None
          blindSpot = false; remedy = None
          outputEntity = None; link = None; size = 2 }

    let nodes : Node list = [
        // ── Power sources ────────────────────────────────────────────────────
        // The GRID is a power source in its own right, not just what charges the
        // batteries: AbeVue2, the EV charger and StudioESP32x are mains-only, so
        // they SURVIVE a battery failure and die in a utility outage - the
        // inverse of everything on the transfer-switch circuits.
        // sensor.total_ac_input_power read 0 W on battery and jumped to 1508 W
        // when mains returned on 2026-08-25.
        yield { node "grid" "Utility grid (mains)" "grid" with
                  entity = Some "sensor.total_ac_input_power"; size = 3
                  remedy = Some "Utility outage: battery-backed circuits keep running; mains-only devices are down until power returns" }

        // Only AC500 #1 takes an AC input from the grid. AC500 #2 and the AC200M
        // are NOT grid-connected.
        // `outputEntity` is the unit's AC-OUTPUT state: a station can be healthy
        // and pingable while its output is off - a STATE of the device, not a
        // separate device.
        yield { node "ac500_1" "BLUETTI AC500 #1" "battery" with
                  device = Some "BLUETTI AC500 #1"; entity = Some "binary_sensor.ac500_connected"
                  outputEntity = Some "sensor.ac500_ac_output_power"
                  area = Some "Garage"; link = Some "ABEWNETG"; size = 3 }
        yield { node "ac500_2" "BLUETTI AC500 #2" "battery" with
                  device = Some "BLUETTI AC500 #2"; entity = Some "binary_sensor.ac500_connected_2"
                  outputEntity = Some "sensor.ac500_ac_output_power_2"
                  area = Some "Garage"; link = Some "ABEWNETG"; size = 3 }
        yield { node "ac200m" "BLUETTI AC200M" "battery" with
                  device = Some "BLUETTI AC200M"; entity = Some "binary_sensor.ac200m_connected"
                  outputEntity = Some "sensor.ac200m_ac_output_power"
                  area = Some "Garage"; size = 3 }

        // ── Manual transfer switch circuits ──────────────────────────────────
        // A C E G H default to AC500 #1, B D F I to AC500 #2 - but every circuit
        // is MANUALLY SWITCHABLE between its Bluetti and the grid (Line). That is
        // why a battery failure is recoverable without any electrical work, and
        // why circuit D's remedy is simply "switch it to LINE".
        for c in [ "A"; "C"; "E"; "G"; "H"; "B"; "D"; "F"; "I" ] ->
            { node $"circuit_{c.ToLowerInvariant()}" $"Circuit {c}" "circuit" with
                area      = Some "Garage"
                size      = 1
                blindSpot = (c = "D")
                remedy    = if c = "D"
                            then Some "Switch circuit D to LINE on the manual transfer switch - restores WiFi, LAN and internet"
                            else Some "Switchable to LINE (grid) on the manual transfer switch" }

        // ── Smart plugs ─────────────────────────────────────────────────────
        // Plugs gate their dependants: `outputEntity` is the switch state, so a
        // plug that is online but switched OFF still means no power downstream.
        yield { node "kauf_xx" "Kauf_XX (PLF12)" "plug" with
                  device = Some "Kauf_XX (PLF12)"; entity = Some "switch.kauf_xx"
                  outputEntity = Some "switch.kauf_xx"; link = Some "ABEWNETG"; size = 1 }
        yield { node "shelly_us" "Shelly Plug US" "plug" with
                  device = Some "Shelly Plug US"; entity = Some "switch.shellyplugus_048308deba94"
                  outputEntity = Some "switch.shellyplugus_048308deba94"
                  area = Some "Garage"; link = Some "ABEWNETG"; size = 1 }

        // ── Compute ─────────────────────────────────────────────────────────
        yield { node "raspi4" "AbeRaspi4" "pi" with
                  device = Some "AbeRaspi4"; link = Some "ABEWNETG" }
        yield { node "raspi_zero" "Pi Zero 2 W (thermal)" "pi" with
                  entity = Some "sensor.thermal_master_p2_thermal_low"
                  area = Some "Garage"; link = Some "ABEWNETG-GAR" }
        yield { node "mac_studio" "M1 Mac Studio" "computer" with
                  device = Some "M1 Mac Studio"; area = Some "Game Room"; link = Some "ABEWNETG" }
        yield { node "homeassistant" "Home Assistant" "host" with
                  device = Some "AbeHomeAssistant"; blindSpot = true; size = 3
                  remedy = Some "If HA is unreachable, suspect circuit D - switch it to LINE" }

        // ── Mains-only devices (no battery backup) ───────────────────────────
        yield { node "abevue2" "AbeVue2 (energy monitor)" "sensor" with
                  device = Some "AbeVue2"; entity = Some "sensor.abevue2_10_oven"; link = Some "ABEWNETG" }
        yield { node "ev_charger" "Emporia EV Charger" "ev" with
                  device = Some "Emporia EV Charger"; area = Some "Garage"; link = Some "ABEWNETG" }
        yield { node "studio_esp32" "StudioESP32x (BT proxy)" "esp32" with
                  device = Some "StudioESP32x"; entity = Some "sensor.studioesp32x_uptime_sensor"
                  area = Some "Studio"; link = Some "ABEWNETG"
                  remedy = Some "On mains, not battery - a utility outage takes it out even when the Bluettis are fine" }

        // ── Devices ─────────────────────────────────────────────────────────
        yield { node "kasa_garage" "Kasa Garage camera" "camera" with
                  device = Some "Kasa Garage"; area = Some "Garage"; link = Some "ABEWNETG" }
        yield { node "cam_driveway" "CloudEdge Driveway" "camera" with
                  device = Some "CloudEdge Driveway"; area = Some "Driveway"; link = Some "ABEWNETG-GAR" }
        yield { node "garage_opener" "Garage Opener" "opener" with
                  device = Some "Garage Opener"; entity = Some "cover.garage_door"
                  area = Some "Garage"; link = Some "ABEWNETG" }
        yield { node "midea_ac" "Midea window A/C" "appliance" with
                  device = Some "Midea AC"; area = Some "Garage"; link = Some "ABEWNETG" }

        // ── Network ─────────────────────────────────────────────────────────
        // Wiring: modem -wired- eero -wired- Nighthawk, and eero -wired- DBit
        // (in the Game Room). Nighthawk is wired to the Mac and to Home Assistant.
        // DBit serves ABEWNETG-GAR. Devices behind DBit read as "Wired" in the
        // inventory because the NETGEAR only sees its uplink, not its radios.
        // The INTERNET connection itself, distinct from the modem hardware: the
        // modem can be powered and healthy while the WAN link is down, which is
        // what cloud cameras and remote access actually depend on.
        yield { node "internet" "Internet (WAN)" "internet" with
                  entity = Some "binary_sensor.internet_up"; size = 3
                  remedy = Some "WAN down: LAN and WiFi keep working; cloud cameras, remote access and app-dependent devices do not" }
        yield { node "modem" "Internet modem" "modem" with
                  area = Some "Game Room"; size = 3 }
        yield { node "eero" "eero" "ap" with
                  device = Some "eero"; link = Some "AbeEero"; size = 3 }
        yield { node "nighthawk" "NETGEAR Nighthawk RAX80" "ap" with
                  device = Some "NETGEAR RAX80"; link = Some "ABEWNETG"; size = 3 }
        yield { node "dbit" "DBit router" "ap" with
                  area = Some "Game Room"; link = Some "ABEWNETG-GAR"; size = 3 }
    ]

    let edges : Edge list = [
        // ── Power: transfer-switch circuits ─────────────────────────────────
        // Every circuit is manually switchable to the grid (Line), so these are
        // the DEFAULT source, not a hard wiring.
        for c in [ "a"; "c"; "e"; "g"; "h" ] ->
            { child = $"circuit_{c}"; parent = "ac500_1"; kind = Power; note = Some "manual transfer switch (switchable to Line)" }
        for c in [ "b"; "d"; "f"; "i" ] ->
            { child = $"circuit_{c}"; parent = "ac500_2"; kind = Power; note = Some "manual transfer switch (switchable to Line)" }

        // ── Power: mains-only devices ───────────────────────────────────────
        // No battery backup: they die in a utility outage and survive a Bluetti
        // failure - the opposite of everything on the circuits.
        yield { child = "abevue2";      parent = "grid"; kind = Power; note = Some "mains only - no battery backup" }
        yield { child = "ev_charger";   parent = "grid"; kind = Power; note = Some "mains only - no battery backup" }
        yield { child = "studio_esp32"; parent = "grid"; kind = Power; note = Some "mains only - no battery backup" }
        // Only AC500 #1 takes an AC input from the grid.
        yield { child = "ac500_1"; parent = "grid"; kind = Power; note = Some "AC input charges the pack" }

        // ── Power: devices on plugs / battery outputs ───────────────────────
        // Gated by AC500 #2's AC-output STATE: the unit can be healthy and
        // pingable while its output is off, which kills Kauf_XX -> Pi 4 -> all
        // Bluetti BLE data even though the AC500s still answer ping.
        yield { child = "kauf_xx";       parent = "ac500_2";   kind = Power; note = Some "fed from AC500 #2 AC out (see its output state)" }
        yield { child = "raspi4";        parent = "kauf_xx";   kind = Power; note = Some "bluetti-mqtt host" }
        yield { child = "raspi_zero";    parent = "shelly_us"; kind = Power; note = Some "shared with Kasa garage camera" }
        yield { child = "kasa_garage";   parent = "shelly_us"; kind = Power; note = Some "shared with Pi Zero" }
        yield { child = "garage_opener"; parent = "ac500_2";   kind = Power; note = Some "on AC500 #2 output" }
        yield { child = "midea_ac";      parent = "ac200m";    kind = Power; note = Some "cools the space the thermal camera measures" }

        // ── Power: circuit D carries the whole network ──────────────────────
        // Routers, the modem and the game room are all on D, so losing it takes
        // WiFi, LAN, internet AND Home Assistant at once.
        yield { child = "nighthawk";     parent = "circuit_d"; kind = Power; note = Some "Nighthawk on circuit D" }
        yield { child = "eero";          parent = "circuit_d"; kind = Power; note = Some "eero on circuit D" }
        yield { child = "dbit";          parent = "circuit_d"; kind = Power; note = Some "DBit on circuit D" }
        yield { child = "modem";         parent = "circuit_d"; kind = Power; note = Some "internet modem on circuit D" }
        yield { child = "homeassistant"; parent = "circuit_d"; kind = Power
                note = Some "HA dies with circuit D - this monitor cannot report it" }

        // ── Network: the wired backbone ─────────────────────────────────────
        // modem --wired-- eero --wired-- Nighthawk
        //                  \--wired-- DBit (Game Room)
        // Nighthawk --wired-- Mac, Home Assistant
        yield { child = "internet";      parent = "modem";     kind = Network; note = Some "WAN service" }
        yield { child = "eero";          parent = "modem";     kind = Network; note = Some "wired" }
        yield { child = "nighthawk";     parent = "eero";      kind = Network; note = Some "wired" }
        yield { child = "dbit";          parent = "eero";      kind = Network; note = Some "wired" }
        yield { child = "mac_studio";    parent = "nighthawk"; kind = Network; note = Some "wired (also has WiFi)" }
        yield { child = "homeassistant"; parent = "nighthawk"; kind = Network; note = Some "wired" }

        // ── Network: wireless clients ───────────────────────────────────────
        yield { child = "raspi_zero";    parent = "dbit";      kind = Network; note = Some "ABEWNETG-GAR" }
        yield { child = "cam_driveway";  parent = "dbit";      kind = Network; note = Some "ABEWNETG-GAR" }
        yield { child = "kauf_xx";       parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "shelly_us";     parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "raspi4";        parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "garage_opener"; parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "kasa_garage";   parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "abevue2";       parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "ev_charger";    parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "studio_esp32";  parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "ac500_1";       parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }
        yield { child = "ac500_2";       parent = "nighthawk"; kind = Network; note = Some "ABEWNETG" }

        // Cloud cameras need INTERNET, not just LAN - losing the modem drops them
        // even though WiFi still works. (Which cameras is brand-dependent; TODO.)
        yield { child = "cam_driveway"; parent = "internet"; kind = Network; note = Some "cloud camera needs internet" }

        // ── BLE ─────────────────────────────────────────────────────────────
        // The Pi 4 is the Bluetti BLE gateway: when it wedges, all three stop
        // reporting while the power stations themselves are fine.
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
