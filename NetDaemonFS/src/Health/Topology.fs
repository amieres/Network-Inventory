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
    /// Where this node's power comes from. `FromArea` draws no line - the area
    /// rectangle already encloses the node, so a line to it is pure clutter.
    powerFrom : PowerSource
    /// How this node reaches the LAN. `NoLan` is NOT the same as `Wired`: it
    /// means the device has no network at all (a dumb appliance), which is why
    /// it can never be diagnosed by reachability.
    lan      : LanKind
    /// The BLE gateway this node is seen through, when it is only reachable over
    /// Bluetooth. Losing the gateway silences the device while the device itself
    /// is fine - blame the gateway, not the three healthy Bluettis.
    btHost   : string option
    /// Needs working internet (not just LAN) to function - cloud cameras, remote
    /// access. Brand-dependent, so it is declared per device.
    needsInternet : bool
    /// The upstream service that feeds this node, for the WAN handoff: the
    /// internet arrives AT the ONT, so the ONT depends on it.
    wanFrom  : string option
}

/// Power can come from a specific device (grid, plug, power station, circuit,
/// anything) or simply from the area the node sits in - a wall socket on that
/// area's circuit. Area-sourced power draws no line.
and PowerSource =
    | FromDevice of key : string
    | FromArea   of area : string
    | PowerUnknown
    with
        member this.label =
            match this with
            | FromDevice k -> "device:" + k
            | FromArea a   -> "area:" + a
            | PowerUnknown -> ""

/// `NoLan` <> `Wired`: no LAN at all means the device is not smart and cannot be
/// reached or diagnosed over the network. Powerline is wired access obtained
/// *from the area* rather than from a named switch/router.
and LanKind =
    | Wifi      of ssid : string
    | Wired     of source : string      // node key of the switch/router/AP
    | Powerline of area : string        // wired access via the area's powerline
    | NoLan
    with
        member this.label =
            match this with
            | Wifi s      -> "wifi:" + s
            | Wired src   -> "wired:" + src
            | Powerline a -> "powerline:" + a
            | NoLan       -> "none"

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
          outputEntity = None; link = None; size = 2
          powerFrom = PowerUnknown; lan = NoLan
          btHost = None; needsInternet = false; wanFrom = None }

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
                  remedy = Some "Utility outage: battery-backed circuits keep running; mains-only devices are down until power returns"
                  powerFrom = PowerUnknown; lan = NoLan }

        // Only AC500 #1 takes an AC input from the grid. AC500 #2 and the AC200M
        // are NOT grid-connected.
        // `outputEntity` is the unit's AC-OUTPUT state: a station can be healthy
        // and pingable while its output is off - a STATE of the device, not a
        // separate device.
        yield { node "ac500_1" "BLUETTI AC500 #1" "battery" with
                  device = Some "BLUETTI AC500 #1"; entity = Some "binary_sensor.ac500_connected"
                  outputEntity = Some "sensor.ac500_ac_output_power"
                  area = Some "Garage"; link = Some "ABEWNETG"; size = 3
                  powerFrom = FromDevice "grid"; lan = Wifi "ABEWNETG"
                  btHost = Some "raspi4" }
        yield { node "ac500_2" "BLUETTI AC500 #2" "battery" with
                  device = Some "BLUETTI AC500 #2"; entity = Some "binary_sensor.ac500_connected_2"
                  outputEntity = Some "sensor.ac500_ac_output_power_2"
                  area = Some "Garage"; link = Some "ABEWNETG"; size = 3
                  powerFrom = PowerUnknown; lan = Wifi "ABEWNETG"
                  btHost = Some "raspi4" }
        yield { node "ac200m" "BLUETTI AC200M" "battery" with
                  device = Some "BLUETTI AC200M"; entity = Some "binary_sensor.ac200m_connected"
                  outputEntity = Some "sensor.ac200m_ac_output_power"
                  area = Some "Garage"; size = 3
                  powerFrom = PowerUnknown; lan = NoLan
                  btHost = Some "raspi4" }

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
                            else Some "Switchable to LINE (grid) on the manual transfer switch"
                // Default feed: A C E G H from AC500 #1, B D F I from AC500 #2.
                // Every circuit is manually switchable to LINE (the grid).
                powerFrom = FromDevice (if "ACEGH".Contains c then "ac500_1" else "ac500_2") }

        // ── Smart plugs ─────────────────────────────────────────────────────
        // Plugs gate their dependants: `outputEntity` is the switch state, so a
        // plug that is online but switched OFF still means no power downstream.
        yield { node "kauf_xx" "Kauf_XX (PLF12)" "plug" with
                  device = Some "Kauf_XX (PLF12)"; entity = Some "switch.kauf_xx"
                  outputEntity = Some "switch.kauf_xx"; link = Some "ABEWNETG"; size = 1
                  powerFrom = FromDevice "ac500_2"; lan = Wifi "ABEWNETG" }
        yield { node "shelly_us" "Shelly Plug US" "plug" with
                  device = Some "Shelly Plug US"; entity = Some "switch.shellyplugus_048308deba94"
                  outputEntity = Some "switch.shellyplugus_048308deba94"
                  area = Some "Garage"; link = Some "ABEWNETG"; size = 1
                  powerFrom = FromArea "Garage"; lan = Wifi "ABEWNETG" }

        // ── Compute ─────────────────────────────────────────────────────────
        yield { node "raspi4" "AbeRaspi4" "pi" with
                  device = Some "AbeRaspi4"; link = Some "ABEWNETG"
                  powerFrom = FromDevice "kauf_xx"; lan = Wifi "ABEWNETG" }
        yield { node "raspi_zero" "Pi Zero 2 W (thermal)" "pi" with
                  entity = Some "sensor.thermal_master_p2_thermal_low"
                  area = Some "Garage"; link = Some "ABEWNETG-GAR"
                  powerFrom = FromDevice "shelly_us"; lan = Wifi "ABEWNETG-GAR" }
        yield { node "mac_studio" "M1 Mac Studio" "computer" with
                  device = Some "M1 Mac Studio"; area = Some "Game Room"; link = Some "ABEWNETG"
                  powerFrom = FromArea "Game Room"; lan = Wired "nighthawk" }
        yield { node "homeassistant" "Home Assistant" "host" with
                  device = Some "AbeHomeAssistant"; blindSpot = true; size = 3
                  remedy = Some "If HA is unreachable, suspect circuit D - switch it to LINE"
                  powerFrom = FromArea "Game Room"; lan = Wired "nighthawk"
                  area = Some "Game Room" }

        // ── Mains-only devices (no battery backup) ───────────────────────────
        yield { node "abevue2" "AbeVue2 (energy monitor)" "sensor" with
                  device = Some "AbeVue2"; entity = Some "sensor.abevue2_10_oven"; link = Some "ABEWNETG"
                  powerFrom = FromDevice "grid"; lan = Wifi "ABEWNETG" }
        yield { node "ev_charger" "Emporia EV Charger" "ev" with
                  device = Some "Emporia EV Charger"; area = Some "Garage"; link = Some "ABEWNETG"
                  powerFrom = FromDevice "grid"; lan = Wifi "ABEWNETG" }
        yield { node "studio_esp32" "StudioESP32x (BT proxy)" "esp32" with
                  device = Some "StudioESP32x"; entity = Some "sensor.studioesp32x_uptime_sensor"
                  area = Some "Studio"; link = Some "ABEWNETG"
                  remedy = Some "On mains, not battery - a utility outage takes it out even when the Bluettis are fine"
                  powerFrom = FromDevice "grid"; lan = Wifi "ABEWNETG" }

        // ── Devices ─────────────────────────────────────────────────────────
        yield { node "kasa_garage" "Kasa Garage camera" "camera" with
                  device = Some "Kasa Garage"; area = Some "Garage"; link = Some "ABEWNETG"
                  powerFrom = FromDevice "shelly_us"; lan = Wifi "ABEWNETG" }
        yield { node "cam_driveway" "CloudEdge Driveway" "camera" with
                  device = Some "CloudEdge Driveway"; area = Some "Driveway"; link = Some "ABEWNETG-GAR"
                  powerFrom = FromArea "Driveway"; lan = Wifi "ABEWNETG-GAR"
                  needsInternet = true }
        yield { node "garage_opener" "Garage Opener" "opener" with
                  device = Some "Garage Opener"; entity = Some "cover.garage_door"
                  area = Some "Garage"; link = Some "ABEWNETG"
                  powerFrom = FromDevice "ac500_2"; lan = Wifi "ABEWNETG" }
        yield { node "midea_ac" "Midea window A/C" "appliance" with
                  device = Some "Midea AC"; area = Some "Garage"; link = Some "ABEWNETG"
                  powerFrom = FromDevice "ac200m"; lan = NoLan }

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
                  remedy = Some "WAN down: LAN and WiFi keep working; cloud cameras, remote access and app-dependent devices do not"
                  powerFrom = PowerUnknown; lan = NoLan }
        // Nokia ONT at 10.0.0.1 (management address, reachable by ping from the
        // LAN). It is in BRIDGE mode - the eero holds the public IP directly
        // (eero reports double_nat: false, wan_ip 139.94.2.161) - so the ONT
        // routes nothing and serves no web UI: ports 80/443/8080/8443/22/23 are
        // all closed.
        yield { node "modem" "Nokia ONT (bridge, 10.0.0.1)" "modem" with
                  area = Some "Game Room"; size = 3
                  powerFrom = FromArea "Game Room"; lan = NoLan
                  // The internet SUPPLIES the ONT: the WAN service arrives at it,
                  // so the modem depends on the internet feed, not the reverse.
                  wanFrom = Some "internet" }
        yield { node "eero" "eero" "ap" with
                  device = Some "eero"; link = Some "AbeEero"; size = 3
                  powerFrom = FromArea "Game Room"; lan = Wired "modem"
                  area = Some "Game Room" }
        yield { node "nighthawk" "NETGEAR Nighthawk RAX80" "ap" with
                  device = Some "NETGEAR RAX80"; link = Some "ABEWNETG"; size = 3
                  powerFrom = FromArea "Game Room"; lan = Wired "eero"
                  area = Some "Game Room" }
        yield { node "dbit" "DBit router" "ap" with
                  area = Some "Game Room"; link = Some "ABEWNETG-GAR"; size = 3
                  powerFrom = FromArea "Game Room"; lan = Wired "eero" }

        // ── Areas as power consumers ────────────────────────────────────────
        // An area can itself be fed from a device, so everything in it inherits
        // that source without needing its own edge. The Game Room is on circuit
        // D, which is why losing D takes the whole room (and the network) out.
        yield { node "area_game_room" "Game Room (area)" "area" with
                  area = Some "Game Room"; size = 1
                  powerFrom = FromDevice "circuit_d" }
        yield { node "area_garage" "Garage (area)" "area" with
                  area = Some "Garage"; size = 1
                  powerFrom = FromDevice "grid" }
    ]

    /// No hardcoded edges. Every relationship is a PROPERTY of a node -
    /// powerFrom, lan, btHost, needsInternet - so each one has a reason
    /// attached, can be edited in the dashboard, and cannot silently duplicate
    /// a derived edge. The circuits are the one structural exception: their
    /// default Bluetti feed is expressed as each circuit's own powerFrom.
    let edges : Edge list = []

    /// Edges implied by each node's own powerFrom / lan fields, so the two are
    /// never out of sync. Area-sourced power and wifi produce NO line: the area
    /// rectangle already encloses the node, and an SSID is shown by its radio
    /// icon rather than a line fanning into an AP.
    let derivedEdges (ns: Node list) : Edge list = [
        for n in ns do
            match n.powerFrom with
            | FromDevice k -> yield { child = n.key; parent = k; kind = Power; note = None }
            | FromArea a   -> ()    // enclosed by the area rectangle; no line
            | PowerUnknown -> ()
            match n.lan with
            | Wired src   -> yield { child = n.key; parent = src; kind = Network; note = Some "wired" }
            | Powerline a -> ()     // wired access via the area; no line
            | Wifi _      -> ()     // shown by the radio icon
            | NoLan       -> ()
            match n.btHost with
            | Some h -> yield { child = n.key; parent = h; kind = BtHost; note = Some "BLE gateway" }
            | None   -> ()
            if n.needsInternet then
                yield { child = n.key; parent = "internet"; kind = Network; note = Some "needs internet" }
            match n.wanFrom with
            | Some w -> yield { child = n.key; parent = w; kind = Network; note = Some "WAN service" }
            | None   -> ()
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
