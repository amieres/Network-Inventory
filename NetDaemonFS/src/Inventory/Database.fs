module Inventory.Database

open System
open System.Data
open Microsoft.Data.Sqlite
open Inventory

// ── Connection helper ─────────────────────────────────────────────────────────

let openConn (dbPath: string) : SqliteConnection =
    let conn = new SqliteConnection($"Data Source={dbPath};Foreign Keys=True")
    conn.Open()
    conn

// Microsoft.Data.Sqlite requires DBNull.Value (not null) for SQL NULL parameters.
let private dbv (v: string option) : obj =
    match v with
    | Some s -> box s
    | None   -> box DBNull.Value

// ── Schema migration ──────────────────────────────────────────────────────────

let migrate (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS devices (
    id         TEXT    PRIMARY KEY NOT NULL,
    name       TEXT,
    category   TEXT    NOT NULL DEFAULT 'Unknown',
    model      TEXT,
    web_ui_url TEXT,
    is_online  INTEGER NOT NULL DEFAULT 0,
    first_seen TEXT    NOT NULL,
    last_seen  TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS device_addrs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id  TEXT    NOT NULL,
    addr_type  TEXT    NOT NULL,
    address    TEXT    NOT NULL,
    label      TEXT,
    is_active  INTEGER NOT NULL DEFAULT 1,
    first_seen TEXT    NOT NULL,
    last_seen  TEXT    NOT NULL,
    UNIQUE (device_id, addr_type, address),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS device_ips (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id  TEXT    NOT NULL,
    ip         TEXT    NOT NULL,
    ip_ver     INTEGER NOT NULL DEFAULT 4,
    hostname   TEXT,
    paired_mac TEXT,
    conn_type  TEXT,
    is_current INTEGER NOT NULL DEFAULT 1,
    first_seen TEXT    NOT NULL,
    last_seen  TEXT    NOT NULL,
    UNIQUE (device_id, ip),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS device_ha_entities (
    device_id TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    PRIMARY KEY (device_id, entity_id),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS device_notes (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id  TEXT    NOT NULL,
    note       TEXT    NOT NULL,
    created_at TEXT    NOT NULL,
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS device_scan_attrs (
    device_id  TEXT NOT NULL,
    key        TEXT NOT NULL,
    value      TEXT NOT NULL,
    source     TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (device_id, key),
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS scan_history (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    scanned_at TEXT    NOT NULL,
    device_id  TEXT    NOT NULL,
    ip         TEXT,
    is_online  INTEGER NOT NULL,
    FOREIGN KEY (device_id) REFERENCES devices(id) ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_addrs_lookup ON device_addrs(addr_type, address);
CREATE        INDEX IF NOT EXISTS idx_ips_ip       ON device_ips(ip);
CREATE        INDEX IF NOT EXISTS idx_ips_current  ON device_ips(device_id, is_current);
"""
    cmd.ExecuteNonQuery() |> ignore

    // ── Incremental migrations ──────────────────────────────────────────
    // Add source column to device_addrs (which scanner discovered this addr)
    use chk = conn.CreateCommand()
    chk.CommandText <- "SELECT COUNT(*) FROM pragma_table_info('device_addrs') WHERE name = 'source'"
    let hasSource = (chk.ExecuteScalar() :?> int64) > 0L
    if not hasSource then
        use add = conn.CreateCommand()
        add.CommandText <- "ALTER TABLE device_addrs ADD COLUMN source TEXT NOT NULL DEFAULT ''"
        add.ExecuteNonQuery() |> ignore

    // Add DHCP reservation columns to device_addrs
    use chkR = conn.CreateCommand()
    chkR.CommandText <- "SELECT COUNT(*) FROM pragma_table_info('device_addrs') WHERE name = 'is_reserved'"
    let hasReserved = (chkR.ExecuteScalar() :?> int64) > 0L
    if not hasReserved then
        use a1 = conn.CreateCommand()
        a1.CommandText <- "ALTER TABLE device_addrs ADD COLUMN is_reserved INTEGER NOT NULL DEFAULT 0"
        a1.ExecuteNonQuery() |> ignore
        use a2 = conn.CreateCommand()
        a2.CommandText <- "ALTER TABLE device_addrs ADD COLUMN reserved_ip TEXT"
        a2.ExecuteNonQuery() |> ignore

    // Add source column to device_ips
    use chk2 = conn.CreateCommand()
    chk2.CommandText <- "SELECT COUNT(*) FROM pragma_table_info('device_ips') WHERE name = 'source'"
    let hasIpSource = (chk2.ExecuteScalar() :?> int64) > 0L
    if not hasIpSource then
        use add2 = conn.CreateCommand()
        add2.CommandText <- "ALTER TABLE device_ips ADD COLUMN source TEXT NOT NULL DEFAULT ''"
        add2.ExecuteNonQuery() |> ignore

// ── Address serialisation ─────────────────────────────────────────────────────

let private addrToRow (addr: Address) =
    match addr with
    | NetworkMac(mac, Wifi)     -> "mac",       mac,  Some "wifi"
    | NetworkMac(mac, Ethernet) -> "mac",       mac,  Some "ethernet"
    | BluetoothAddr a           -> "bluetooth", a,    None
    | Irk k                     -> "irk",       k,    None
    | Rpa a                     -> "rpa",       a,    None
    | BeaconId u                -> "beacon_id", u,    None

let private rowToAddr (addrType: string) (address: string) (label: string option) : Address option =
    match addrType, label with
    | "mac",       Some "wifi"     -> Some (NetworkMac(address, Wifi))
    | "mac",       Some "ethernet" -> Some (NetworkMac(address, Ethernet))
    | "mac",       _               -> Some (NetworkMac(address, Wifi))   // default wifi if label missing
    | "bluetooth", _               -> Some (BluetoothAddr address)
    | "irk",       _               -> Some (Irk address)
    | "rpa",       _               -> Some (Rpa address)
    | "beacon_id", _               -> Some (BeaconId address)
    | _                            -> None

// ── IP serialisation ──────────────────────────────────────────────────────────

let private ipToRow (ip: Ip) = ip.value, ip.ver

let private rowToIp (ipStr: string) (ipVer: int) : Ip =
    if ipVer = 6 then IPv6 ipStr else IPv4 ipStr

// ── Null helpers ──────────────────────────────────────────────────────────────

let private str (r: IDataReader) col =
    let i = r.GetOrdinal(col)
    if r.IsDBNull(i) then None else Some (r.GetString(i))

let private strReq (r: IDataReader) col =
    r.GetString(r.GetOrdinal(col))

let private intReq (r: IDataReader) col =
    r.GetInt32(r.GetOrdinal(col))

let private boolInt (r: IDataReader) col =
    r.GetInt32(r.GetOrdinal(col)) <> 0

let private dto (r: IDataReader) col =
    DateTimeOffset.Parse(r.GetString(r.GetOrdinal(col)))

// ── Read helpers ──────────────────────────────────────────────────────────────

let private readAddrs (conn: SqliteConnection) (deviceId: string) : AddrEntry list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT addr_type, address, label, is_active, source, is_reserved, reserved_ip, first_seen, last_seen FROM device_addrs WHERE device_id = @id"
    cmd.Parameters.AddWithValue("@id", deviceId) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do
        let addrType = strReq r "addr_type"
        let address  = strReq r "address"
        let label    = str r "label"
        match rowToAddr addrType address label with
        | Some addr ->
            yield {
                address    = addr
                isActive   = boolInt r "is_active"
                isReserved = boolInt r "is_reserved"
                reservedIp = str r "reserved_ip"
                source     = strReq r "source"
                firstSeen  = dto r "first_seen"
                lastSeen   = dto r "last_seen"
            }
        | None -> () ]

let private readIps (conn: SqliteConnection) (deviceId: string) : IpEntry list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT ip, ip_ver, hostname, paired_mac, conn_type, is_current, source, first_seen, last_seen FROM device_ips WHERE device_id = @id"
    cmd.Parameters.AddWithValue("@id", deviceId) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do
        yield {
            ip        = rowToIp (strReq r "ip") (intReq r "ip_ver")
            hostname  = str r "hostname"
            pairedMac = str r "paired_mac"
            connType  = str r "conn_type"
            isCurrent = boolInt r "is_current"
            source    = strReq r "source"
            firstSeen = dto r "first_seen"
            lastSeen  = dto r "last_seen"
        } ]

let private readEntities (conn: SqliteConnection) (deviceId: string) : string list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT entity_id FROM device_ha_entities WHERE device_id = @id"
    cmd.Parameters.AddWithValue("@id", deviceId) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do yield r.GetString(0) ]

let private readNotes (conn: SqliteConnection) (deviceId: string) : Note list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT id, note, created_at FROM device_notes WHERE device_id = @id ORDER BY created_at"
    cmd.Parameters.AddWithValue("@id", deviceId) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do
        yield { id = intReq r "id"; note = strReq r "note"; createdAt = dto r "created_at" } ]

let private readScanAttrs (conn: SqliteConnection) (deviceId: string) : ScanAttr list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT key, value, source, updated_at FROM device_scan_attrs WHERE device_id = @id ORDER BY key"
    cmd.Parameters.AddWithValue("@id", deviceId) |> ignore
    use r = cmd.ExecuteReader()
    [ while r.Read() do
        yield { key       = strReq r "key"
                value     = strReq r "value"
                source    = strReq r "source"
                updatedAt = dto r "updated_at" } ]

let private hydrateDevice (conn: SqliteConnection) (r: IDataReader) : Device =
    let id = strReq r "id"
    { id         = Guid.Parse(id)
      name       = str r "name"
      category   = strReq r "category"
      model      = str r "model"
      webUiUrl   = str r "web_ui_url"
      isOnline   = boolInt r "is_online"
      firstSeen  = dto r "first_seen"
      lastSeen   = dto r "last_seen"
      haEntities = readEntities  conn id
      addrs      = readAddrs     conn id
      ips        = readIps       conn id
      notes      = readNotes     conn id
      scanAttrs  = readScanAttrs conn id }

// ── Device queries ────────────────────────────────────────────────────────────

let getAll (conn: SqliteConnection) : Device list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT id, name, category, model, web_ui_url, is_online, first_seen, last_seen FROM devices ORDER BY last_seen DESC"
    use r = cmd.ExecuteReader()
    [ while r.Read() do yield hydrateDevice conn r ]

let getById (conn: SqliteConnection) (id: Guid) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT id, name, category, model, web_ui_url, is_online, first_seen, last_seen FROM devices WHERE id = @id"
    cmd.Parameters.AddWithValue("@id", string id) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

let getByIp (conn: SqliteConnection) (ip: string) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT d.id, d.name, d.category, d.model, d.web_ui_url, d.is_online, d.first_seen, d.last_seen
        FROM devices d
        JOIN device_ips i ON i.device_id = d.id
        WHERE i.ip = @ip AND i.is_current = 1
        LIMIT 1"""
    cmd.Parameters.AddWithValue("@ip", ip) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

let getByMac (conn: SqliteConnection) (mac: string) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT d.id, d.name, d.category, d.model, d.web_ui_url, d.is_online, d.first_seen, d.last_seen
        FROM devices d
        JOIN device_addrs a ON a.device_id = d.id
        WHERE a.addr_type = 'mac' AND a.address = @mac
        LIMIT 1"""
    cmd.Parameters.AddWithValue("@mac", mac) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

let getByBtAddr (conn: SqliteConnection) (mac: string) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT d.id, d.name, d.category, d.model, d.web_ui_url, d.is_online, d.first_seen, d.last_seen
        FROM devices d
        JOIN device_addrs a ON a.device_id = d.id
        WHERE a.addr_type = 'bluetooth' AND UPPER(a.address) = UPPER(@mac)
        LIMIT 1"""
    cmd.Parameters.AddWithValue("@mac", mac) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

let getByIrkAddr (conn: SqliteConnection) (irk: string) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT d.id, d.name, d.category, d.model, d.web_ui_url, d.is_online, d.first_seen, d.last_seen
        FROM devices d
        JOIN device_addrs a ON a.device_id = d.id
        WHERE a.addr_type = 'irk' AND LOWER(a.address) = LOWER(@irk)
        LIMIT 1"""
    cmd.Parameters.AddWithValue("@irk", irk) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

let getByBeaconId (conn: SqliteConnection) (uuid: string) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT d.id, d.name, d.category, d.model, d.web_ui_url, d.is_online, d.first_seen, d.last_seen
        FROM devices d
        JOIN device_addrs a ON a.device_id = d.id
        WHERE a.addr_type = 'beacon_id' AND LOWER(a.address) = LOWER(@uuid)
        LIMIT 1"""
    cmd.Parameters.AddWithValue("@uuid", uuid) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

let getByScanAttr (conn: SqliteConnection) (key: string) (value: string) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT d.id, d.name, d.category, d.model, d.web_ui_url, d.is_online, d.first_seen, d.last_seen
        FROM devices d
        JOIN device_scan_attrs a ON a.device_id = d.id
        WHERE a.key = @key AND a.value = @value
        LIMIT 1"""
    cmd.Parameters.AddWithValue("@key",   key)   |> ignore
    cmd.Parameters.AddWithValue("@value", value) |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

let getByScanAttrPrefix (conn: SqliteConnection) (key: string) (prefix: string) : Device option =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT d.id, d.name, d.category, d.model, d.web_ui_url, d.is_online, d.first_seen, d.last_seen
        FROM devices d
        JOIN device_scan_attrs a ON a.device_id = d.id
        WHERE a.key = @key AND a.value LIKE @prefix
        LIMIT 1"""
    cmd.Parameters.AddWithValue("@key",    key)           |> ignore
    cmd.Parameters.AddWithValue("@prefix", prefix + "%")  |> ignore
    use r = cmd.ExecuteReader()
    if r.Read() then Some (hydrateDevice conn r) else None

/// Returns (deviceId, mac) pairs for devices that have a MAC or BT address but no manufacturer attr.
/// Skips locally administered MACs (second hex char in 2367ABEFabef) and transient BT (first char 0-B).
let getMacsNeedingOui (conn: SqliteConnection) : (string * string) list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT a.device_id, a.address
        FROM device_addrs a
        WHERE a.addr_type IN ('mac', 'bluetooth')
          AND SUBSTR(a.address, 2, 1) NOT IN ('2','3','6','7','A','a','B','b','E','e','F','f')
          AND NOT (a.addr_type = 'bluetooth'
                   AND UPPER(SUBSTR(a.address, 1, 1)) NOT IN ('0','1','2','3'))
          AND NOT EXISTS (
              SELECT 1 FROM device_scan_attrs s
              WHERE s.device_id = a.device_id
                AND s.key IN ('oui.manufacturer', 'eero.manufacturer')
          )"""
    use r = cmd.ExecuteReader()
    [ while r.Read() do yield (r.GetString(0), r.GetString(1)) ]

/// Returns (deviceId, address) pairs for non-transient BT addresses (first nibble
/// not 4-7 RPA or 8-B reserved) that have no bt.manufacturer attr yet.
let getBtAddrsNeedingOui (conn: SqliteConnection) : (string * string) list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT a.device_id, a.address
        FROM device_addrs a
        WHERE a.addr_type = 'bluetooth'
          AND LENGTH(a.address) >= 8
          AND UPPER(SUBSTR(a.address, 1, 1)) NOT IN ('4','5','6','7','8','9','A','B')
          AND NOT EXISTS (
              SELECT 1 FROM device_scan_attrs s
              WHERE s.device_id = a.device_id
                AND s.key = 'bt.manufacturer'
          )"""
    use r = cmd.ExecuteReader()
    [ while r.Read() do yield (r.GetString(0), r.GetString(1)) ]

// ── Device upsert (used by scanner) ──────────────────────────────────────────

let private now () = DateTimeOffset.UtcNow.ToString("o")

/// Insert a new device row; caller owns the transaction.
let insertDevice (conn: SqliteConnection) (d: Device) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        INSERT INTO devices (id, name, category, model, web_ui_url, is_online, first_seen, last_seen)
        VALUES (@id, @name, @cat, @model, @url, @online, @fs, @ls)
        ON CONFLICT(id) DO UPDATE SET
            is_online = excluded.is_online,
            last_seen = excluded.last_seen"""
    cmd.Parameters.AddWithValue("@id",     string d.id)                 |> ignore
    cmd.Parameters.AddWithValue("@name",   d.name |> dbv)      |> ignore
    cmd.Parameters.AddWithValue("@cat",    d.category)                  |> ignore
    cmd.Parameters.AddWithValue("@model",  d.model |> dbv)     |> ignore
    cmd.Parameters.AddWithValue("@url",    d.webUiUrl |> dbv)  |> ignore
    cmd.Parameters.AddWithValue("@online", if d.isOnline then 1 else 0) |> ignore
    cmd.Parameters.AddWithValue("@fs",     d.firstSeen.ToString("o"))   |> ignore
    cmd.Parameters.AddWithValue("@ls",     d.lastSeen.ToString("o"))    |> ignore
    cmd.ExecuteNonQuery() |> ignore

let upsertAddr (conn: SqliteConnection) (deviceId: string) (entry: AddrEntry) =
    let (addrType, address, label) = addrToRow entry.address
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        INSERT INTO device_addrs (device_id, addr_type, address, label, is_active, source, first_seen, last_seen)
        VALUES (@did, @type, @addr, @label, @active, @src, @fs, @ls)
        ON CONFLICT(device_id, addr_type, address) DO UPDATE SET
            is_active = excluded.is_active,
            source    = CASE
                            WHEN device_addrs.source = '' THEN excluded.source
                            WHEN INSTR(device_addrs.source, excluded.source) > 0 THEN device_addrs.source
                            ELSE device_addrs.source || ', ' || excluded.source
                        END,
            last_seen = excluded.last_seen"""
    cmd.Parameters.AddWithValue("@did",    deviceId)                         |> ignore
    cmd.Parameters.AddWithValue("@type",   addrType)                         |> ignore
    cmd.Parameters.AddWithValue("@addr",   address)                          |> ignore
    cmd.Parameters.AddWithValue("@label",  label |> dbv)            |> ignore
    cmd.Parameters.AddWithValue("@active", if entry.isActive then 1 else 0)  |> ignore
    cmd.Parameters.AddWithValue("@src",    entry.source)                     |> ignore
    cmd.Parameters.AddWithValue("@fs",     entry.firstSeen.ToString("o"))    |> ignore
    cmd.Parameters.AddWithValue("@ls",     entry.lastSeen.ToString("o"))     |> ignore
    cmd.ExecuteNonQuery() |> ignore

let upsertIp (conn: SqliteConnection) (deviceId: string) (entry: IpEntry) =
    let (ipStr, ipVer) = ipToRow entry.ip
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        INSERT INTO device_ips (device_id, ip, ip_ver, hostname, paired_mac, conn_type, is_current, source, first_seen, last_seen)
        VALUES (@did, @ip, @ver, @host, @pmac, @conn, @cur, @src, @fs, @ls)
        ON CONFLICT(device_id, ip) DO UPDATE SET
            hostname   = COALESCE(excluded.hostname,   hostname),
            paired_mac = COALESCE(excluded.paired_mac, paired_mac),
            conn_type  = COALESCE(excluded.conn_type,  conn_type),
            is_current = excluded.is_current,
            source     = CASE
                             WHEN device_ips.source = '' THEN excluded.source
                             WHEN INSTR(device_ips.source, excluded.source) > 0 THEN device_ips.source
                             ELSE device_ips.source || ', ' || excluded.source
                         END,
            last_seen  = excluded.last_seen"""
    cmd.Parameters.AddWithValue("@did",  deviceId)                          |> ignore
    cmd.Parameters.AddWithValue("@ip",   ipStr)                             |> ignore
    cmd.Parameters.AddWithValue("@ver",  ipVer)                             |> ignore
    cmd.Parameters.AddWithValue("@host", entry.hostname  |> dbv)   |> ignore
    cmd.Parameters.AddWithValue("@pmac", entry.pairedMac |> dbv)   |> ignore
    cmd.Parameters.AddWithValue("@conn", entry.connType  |> dbv)   |> ignore
    cmd.Parameters.AddWithValue("@cur",  if entry.isCurrent then 1 else 0)  |> ignore
    cmd.Parameters.AddWithValue("@src",  entry.source)                      |> ignore
    cmd.Parameters.AddWithValue("@fs",   entry.firstSeen.ToString("o"))     |> ignore
    cmd.Parameters.AddWithValue("@ls",   entry.lastSeen.ToString("o"))      |> ignore
    cmd.ExecuteNonQuery() |> ignore

/// Mark all IPs for a device as not current (called before upserting new scan IPs).
let markIpsStale (conn: SqliteConnection) (deviceId: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "UPDATE device_ips SET is_current = 0 WHERE device_id = @id"
    cmd.Parameters.AddWithValue("@id", deviceId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let setOnlineStatus (conn: SqliteConnection) (deviceId: string) (online: bool) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "UPDATE devices SET is_online = @v, last_seen = @ls WHERE id = @id"
    cmd.Parameters.AddWithValue("@v",  if online then 1 else 0) |> ignore
    cmd.Parameters.AddWithValue("@ls", now ())                  |> ignore
    cmd.Parameters.AddWithValue("@id", deviceId)                |> ignore
    cmd.ExecuteNonQuery() |> ignore

let setAllOffline (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "UPDATE devices SET is_online = 0"
    cmd.ExecuteNonQuery() |> ignore

let clearAllCurrentIps (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "UPDATE device_ips SET is_current = 0"
    cmd.ExecuteNonQuery() |> ignore

let deactivateAllAddrs (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "UPDATE device_addrs SET is_active = 0"
    cmd.ExecuteNonQuery() |> ignore

let activateBluetoothAddrs (conn: SqliteConnection) (deviceId: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "UPDATE device_addrs SET is_active = 1 WHERE device_id = @id AND addr_type = 'bluetooth'"
    cmd.Parameters.AddWithValue("@id", deviceId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

let clearScanAttr (conn: SqliteConnection) (key: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "DELETE FROM device_scan_attrs WHERE key = @key"
    cmd.Parameters.AddWithValue("@key", key) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Device update (PUT handler) ───────────────────────────────────────────────

let updateDevice (conn: SqliteConnection) (id: Guid) (upd: DeviceUpdate) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        UPDATE devices SET
            name       = COALESCE(@name, name),
            category   = COALESCE(@cat,  category),
            model      = COALESCE(@model, model),
            web_ui_url = COALESCE(@url,  web_ui_url)
        WHERE id = @id"""
    cmd.Parameters.AddWithValue("@name",  upd.name     |> dbv) |> ignore
    cmd.Parameters.AddWithValue("@cat",   upd.category |> dbv) |> ignore
    cmd.Parameters.AddWithValue("@model", upd.model    |> dbv) |> ignore
    cmd.Parameters.AddWithValue("@url",   upd.webUiUrl |> dbv) |> ignore
    cmd.Parameters.AddWithValue("@id",    string id)                    |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Device delete ─────────────────────────────────────────────────────────────

let deleteDevice (conn: SqliteConnection) (id: Guid) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "DELETE FROM devices WHERE id = @id"
    cmd.Parameters.AddWithValue("@id", string id) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Merge two devices ─────────────────────────────────────────────────────────
// Re-parents all child rows from mergeId → keepId, then deletes mergeId.

let mergeDevices (conn: SqliteConnection) (keepId: Guid) (mergeId: Guid) =
    use tx = conn.BeginTransaction()
    let keepStr  = string keepId
    let mergeStr = string mergeId
    let exec sql (p: (string * obj) list) =
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- sql
        for (k, v) in p do cmd.Parameters.AddWithValue(k, v) |> ignore
        cmd.ExecuteNonQuery() |> ignore
    // Re-parent child rows (ignore conflicts — keep already has the data)
    exec "UPDATE OR IGNORE device_addrs      SET device_id = @k WHERE device_id = @m" ["@k", keepStr; "@m", mergeStr]
    exec "UPDATE OR IGNORE device_ips        SET device_id = @k WHERE device_id = @m" ["@k", keepStr; "@m", mergeStr]
    exec "UPDATE OR IGNORE device_ha_entities SET device_id = @k WHERE device_id = @m" ["@k", keepStr; "@m", mergeStr]
    exec "INSERT OR IGNORE INTO device_notes (device_id, note, created_at) SELECT @k, note, created_at FROM device_notes WHERE device_id = @m" ["@k", keepStr; "@m", mergeStr]
    exec "INSERT OR IGNORE INTO device_scan_attrs (device_id, key, value, source, updated_at) SELECT @k, key, value, source, updated_at FROM device_scan_attrs WHERE device_id = @m" ["@k", keepStr; "@m", mergeStr]
    exec "DELETE FROM devices WHERE id = @m" ["@m", mergeStr]
    tx.Commit()

// ── HA entities ───────────────────────────────────────────────────────────────

let addEntity (conn: SqliteConnection) (deviceId: Guid) (entityId: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "INSERT OR IGNORE INTO device_ha_entities (device_id, entity_id) VALUES (@did, @eid)"
    cmd.Parameters.AddWithValue("@did", string deviceId) |> ignore
    cmd.Parameters.AddWithValue("@eid", entityId)        |> ignore
    cmd.ExecuteNonQuery() |> ignore

let removeEntity (conn: SqliteConnection) (deviceId: Guid) (entityId: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "DELETE FROM device_ha_entities WHERE device_id = @did AND entity_id = @eid"
    cmd.Parameters.AddWithValue("@did", string deviceId) |> ignore
    cmd.Parameters.AddWithValue("@eid", entityId)        |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Notes ─────────────────────────────────────────────────────────────────────

let addNote (conn: SqliteConnection) (deviceId: Guid) (text: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "INSERT INTO device_notes (device_id, note, created_at) VALUES (@did, @note, @ts)"
    cmd.Parameters.AddWithValue("@did",  string deviceId)        |> ignore
    cmd.Parameters.AddWithValue("@note", text)                   |> ignore
    cmd.Parameters.AddWithValue("@ts",   now ())                 |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Scan attrs ────────────────────────────────────────────────────────────────

let upsertScanAttr (conn: SqliteConnection) (deviceId: string) (attr: ScanAttr) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        INSERT INTO device_scan_attrs (device_id, key, value, source, updated_at)
        VALUES (@did, @k, @v, @s, @ts)
        ON CONFLICT(device_id, key) DO UPDATE SET
            value      = excluded.value,
            source     = excluded.source,
            updated_at = excluded.updated_at"""
    cmd.Parameters.AddWithValue("@did", deviceId)                       |> ignore
    cmd.Parameters.AddWithValue("@k",   attr.key)                       |> ignore
    cmd.Parameters.AddWithValue("@v",   attr.value)                     |> ignore
    cmd.Parameters.AddWithValue("@s",   attr.source)                    |> ignore
    cmd.Parameters.AddWithValue("@ts",  attr.updatedAt.ToString("o"))   |> ignore
    cmd.ExecuteNonQuery() |> ignore

let deleteNote (conn: SqliteConnection) (noteId: int) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "DELETE FROM device_notes WHERE id = @id"
    cmd.Parameters.AddWithValue("@id", noteId) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Scan history ──────────────────────────────────────────────────────────────

let appendScanHistory (conn: SqliteConnection) (deviceId: string) (ip: string option) (online: bool) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "INSERT INTO scan_history (scanned_at, device_id, ip, is_online) VALUES (@ts, @did, @ip, @on)"
    cmd.Parameters.AddWithValue("@ts",  now ())                      |> ignore
    cmd.Parameters.AddWithValue("@did", deviceId)                    |> ignore
    cmd.Parameters.AddWithValue("@ip",  ip |> dbv)          |> ignore
    cmd.Parameters.AddWithValue("@on",  if online then 1 else 0)     |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Stats ─────────────────────────────────────────────────────────────────────

let getStats (conn: SqliteConnection) : Stats =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        SELECT
            COUNT(DISTINCT d.id)                                          AS total,
            SUM(d.is_online)                                              AS online,
            SUM(CASE WHEN i.conn_type = 'Wired'        THEN 1 ELSE 0 END) AS wired,
            SUM(CASE WHEN i.conn_type = '2.4G Wireless' THEN 1 ELSE 0 END) AS w24,
            SUM(CASE WHEN i.conn_type = '5G Wireless'  THEN 1 ELSE 0 END) AS w5,
            SUM(CASE WHEN EXISTS(
                SELECT 1 FROM device_addrs a WHERE a.device_id = d.id AND a.addr_type = 'bluetooth'
            ) THEN 1 ELSE 0 END) AS bt,
            SUM(CASE WHEN d.web_ui_url IS NOT NULL      THEN 1 ELSE 0 END) AS webui,
            SUM(CASE WHEN d.category   = 'Unknown'      THEN 1 ELSE 0 END) AS unknown
        FROM devices d
        LEFT JOIN device_ips i ON i.device_id = d.id AND i.is_current = 1"""
    use r = cmd.ExecuteReader()
    if r.Read() then
        let ni (col: string) = let i = r.GetOrdinal(col) in if r.IsDBNull(i) then 0 else r.GetInt32(i)
        { total      = ni "total"
          online     = ni "online"
          wired      = ni "wired"
          wireless24 = ni "w24"
          wireless5  = ni "w5"
          bluetooth  = ni "bt"
          withWebUi  = ni "webui"
          unknown    = ni "unknown" }
    else
        { total=0; online=0; wired=0; wireless24=0; wireless5=0; bluetooth=0; withWebUi=0; unknown=0 }

// ── Seed import ───────────────────────────────────────────────────────────────
// Called on first startup or via POST /api/devices/seed.
// Merge key: wifiMac (if present) or btMac (if present).
// If a device with that MAC already exists: update name/category/model/webUiUrl/notes.
// If no match: create a new UUID device.
// Never deletes existing devices.

let importSeedData (conn: SqliteConnection) (devices: KnownDevice[]) =
    use tx = conn.BeginTransaction()
    let ts    = DateTimeOffset.UtcNow
    let tsStr = ts.ToString("o")

    for d in devices do
        // Try to find existing device by wifi MAC, then BT MAC
        let existing =
            if d.wifiMac <> "" then getByMac conn d.wifiMac
            elif d.btMac <> "" then
                use cmd = conn.CreateCommand()
                cmd.CommandText <- """
                    SELECT dev.id, dev.name, dev.category, dev.model, dev.web_ui_url,
                           dev.is_online, dev.first_seen, dev.last_seen
                    FROM devices dev
                    JOIN device_addrs a ON a.device_id = dev.id
                    WHERE a.addr_type = 'bluetooth' AND a.address = @mac
                    LIMIT 1"""
                cmd.Parameters.AddWithValue("@mac", d.btMac) |> ignore
                use r = cmd.ExecuteReader()
                if r.Read() then Some (hydrateDevice conn r) else None
            else None

        let deviceId =
            match existing with
            | Some dev ->
                // Update editable fields
                let upd = {
                    name     = Some d.name
                    category = Some d.category
                    model    = if d.model <> "" then Some d.model else None
                    webUiUrl = if d.webUiUrl <> "" then Some d.webUiUrl else None
                }
                updateDevice conn dev.id upd
                string dev.id
            | None ->
                let newId = Guid.NewGuid()
                let device = {
                    id         = newId
                    name       = Some d.name
                    category   = d.category
                    model      = if d.model <> "" then Some d.model else None
                    webUiUrl   = if d.webUiUrl <> "" then Some d.webUiUrl else None
                    haEntities = d.haEntities
                    isOnline   = false
                    firstSeen  = ts
                    lastSeen   = ts
                    addrs      = []
                    ips        = []
                    notes      = []
                    scanAttrs  = []
                }
                insertDevice conn device
                for eid in d.haEntities do addEntity conn newId eid
                string newId

        // Upsert wifi/ethernet MAC
        if d.wifiMac <> "" then
            let iface = match d.connType with "Wired" -> Ethernet | _ -> Wifi
            upsertAddr conn deviceId {
                address    = NetworkMac(d.wifiMac, iface)
                isActive   = true
                isReserved = false
                reservedIp = None
                source     = "seed"
                firstSeen  = ts
                lastSeen   = ts
            }

        // Upsert BT MAC
        if d.btMac <> "" then
            upsertAddr conn deviceId {
                address    = BluetoothAddr d.btMac
                isActive   = true
                isReserved = false
                reservedIp = None
                source     = "seed"
                firstSeen  = ts
                lastSeen   = ts
            }

        // Upsert IP
        if d.ip <> "" then
            let ipDU = if d.ip.Contains(":") then IPv6 d.ip else IPv4 d.ip
            upsertIp conn deviceId {
                ip        = ipDU
                hostname  = None
                pairedMac = if d.wifiMac <> "" then Some d.wifiMac else None
                connType  = if d.connType <> "" then Some d.connType else None
                isCurrent = true
                source    = "seed"
                firstSeen = ts
                lastSeen  = ts
            }

        // Append seed note if provided
        if d.notes <> "" then
            // Only add once (check if this exact note already exists)
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM device_notes WHERE device_id = @did AND note = @note"
            cmd.Parameters.AddWithValue("@did",  deviceId) |> ignore
            cmd.Parameters.AddWithValue("@note", d.notes)  |> ignore
            let count = cmd.ExecuteScalar() :?> int64
            if count = 0L then addNote conn (Guid.Parse(deviceId)) d.notes

    tx.Commit()

// ── Address reservation ───────────────────────────────────────────────────────

let setAddrReservation (conn: SqliteConnection) (deviceId: string) (address: string) (reservedIp: string option) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        UPDATE device_addrs SET
            is_reserved = @reserved,
            reserved_ip = @ip
        WHERE device_id = @did AND address = @addr"""
    cmd.Parameters.AddWithValue("@reserved", if reservedIp.IsSome then 1 else 0) |> ignore
    cmd.Parameters.AddWithValue("@ip",       reservedIp |> dbv)                  |> ignore
    cmd.Parameters.AddWithValue("@did",      deviceId)                           |> ignore
    cmd.Parameters.AddWithValue("@addr",     address)                            |> ignore
    cmd.ExecuteNonQuery() |> ignore

let deleteAddr (conn: SqliteConnection) (deviceId: string) (address: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "DELETE FROM device_addrs WHERE device_id = @did AND address = @addr"
    cmd.Parameters.AddWithValue("@did",  deviceId) |> ignore
    cmd.Parameters.AddWithValue("@addr", address)  |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── CSV export ────────────────────────────────────────────────────────────────

let exportCsv (conn: SqliteConnection) : string =
    let csvEsc (s: string) =
        if s.Contains(",") || s.Contains("\"") || s.Contains("\n") then
            "\"" + s.Replace("\"", "\"\"") + "\""
        else s
    let devices = getAll conn
    let sb = System.Text.StringBuilder()
    sb.AppendLine("id,name,category,model,ip,connType,mac,isOnline,webUiUrl,lastSeen") |> ignore
    for d in devices do
        let ip     = d.ips   |> List.tryFind (fun i -> i.isCurrent) |> Option.map (fun i -> i.ip.value)  |> Option.defaultValue ""
        let conn_t = d.ips   |> List.tryFind (fun i -> i.isCurrent) |> Option.bind (fun i -> i.connType) |> Option.defaultValue ""
        let mac    = d.addrs |> List.tryPick (fun a -> match a.address with NetworkMac(m,_) -> Some m | _ -> None) |> Option.defaultValue ""
        let nm     = d.name     |> Option.defaultValue "" |> csvEsc
        let cat    = d.category |> csvEsc
        let mdl    = d.model    |> Option.defaultValue "" |> csvEsc
        let url    = d.webUiUrl |> Option.defaultValue ""
        sb.AppendLine(sprintf "%s,%s,%s,%s,%s,%s,%s,%b,%s,%s"
            (string d.id) nm cat mdl ip conn_t mac d.isOnline url (d.lastSeen.ToString("o"))
        ) |> ignore
    sb.ToString()

// ── Transient device cleanup ──────────────────────────────────────────────
// Purges devices that are:
//   - category = 'Unknown' (user hasn't touched)
//   - last_seen older than @cutoff
//   - BLE-only (no MAC addresses, no IPs)
//     OR random-MAC-only (only locally-administered MACs, no IPs)
// Returns count of deleted devices.

let purgeTransientDevices (conn: SqliteConnection) (cutoff: DateTimeOffset) : int =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        DELETE FROM devices
        WHERE category = 'Unknown'
          AND last_seen < @cutoff
          AND id NOT IN (SELECT device_id FROM device_ips)
          AND id NOT IN (SELECT device_id FROM device_addrs WHERE source = 'manual')
          AND (
              -- BLE-only: has bluetooth addrs but no MAC addrs
              (    EXISTS (SELECT 1 FROM device_addrs WHERE device_id = devices.id AND addr_type = 'bluetooth')
               AND NOT EXISTS (SELECT 1 FROM device_addrs WHERE device_id = devices.id AND addr_type = 'mac'))
              OR
              -- Random-MAC-only: every MAC is locally administered (2nd hex char in 2367ABEFabef)
              (    EXISTS (SELECT 1 FROM device_addrs WHERE device_id = devices.id AND addr_type = 'mac')
               AND NOT EXISTS (
                   SELECT 1 FROM device_addrs
                   WHERE device_id = devices.id
                     AND addr_type = 'mac'
                     AND SUBSTR(address, 2, 1) NOT IN ('2','3','6','7','A','a','B','b','E','e','F','f')
               )
               AND NOT EXISTS (SELECT 1 FROM device_ips WHERE device_id = devices.id))
          )"""
    cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("o")) |> ignore
    cmd.ExecuteNonQuery()

/// Purge stale inactive addresses: transient BT (first char 0-B = RPA/Non-Resolvable)
/// and locally-administered MACs not seen since cutoff. Returns count deleted.
let purgeStaleAddresses (conn: SqliteConnection) (cutoff: DateTimeOffset) : int =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
        DELETE FROM device_addrs
        WHERE is_active = 0
          AND last_seen < @cutoff
          AND source != 'manual'
          AND (
              (addr_type = 'bluetooth'
                  AND UPPER(SUBSTR(address, 1, 1)) NOT IN ('0','1','2','3'))
              OR (addr_type = 'mac'
                  AND SUBSTR(address, 2, 1) IN ('2','3','6','7','A','a','B','b','E','e','F','f'))
          )"""
    cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("o")) |> ignore
    cmd.ExecuteNonQuery()

let purgeStaleAttrs (conn: SqliteConnection) (cutoff: DateTimeOffset) : int =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "DELETE FROM device_scan_attrs WHERE updated_at < @cutoff"
    cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("o")) |> ignore
    cmd.ExecuteNonQuery()

// ── Seed-needed check ─────────────────────────────────────────────────────────

let isDbEmpty (conn: SqliteConnection) : bool =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT COUNT(*) FROM devices"
    (cmd.ExecuteScalar() :?> int64) = 0L
