module Inventory.Scanner

open System
open System.Net
open System.Net.NetworkInformation
open System.Net.Http
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging
open Inventory

// ── Intermediate scan record ──────────────────────────────────────────────────
// Merges data from all sources before writing to DB.

type ScanResult = {
    ip       : string
    mac      : string option
    hostname : string option
    connType : string option   // "Wired" | "2.4G Wireless" | "5G Wireless" | "Bluetooth"
    online   : bool
    attrs    : ScanAttr list
    sources  : string list     // which scan sources contributed, e.g. ["netgear"; "eero"]
}

let private attrOf (source: string) (ts: DateTimeOffset) (key: string) (value: string) : ScanAttr option =
    if String.IsNullOrWhiteSpace(value) then None
    else Some { key = key; value = value; source = source; updatedAt = ts }

let private attrOfInt (source: string) (ts: DateTimeOffset) (key: string) (value: int) : ScanAttr option =
    if value = 0 then None
    else Some { key = key; value = string value; source = source; updatedAt = ts }

// ── ARP cache ─────────────────────────────────────────────────────────────────
// Linux: /proc/net/arp — tab/space separated, columns: IP, HW type, Flags, MAC, Mask, Interface
// Returns a map of IP → MAC for all entries with flags != 0x0 (0x0 = incomplete)

let readArpCache () : Map<string, string> =
    try
        let lines = System.IO.File.ReadAllLines("/proc/net/arp")
        lines
        |> Array.skip 1   // skip header
        |> Array.choose (fun line ->
            let parts = line.Split([|' '; '\t'|], StringSplitOptions.RemoveEmptyEntries)
            if parts.Length >= 4 then
                let ip    = parts.[0]
                let flags = parts.[2]
                let mac   = parts.[3]
                if flags <> "0x0" && mac <> "00:00:00:00:00:00" then Some (ip, mac.ToUpperInvariant())
                else None
            else None)
        |> Map.ofArray
    with _ ->
        Map.empty   // Windows dev machine or permission denied — non-fatal

// ── ICMP ping ─────────────────────────────────────────────────────────────────

let private pingHost (timeoutMs: int) (ip: string) : Async<bool> =
    async {
        try
            use pinger = new Ping()
            let! reply = pinger.SendPingAsync(ip, timeoutMs) |> Async.AwaitTask
            return reply.Status = IPStatus.Success
        with _ ->
            return false
    }

// ── Subnet expansion ──────────────────────────────────────────────────────────
// Input: "192.168.5" → produces "192.168.5.1" .. "192.168.5.254"

let private expandSubnet (prefix: string) : string list =
    [ for i in 1..254 -> $"{prefix}.{i}" ]

// ── Ping scan ────────────────────────────────────────────────────────────────
// Pings all hosts in parallel (throttled to 64 concurrent), returns responding IPs.

let private pingScan (log: ILogger) (timeoutMs: int) (subnets: string[]) : Async<string list> =
    async {
        let allIps = subnets |> Array.toList |> List.collect expandSubnet
        log.LogDebug("Ping scan: {n} addresses across {s} subnets", allIps.Length, subnets.Length)
        let! results =
            allIps
            |> List.chunkBySize 64
            |> List.map (fun chunk ->
                chunk
                |> List.map (fun ip -> async {
                    let! alive = pingHost timeoutMs ip
                    return if alive then Some ip else None
                })
                |> Async.Parallel)
            |> Async.Sequential
        let online =
            results
            |> Array.toList
            |> List.collect Array.toList
            |> List.choose id
        log.LogDebug("Ping scan: {n} hosts responded", online.Length)
        return online
    }

// ── Hostname reverse-DNS lookup ───────────────────────────────────────────────

let private resolveHostname (ip: string) : Async<string option> =
    async {
        try
            let! entry = Dns.GetHostEntryAsync(ip) |> Async.AwaitTask
            return Some entry.HostName
        with _ ->
            return None
    }

// ── Main scan orchestration ───────────────────────────────────────────────────

type ScanSources = {
    http      : HttpClient
    log       : ILogger
    cfg       : InventoryConfig
}

let runScan (src: ScanSources) (conn: SqliteConnection) : Async<int> =
    async {
        let ts = DateTimeOffset.UtcNow

        // ── 1. NETGEAR SOAP ───────────────────────────────────────────────────
        let! netgearDevices =
            if src.cfg.Netgear.Password <> "" then
                NetgearClient.fetchAll src.log src.http src.cfg.Netgear.Host src.cfg.Netgear.Password
            else
                async { return [] }

        // ── 2. Bermuda BLE scan ───────────────────────────────────────────────
        let! bleDevices = BermudaClient.fetchDevices src.log src.http

        // ── 3. eero (direct cloud API) ──────────────────────────────────
        let! eeroDevices =
            if src.cfg.Eero.Enabled then
                EeroClient.getDevices src.log src.http src.cfg
            else
                async { return [] }

        // ── 4. ICMP ping scan ─────────────────────────────────────────────────
        let! pingOnline = pingScan src.log src.cfg.PingTimeout src.cfg.Subnets

        // ── 5. ARP cache (read AFTER ping so newly pinged hosts are in the cache)
        let arpMap = readArpCache ()

        // ── 6. Merge: build a unified IP → ScanResult map ────────────────────
        // Priority: NETGEAR > eero > ping
        // NETGEAR and eero supply authoritative MAC + connType; ping fills remaining.

        let results = System.Collections.Generic.Dictionary<string, ScanResult>()

        // From NETGEAR
        for d in netgearDevices do
            if d.ip <> "" then
                let isWireless = d.connType.Contains("Wireless")
                let ngAttrs = List.choose id [
                    if isWireless then Some { key = "router"; value = "Netgear"; source = "netgear"; updatedAt = ts } else None
                    attrOf    "netgear" ts "netgear.ssid"       d.ssid
                    attrOfInt "netgear" ts "netgear.signal_dbm" d.signalDbm
                ]
                results.[d.ip] <- {
                    ip       = d.ip
                    mac      = if d.mac <> "" then Some (d.mac.ToUpperInvariant()) else None
                    hostname = if d.name <> "" then Some d.name else None
                    connType = Some d.connType
                    online   = true
                    attrs    = ngAttrs
                    sources  = ["netgear"]
                }

        // From eero (direct cloud API — accurate IP/MAC/band data)
        // Build a set of MACs already seen from Netgear (with their IPs)
        let netgearMacToIp =
            results.Values
            |> Seq.choose (fun r -> r.mac |> Option.map (fun m -> m, r.ip))
            |> dict

        for d in eeroDevices do
            if d.ip <> "" then
                let eeAttrs = List.choose id [
                    if d.connType = "wireless" then Some { key = "router"; value = "Eero"; source = "eero"; updatedAt = ts } else None
                    attrOf    "eero" ts "eero.hostname"     d.hostname
                    attrOf    "eero" ts "eero.manufacturer" d.manufacturer
                    attrOf    "eero" ts "eero.connected_to" d.connectedTo
                    attrOf    "eero" ts "eero.band"         d.band
                    attrOf    "eero" ts "eero.ssid"         d.ssid
                    attrOfInt "eero" ts "eero.signal_dbm"   d.signalDbm
                ]
                let connType =
                    match d.band with
                    | "5 GHz" | "6 GHz" -> Some "5G Wireless"
                    | "2.4 GHz"         -> Some "2.4G Wireless"
                    | _ ->
                        match d.connType with
                        | "wireless" -> Some "2.4G Wireless"
                        | "wired"    -> Some "Wired"
                        | _          -> None

                // If Netgear already has this MAC at a DIFFERENT IP, attach attrs there instead
                let eeroMac = if d.mac <> "" then Some (d.mac.ToUpperInvariant()) else None
                let ngIp =
                    match eeroMac with
                    | Some mac ->
                        match netgearMacToIp.TryGetValue(mac) with
                        | true, ip when ip <> d.ip -> Some ip
                        | _ -> None
                    | None -> None

                match ngIp with
                | Some existingIp ->
                    // Same device already in Netgear at a different IP — Eero IP is stale
                    // Append eero attrs and override connType if eero says wireless
                    let existing = results.[existingIp]
                    let betterConn =
                        match connType with
                        | Some ct when ct.Contains("Wireless") -> connType
                        | _ -> existing.connType
                    results.[existingIp] <- { existing with connType = betterConn; attrs = existing.attrs @ eeAttrs; sources = existing.sources @ ["eero"] }
                | None ->
                    match results.TryGetValue(d.ip) with
                    | true, existing ->
                        let betterHost =
                            match existing.hostname with
                            | None   -> if d.hostname <> "" then Some d.hostname else None
                            | some   -> some
                        // If eero reports wireless, override connType (eero band is authoritative)
                        let betterConn =
                            match connType with
                            | Some ct when ct.Contains("Wireless") -> connType
                            | _ -> existing.connType
                        results.[d.ip] <- { existing with hostname = betterHost; connType = betterConn; attrs = existing.attrs @ eeAttrs; sources = existing.sources @ ["eero"] }
                    | false, _ ->
                        results.[d.ip] <- {
                            ip       = d.ip
                            mac      = eeroMac
                            hostname = if d.hostname <> "" then Some d.hostname else None
                            connType = connType
                            online   = d.connected
                            attrs    = eeAttrs
                            sources  = ["eero"]
                        }

        // From ping (fill any IPs not seen in router scans)
        for ip in pingOnline do
            if not (results.ContainsKey(ip)) then
                let mac = arpMap |> Map.tryFind ip
                results.[ip] <- {
                    ip       = ip
                    mac      = mac
                    hostname = None
                    connType = None   // unknown without router data
                    online   = true
                    attrs    = []
                    sources  = ["ping"]
                }

        // Mark IPs seen by NETGEAR/eero but not ping as still online
        // (router data is authoritative for "currently connected")

        // ── 7. Reverse-DNS for entries without a hostname ─────────────────────
        let needsHostname =
            results.Values
            |> Seq.filter (fun r -> r.hostname.IsNone)
            |> Seq.map (fun r -> r.ip)
            |> Seq.toList

        let! hostnames =
            needsHostname
            |> List.map (fun ip -> async {
                let! h = resolveHostname ip
                return ip, h })
            |> Async.Parallel

        for (ip, h) in hostnames do
            match results.TryGetValue(ip), h with
            | (true, r), Some hostname -> results.[ip] <- { r with hostname = Some hostname }
            | _ -> ()

        // ── 8. Write to DB ────────────────────────────────────────────────────
        // Strategy:
        //   - Look up device by MAC (most reliable)
        //   - Fall back to lookup by IP
        //   - If still not found: create new device
        //   - Mark all devices not seen this scan as offline

        Database.setAllOffline conn
        Database.clearAllCurrentIps conn
        Database.deactivateAllAddrs conn
        Database.clearScanAttr conn "router"

        let mutable deviceCount = 0

        for kvp in results do
            let r = kvp.Value
            let ipDU = if r.ip.Contains(":") then IPv6 r.ip else IPv4 r.ip

            // Find or create device
            let existingOpt =
                match r.mac with
                | Some mac -> Database.getByMac conn mac
                | None     -> Database.getByIp  conn r.ip

            let deviceId =
                match existingOpt with
                | Some dev -> string dev.id
                | None when r.mac.IsSome ->
                    // Only create new devices when we have a MAC (authoritative identity)
                    let newId = Guid.NewGuid()
                    let device = {
                        id         = newId
                        name       = None
                        category   = "Unknown"
                        model      = None
                        webUiUrl   = None
                        haEntities = []
                        isOnline   = true
                        firstSeen  = ts
                        lastSeen   = ts
                        addrs      = []
                        ips        = []
                        notes      = []
                        scanAttrs  = []
                    }
                    Database.insertDevice conn device
                    string newId
                | None ->
                    // No MAC, no existing device — skip (avoid orphan devices)
                    ""

            if deviceId <> "" then
                // Update online status + last_seen
                Database.setOnlineStatus conn deviceId r.online

                // Upsert IP entry
                let ipSrc = r.sources |> List.distinct |> String.concat ", "
                Database.upsertIp conn deviceId {
                    ip        = ipDU
                    hostname  = r.hostname
                    pairedMac = r.mac
                    connType  = r.connType
                    isCurrent = true
                    source    = ipSrc
                    firstSeen = ts
                    lastSeen  = ts
                }

                // Upsert MAC address if known
                match r.mac with
                | Some mac ->
                    let iface  = match r.connType with Some "Wired" -> Ethernet | _ -> Wifi
                    let macSrc = r.sources |> List.distinct |> String.concat ", "
                    Database.upsertAddr conn deviceId {
                        address    = NetworkMac(mac, iface)
                        isActive   = true
                        isReserved = false
                        reservedIp = None
                        source     = macSrc
                        firstSeen  = ts
                        lastSeen   = ts
                    }
                | None -> ()

                // Upsert scan attrs
                for a in r.attrs do
                    Database.upsertScanAttr conn deviceId a

                // Append scan history
                Database.appendScanHistory conn deviceId (Some r.ip) true
                deviceCount <- deviceCount + 1

        // ── 9. BLE-only devices from Bermuda ─────────────────────────────
        for d in bleDevices do
            // Find existing device by BT address, then fall back to bermuda.name
            // (handles BLE MAC rotation where address changes every scan).
            // For iBeacon-style names (bermuda_UUID_major_minor), extract the stable
            // UUID part and look up by BeaconId address — survives major/minor rotation
            // and device merges.
            let namePrefix =
                if d.name <> "" then
                    let parts = d.name.Split('_')
                    if parts.Length >= 3 then
                        // e.g. "1ca92e...bf6_11323_0" → prefix "1ca92e...bf6"
                        String.Join("_", parts.[.. parts.Length - 3])
                    else ""
                else ""
            // Extract beacon UUID: "bermuda_<32hexchars>_major_minor" → UUID part
            let isHex (s: string) = s |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
            let beaconUuid =
                if d.name.StartsWith("bermuda_") then
                    let parts = d.name.Split('_')
                    if parts.Length >= 4 && parts.[1].Length = 32 && isHex parts.[1] then
                        parts.[1].ToLowerInvariant()
                    else ""
                else ""
            let existingOpt =
                let byAddr =
                    if d.mac <> "" then Database.getByBtAddr conn d.mac
                    elif d.irk <> "" then Database.getByIrkAddr conn d.irk
                    elif beaconUuid <> "" then Database.getByBeaconId conn beaconUuid
                    else None
                match byAddr with
                | Some _ as found -> found
                | None when d.name <> "" ->
                    match Database.getByScanAttr conn "bermuda.name" d.name with
                    | Some _ as found -> found
                    | None when namePrefix <> "" ->
                        Database.getByScanAttrPrefix conn "bermuda.name" namePrefix
                    | other -> other
                | None -> None
            let deviceId =
                match existingOpt with
                | Some dev -> string dev.id
                | None when d.isHome ->
                    // New BLE device currently home — add to inventory
                    let newId = Guid.NewGuid()
                    Database.insertDevice conn {
                        id         = newId
                        name       = if d.name <> "" then Some d.name else None
                        category   = "Unknown"
                        model      = None
                        webUiUrl   = None
                        haEntities = []
                        isOnline   = false
                        firstSeen  = ts
                        lastSeen   = ts
                        addrs      = []
                        ips        = []
                        notes      = []
                        scanAttrs  = []
                    }
                    string newId
                | None -> ""   // not home + unknown → skip

            if deviceId <> "" then
                // Only promote to online — never demote via BLE.
                // setAllOffline already ran; IP-based sources already set online where applicable.
                if d.isHome then
                    Database.setOnlineStatus conn deviceId true
                    // Device found by name with no MAC/IRK — reactivate any stored BT addresses
                    if d.mac = "" && d.irk = "" && beaconUuid = "" then
                        Database.activateBluetoothAddrs conn deviceId
                if d.mac <> "" then
                    Database.upsertAddr conn deviceId {
                        address    = BluetoothAddr d.mac
                        isActive   = d.isHome
                        isReserved = false
                        reservedIp = None
                        source     = "bermuda"
                        firstSeen  = ts
                        lastSeen   = ts
                    }
                elif d.irk <> "" then
                    src.log.LogInformation("BERMUDA: storing IRK for '{name}' ({irk}…) deviceId={id}",
                        d.name, d.irk.[..7], deviceId)
                    Database.upsertAddr conn deviceId {
                        address    = Irk d.irk
                        isActive   = d.isHome
                        isReserved = false
                        reservedIp = None
                        source     = "bermuda"
                        firstSeen  = ts
                        lastSeen   = ts
                    }
                if beaconUuid <> "" then
                    Database.upsertAddr conn deviceId {
                        address    = BeaconId beaconUuid
                        isActive   = d.isHome
                        isReserved = false
                        reservedIp = None
                        source     = "bermuda"
                        firstSeen  = ts
                        lastSeen   = ts
                    }
                match d.rssi with
                | Some v ->
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.rssi"; value = string v; source = "bermuda"; updatedAt = ts }
                | None -> ()
                if d.name <> "" then
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.name"; value = d.name; source = "bermuda"; updatedAt = ts }
                match d.area with
                | Some a ->
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.area"; value = a; source = "bermuda"; updatedAt = ts }
                | None -> ()
                match d.distance with
                | Some v ->
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.distance_m"; value = sprintf "%.2f" v; source = "bermuda"; updatedAt = ts }
                | None -> ()
                match d.nearestScanner with
                | Some s ->
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.nearest_scanner"; value = s; source = "bermuda"; updatedAt = ts }
                | None -> ()
                match d.floor with
                | Some f ->
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.floor"; value = f; source = "bermuda"; updatedAt = ts }
                | None -> ()
                match d.lastSeen with
                | Some dt ->
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.last_seen"; value = dt.ToString("o"); source = "bermuda"; updatedAt = ts }
                | None -> ()
                match d.areaLastSeen with
                | Some s ->
                    Database.upsertScanAttr conn deviceId
                        { key = "bermuda.area_last_seen"; value = s; source = "bermuda"; updatedAt = ts }
                | None -> ()
                if d.isHome then
                    Database.appendScanHistory conn deviceId None true
                    deviceCount <- deviceCount + 1

        // ── 10. OUI manufacturer lookup for MACs without one ──────────────
        // OUI = first 3 bytes (XX:XX:XX). Group by prefix so we only call the
        // API once per unique prefix, then fan out the result to all devices.
        let needsOui = Database.getMacsNeedingOui conn
        if needsOui.Length > 0 then
            let ouiPrefix (mac: string) = if mac.Length >= 8 then mac.[..7] else mac
            let grouped =
                needsOui
                |> List.groupBy (fun (_, mac) -> ouiPrefix mac)
                |> List.truncate 10   // max 10 unique prefixes per scan
            let! ouiResults =
                grouped
                |> List.map (fun (prefix, _devices) -> async {
                    try
                        do! Async.Sleep 1100   // macvendors.com rate limit: 1 req/sec
                        use req = new HttpRequestMessage(HttpMethod.Get, $"https://api.macvendors.com/{prefix}")
                        use! resp = src.http.SendAsync(req) |> Async.AwaitTask
                        if resp.IsSuccessStatusCode then
                            let! vendor = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                            return Some (prefix, vendor.Trim())
                        else return None
                    with _ -> return None
                })
                |> Async.Sequential
            let found = ouiResults |> Array.choose id |> Map.ofArray
            let mutable assigned = 0
            for (prefix, devices) in grouped do
                match found |> Map.tryFind prefix with
                | Some vendor ->
                    for (devId, _) in devices do
                        Database.upsertScanAttr conn devId
                            { key = "oui.manufacturer"; value = vendor; source = "oui"; updatedAt = ts }
                        assigned <- assigned + 1
                | None -> ()
            let uniquePrefixes = grouped.Length
            let totalDevices   = needsOui.Length
            src.log.LogInformation("OUI: {u} unique prefixes looked up ({d} devices), {f} resolved → {a} devices assigned",
                uniquePrefixes, totalDevices, found.Count, assigned)

        // ── 11. OUI manufacturer lookup for non-transient BT addresses ───────
        let needsBtOui = Database.getBtAddrsNeedingOui conn
        if needsBtOui.Length > 0 then
            let ouiPrefix (mac: string) = if mac.Length >= 8 then mac.[..7] else mac
            let grouped =
                needsBtOui
                |> List.groupBy (fun (_, mac) -> ouiPrefix mac)
                |> List.truncate 5   // max 5 unique BT prefixes per scan
            let! btOuiResults =
                grouped
                |> List.map (fun (prefix, _devices) -> async {
                    try
                        do! Async.Sleep 1100   // macvendors.com rate limit: 1 req/sec
                        use req = new HttpRequestMessage(HttpMethod.Get, $"https://api.macvendors.com/{prefix}")
                        use! resp = src.http.SendAsync(req) |> Async.AwaitTask
                        if resp.IsSuccessStatusCode then
                            let! vendor = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
                            return Some (prefix, vendor.Trim())
                        else return None
                    with _ -> return None
                })
                |> Async.Sequential
            let btFound = btOuiResults |> Array.choose id |> Map.ofArray
            let mutable btAssigned = 0
            for (prefix, devices) in grouped do
                match btFound |> Map.tryFind prefix with
                | Some vendor ->
                    for (devId, _) in devices do
                        Database.upsertScanAttr conn devId
                            { key = "bt.manufacturer"; value = vendor; source = "oui"; updatedAt = ts }
                        btAssigned <- btAssigned + 1
                | None -> ()
            src.log.LogInformation("BT OUI: {u} prefixes looked up, {f} resolved → {a} devices assigned",
                grouped.Length, btFound.Count, btAssigned)

        src.log.LogInformation("Scan complete: {n} devices found/updated", deviceCount)
        return deviceCount
    }
