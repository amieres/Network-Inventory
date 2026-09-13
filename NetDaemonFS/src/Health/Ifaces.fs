module Health.Ifaces

open System
open Microsoft.Data.Sqlite

// ── Device interfaces from the inventory ─────────────────────────────────────
// The topology should not re-declare MACs and IPs: the inventory already scans
// them. A node names an inventory device, and its interfaces are read from
// there - which also means a device with TWO network interfaces (the Mac Studio
// has ethernet and wifi, different MACs and different IPs) shows an icon for
// each, and a device with a bluetooth address gets a bluetooth icon.

type Kind =
    | Wifi
    | Ethernet
    | Bluetooth
    | Other
    with
        member this.label =
            match this with
            | Wifi -> "wifi" | Ethernet -> "wired" | Bluetooth -> "bluetooth" | Other -> "other"

type Iface = {
    kind : Kind
    mac  : string
    ip   : string option
    /// Router-reported connection, e.g. "5G Wireless" / "Wired" / "2.4G Wireless".
    conn : string option
}

/// Interfaces keyed by inventory device NAME (the same key the topology uses).
let load (conn: SqliteConnection) : Map<string, Iface list> =
    use cmd = conn.CreateCommand()
    // Pair each address with the IP that the scanner saw on that same MAC.
    cmd.CommandText <- """
SELECT d.name, a.addr_type, a.address, a.label, i.ip, i.conn_type
FROM   devices d
-- Bluetooth addresses are included regardless of is_active: BLE devices rotate
-- random MACs, so most bluetooth rows are marked inactive even when the device
-- is perfectly healthy (the AC500's permanent BT address is flagged inactive).
JOIN   device_addrs a ON a.device_id = d.id
                     AND (a.is_active = 1 OR a.addr_type = 'bluetooth')
LEFT   JOIN device_ips i ON i.device_id = d.id AND i.is_current = 1
                        AND UPPER(i.paired_mac) = UPPER(a.address)
WHERE  d.name IS NOT NULL"""
    use r = cmd.ExecuteReader()
    let acc = ResizeArray()
    while r.Read() do
        let name     = r.GetString 0
        let addrType = r.GetString 1
        let address  = r.GetString 2
        // `iface` is stored in the addr LABEL column ('wifi' / 'ethernet'), not a
        // column of its own - see Inventory.Database.rowToAddr.
        let iface    = if r.IsDBNull 3 then None else Some (r.GetString 3)
        let ip       = if r.IsDBNull 4 then None else Some (r.GetString 4)
        let connType = if r.IsDBNull 5 then None else Some (r.GetString 5)
        let kind =
            match addrType, iface with
            | "bluetooth", _        -> Bluetooth
            | _, Some "ethernet"    -> Ethernet
            | _, Some "wifi"        -> Wifi
            | _, _ ->
                // No iface recorded: fall back to what the router reported.
                match connType with
                | Some c when c.Contains "Wired" -> Ethernet
                | Some _                          -> Wifi
                | None                            -> Other
        acc.Add(name, { kind = kind; mac = address; ip = ip; conn = connType })
    acc
    |> Seq.groupBy fst
    |> Seq.map (fun (name, xs) ->
        name,
        xs |> Seq.map snd
           |> Seq.distinctBy (fun i -> i.mac)
           // Wired first, then wifi, then bluetooth - reads left to right.
           |> Seq.sortBy (fun i -> match i.kind with Ethernet -> 0 | Wifi -> 1 | Bluetooth -> 2 | Other -> 3)
           |> List.ofSeq)
    |> Map.ofSeq
