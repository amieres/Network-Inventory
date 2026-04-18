namespace Inventory

open System

// ── IP address ───────────────────────────────────────────────────────────────
// Version is the union case — no separate ip_ver field needed in domain code.
// IPv6 subtypes to expect:
//   fe80::/10  link-local  (mDNS, BLE, per-interface)
//   fc00::/7   unique-local (ULA, like private IPv4)
//   2xxx::     global unicast
type Ip =
    | IPv4 of string   // "192.168.5.70"
    | IPv6 of string   // "fe80::9ceb:e8ff:fe1b:2935"
    with
        member this.value = match this with IPv4 s -> s | IPv6 s -> s
        member this.ver   = match this with IPv4 _ -> 4  | IPv6 _ -> 6

// ── Hardware / radio address ─────────────────────────────────────────────────
// MacLabel: which physical interface the MAC belongs to.
//   Band (2.4G vs 5G) is connection-time info — stored in IpEntry.connType,
//   not on the MAC itself.
//   Source: inferred from NETGEAR ConnectionType at time of first observation.
type MacIface = Wifi | Ethernet

// Address DU — type and value in one union; Database.fs is the only place
// that serialises/deserialises these to/from addr_type + address + label columns.
type Address =
    | NetworkMac    of mac  : string * iface : MacIface  // IEEE 802 WiFi or Ethernet MAC
    | BluetoothAddr of addr : string                     // BT Classic or BLE public/static
    | Irk           of key  : string                     // BLE Identity Resolving Key (128-bit hex)
    | Rpa           of addr : string                     // BLE Resolvable Private Addr (transient ~15 min)
    | BeaconId      of uuid : string                     // iBeacon UUID (stable part of bermuda_UUID_major_minor name)
    with
        member this.rawAddress =
            match this with
            | NetworkMac(mac, _)   -> mac
            | BluetoothAddr addr   -> addr
            | Irk key              -> key
            | Rpa addr             -> addr
            | BeaconId uuid        -> uuid

// Address + scan metadata (one row in device_addrs)
type AddrEntry = {
    address    : Address
    isActive   : bool
    isReserved : bool
    reservedIp : string option  // DHCP reservation: the IP bound to this MAC
    source     : string         // which scanner discovered it: "netgear" | "eero" | "bermuda" | "arp" | ...
    firstSeen  : DateTimeOffset
    lastSeen   : DateTimeOffset
}

// ── IP entry ─────────────────────────────────────────────────────────────────
// IP + metadata + MAC pairing (one row in device_ips).
// pairedMac: raw MAC string seen with this IP in the same scan observation —
//   lets us know which interface/MAC corresponds to which IP on multi-NIC devices
//   (e.g. Amcrest: 9C:8E:CD:1F:20:5C wired ↔ 192.168.5.45).
// connType: "Wired" | "2.4G Wireless" | "5G Wireless" | "Bluetooth" | "—"
//   Band info lives here (connection-time), not on the MAC.
type IpEntry = {
    ip        : Ip
    hostname  : string option
    pairedMac : string option
    connType  : string option
    isCurrent : bool
    source    : string         // which scanner discovered it: "netgear" | "eero" | "ping" | ...
    firstSeen : DateTimeOffset
    lastSeen  : DateTimeOffset
}

// ── Note ─────────────────────────────────────────────────────────────────────
// Timestamped append-only note entry (one row in device_notes)
type Note = {
    id        : int
    note      : string
    createdAt : DateTimeOffset
}

// ── Scan attribute ───────────────────────────────────────────────────────────
// Key/value data collected by scanners that doesn't fit core fields.
// Upserted per scan (key is unique per device); value is a string for storage
// flexibility. Source tags the origin: "netgear" | "eero" | "bermuda" | ...
type ScanAttr = {
    key       : string      // e.g. "eero.nickname", "netgear.ssid", "bermuda.rssi"
    value     : string
    source    : string
    updatedAt : DateTimeOffset
}

// ── Device ───────────────────────────────────────────────────────────────────
// UUID primary key — stable across IP changes, MAC randomisation, and address
// changes.  All lists are joined from child tables and populated on GET.
type Device = {
    id         : Guid
    name       : string option
    category   : string
    model      : string option
    webUiUrl   : string option
    haEntities : string list
    isOnline   : bool
    firstSeen  : DateTimeOffset
    lastSeen   : DateTimeOffset
    addrs      : AddrEntry list
    ips        : IpEntry   list
    notes      : Note      list
    scanAttrs  : ScanAttr  list
}

// ── Editable update payload (PUT /api/devices/{id}) ─────────────────────────
// Only scalar fields — lists (haEntities, notes, addrs) are managed via
// their own sub-endpoints.
type DeviceUpdate = {
    name     : string option
    category : string option
    model    : string option
    webUiUrl : string option
}

// ── Scan status ──────────────────────────────────────────────────────────────
type ScanStatus = {
    isRunning         : bool
    lastScanStarted   : DateTimeOffset option
    lastScanCompleted : DateTimeOffset option
    devicesFound      : int
}

// ── Stats ────────────────────────────────────────────────────────────────────
type Stats = {
    total      : int
    online     : int
    wired      : int
    wireless24 : int
    wireless5  : int
    bluetooth  : int
    withWebUi  : int
    unknown    : int
}
