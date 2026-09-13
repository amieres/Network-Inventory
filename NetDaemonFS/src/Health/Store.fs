module Health.Store

open System
open Microsoft.Data.Sqlite
open Health

// ── Editable topology overrides ──────────────────────────────────────────────
// The topology in Topology.fs is the seed. Anything the user edits in the
// dashboard - a name, an SSID, an area, a box size, whether a device takes AC
// input - is stored here and layered on top at read time, so edits survive a
// redeploy instead of being overwritten by the next build.
//
// Same SQLite file as the inventory, so there is one database to back up.

let private dbv (v: string option) : obj =
    match v with Some s -> box s | None -> box DBNull.Value

let migrate (conn: SqliteConnection) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
CREATE TABLE IF NOT EXISTS health_node_overrides (
    node_key   TEXT PRIMARY KEY NOT NULL,
    label      TEXT,
    area       TEXT,
    link       TEXT,      -- SSID / link name, '' means wired
    size       INTEGER,
    ac_input   INTEGER,   -- 1 = takes an AC input from the grid
    kind       TEXT,
    power_from TEXT,      -- 'device:<key>' | 'area:<name>' | ''
    lan        TEXT,      -- 'wifi:<ssid>' | 'wired:<key>' | 'powerline:<area>' | 'none'
    updated_at TEXT NOT NULL
);

-- Devices added from the dashboard, so the graph can be extended without a
-- redeploy. Deleted nodes are tombstoned rather than removed, so a seed node
-- from Topology.fs can be hidden too.
CREATE TABLE IF NOT EXISTS health_custom_nodes (
    node_key   TEXT PRIMARY KEY NOT NULL,
    label      TEXT NOT NULL,
    kind       TEXT NOT NULL,
    area       TEXT,
    device     TEXT,
    entity     TEXT,
    power_from TEXT,
    lan        TEXT,
    size       INTEGER NOT NULL DEFAULT 2,
    deleted    INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);

-- User-added edges, so a connection drawn in the UI survives a redeploy.
CREATE TABLE IF NOT EXISTS health_user_edges (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    child      TEXT NOT NULL,
    parent     TEXT NOT NULL,
    kind       TEXT NOT NULL,
    note       TEXT,
    created_at TEXT NOT NULL,
    UNIQUE (child, parent, kind)
);

-- Hand-arranged diagram positions, so a layout is not tied to one browser.
CREATE TABLE IF NOT EXISTS health_node_pos (
    node_key   TEXT PRIMARY KEY NOT NULL,
    x          REAL NOT NULL,
    y          REAL NOT NULL,
    updated_at TEXT NOT NULL
);
"""
    cmd.ExecuteNonQuery() |> ignore

    // Columns added after the first release.
    for col, decl in [ "power_from", "TEXT"; "lan", "TEXT" ] do
        use chk = conn.CreateCommand()
        chk.CommandText <- $"SELECT COUNT(*) FROM pragma_table_info('health_node_overrides') WHERE name = '{col}'"
        if (chk.ExecuteScalar() :?> int64) = 0L then
            use add = conn.CreateCommand()
            add.CommandText <- $"ALTER TABLE health_node_overrides ADD COLUMN {col} {decl}"
            add.ExecuteNonQuery() |> ignore

// ── Parsing helpers ──────────────────────────────────────────────────────────

let parsePower (s: string) : PowerSource option =
    match s with
    | null | "" -> None
    | s when s.StartsWith "device:" -> Some (FromDevice (s.Substring 7))
    | s when s.StartsWith "area:"   -> Some (FromArea   (s.Substring 5))
    | _ -> Some PowerUnknown

let parseLan (s: string) : LanKind option =
    match s with
    | null | "" -> None
    | "none" -> Some NoLan
    | s when s.StartsWith "wifi:"      -> Some (Wifi      (s.Substring 5))
    | s when s.StartsWith "wired:"     -> Some (Wired     (s.Substring 6))
    | s when s.StartsWith "powerline:" -> Some (Powerline (s.Substring 10))
    | _ -> Some NoLan

// ── Overrides ────────────────────────────────────────────────────────────────

type Override = {
    nodeKey : string
    label   : string option
    area    : string option
    link    : string option
    size    : int option
    acInput : bool option
    kind    : string option
    powerFrom : string option
    lan       : string option
}

let private readOpt (r: SqliteDataReader) (i: int) =
    if r.IsDBNull i then None else Some (r.GetString i)

let loadOverrides (conn: SqliteConnection) : Map<string, Override> =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT node_key, label, area, link, size, ac_input, kind, power_from, lan FROM health_node_overrides"
    use r = cmd.ExecuteReader()
    let acc = ResizeArray()
    while r.Read() do
        acc.Add(
            r.GetString 0,
            { nodeKey = r.GetString 0
              label   = readOpt r 1
              area    = readOpt r 2
              link    = readOpt r 3
              size    = if r.IsDBNull 4 then None else Some (r.GetInt32 4)
              acInput = if r.IsDBNull 5 then None else Some (r.GetInt32 5 = 1)
              kind    = readOpt r 6
              powerFrom = readOpt r 7
              lan       = readOpt r 8 })
    acc |> Map.ofSeq

let saveOverride (conn: SqliteConnection) (o: Override) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
INSERT INTO health_node_overrides (node_key, label, area, link, size, ac_input, kind, power_from, lan, updated_at)
VALUES ($k, $label, $area, $link, $size, $ac, $kind, $pf, $lan, $now)
ON CONFLICT(node_key) DO UPDATE SET
    label = $label, area = $area, link = $link,
    size = $size, ac_input = $ac, kind = $kind,
    power_from = $pf, lan = $lan, updated_at = $now"""
    cmd.Parameters.AddWithValue("$k", o.nodeKey) |> ignore
    cmd.Parameters.AddWithValue("$label", dbv o.label) |> ignore
    cmd.Parameters.AddWithValue("$area",  dbv o.area)  |> ignore
    cmd.Parameters.AddWithValue("$link",  dbv o.link)  |> ignore
    cmd.Parameters.AddWithValue("$size",  (match o.size with Some v -> box v | None -> box DBNull.Value)) |> ignore
    cmd.Parameters.AddWithValue("$ac",    (match o.acInput with Some v -> box (if v then 1 else 0) | None -> box DBNull.Value)) |> ignore
    cmd.Parameters.AddWithValue("$kind",  dbv o.kind)  |> ignore
    cmd.Parameters.AddWithValue("$pf",  dbv o.powerFrom) |> ignore
    cmd.Parameters.AddWithValue("$lan", dbv o.lan) |> ignore
    cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString "o") |> ignore
    cmd.ExecuteNonQuery() |> ignore

/// Layer stored edits over the seed topology.
let applyOverrides (overrides: Map<string, Override>) (n: Node) : Node =
    match overrides |> Map.tryFind n.key with
    | None -> n
    | Some o ->
        { n with
            label = o.label |> Option.defaultValue n.label
            area  = (match o.area with Some "" -> None | Some a -> Some a | None -> n.area)
            link  = (match o.link with Some "" -> None | Some l -> Some l | None -> n.link)
            size  = o.size |> Option.defaultValue n.size
            kind  = o.kind |> Option.defaultValue n.kind
            powerFrom = (o.powerFrom |> Option.bind parsePower |> Option.defaultValue n.powerFrom)
            lan       = (o.lan       |> Option.bind parseLan   |> Option.defaultValue n.lan) }

// ── Custom / deleted nodes ───────────────────────────────────────────────────
// Devices added from the dashboard, plus tombstones that hide a seed node from
// Topology.fs without editing code.

let loadCustomNodes (conn: SqliteConnection) : Node list * Set<string> =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT node_key, label, kind, area, device, entity, power_from, lan, size, deleted FROM health_custom_nodes"
    use r = cmd.ExecuteReader()
    let added = ResizeArray()
    let dead  = ResizeArray()
    while r.Read() do
        let key = r.GetString 0
        if r.GetInt32 9 = 1 then dead.Add key
        else
            added.Add
                { key   = key
                  label = r.GetString 1
                  kind  = r.GetString 2
                  device = readOpt r 4
                  entity = readOpt r 5
                  area   = readOpt r 3
                  blindSpot = false
                  remedy = None
                  outputEntity = None
                  link = (match readOpt r 7 |> Option.bind parseLan with Some (Wifi s) -> Some s | _ -> None)
                  size = r.GetInt32 8
                  powerFrom = (readOpt r 6 |> Option.bind parsePower |> Option.defaultValue PowerUnknown)
                  lan       = (readOpt r 7 |> Option.bind parseLan   |> Option.defaultValue NoLan)
                  btHost = None; needsInternet = false }
    List.ofSeq added, Set.ofSeq dead

let addCustomNode (conn: SqliteConnection)
                  (key: string) (label: string) (kind: string)
                  (area: string option) (device: string option) (entity: string option)
                  (powerFrom: string option) (lan: string option) (size: int) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
INSERT INTO health_custom_nodes (node_key, label, kind, area, device, entity, power_from, lan, size, deleted, created_at)
VALUES ($k, $l, $kind, $a, $d, $e, $pf, $lan, $sz, 0, $now)
ON CONFLICT(node_key) DO UPDATE SET
    label = $l, kind = $kind, area = $a, device = $d, entity = $e,
    power_from = $pf, lan = $lan, size = $sz, deleted = 0"""
    for n, v in [ "$k", box key; "$l", box label; "$kind", box kind
                  "$a", dbv area; "$d", dbv device; "$e", dbv entity
                  "$pf", dbv powerFrom; "$lan", dbv lan; "$sz", box size
                  "$now", box (DateTimeOffset.UtcNow.ToString "o") ] do
        cmd.Parameters.AddWithValue(n, v) |> ignore
    cmd.ExecuteNonQuery() |> ignore

/// Tombstone rather than delete, so a node seeded in Topology.fs can be hidden.
let deleteNode (conn: SqliteConnection) (key: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
INSERT INTO health_custom_nodes (node_key, label, kind, deleted, created_at)
VALUES ($k, $k, 'deleted', 1, $now)
ON CONFLICT(node_key) DO UPDATE SET deleted = 1"""
    cmd.Parameters.AddWithValue("$k", key) |> ignore
    cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString "o") |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── User edges ───────────────────────────────────────────────────────────────

let loadUserEdges (conn: SqliteConnection) : Edge list =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT child, parent, kind, note FROM health_user_edges"
    use r = cmd.ExecuteReader()
    let acc = ResizeArray()
    while r.Read() do
        let kind =
            match r.GetString 2 with
            | "power"   -> Power
            | "network" -> Network
            | "bt-host" -> BtHost
            | _         -> Host
        acc.Add { child = r.GetString 0; parent = r.GetString 1; kind = kind; note = readOpt r 3 }
    List.ofSeq acc

let addUserEdge (conn: SqliteConnection) (child: string) (parent: string) (kind: string) (note: string option) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
INSERT INTO health_user_edges (child, parent, kind, note, created_at)
VALUES ($c, $p, $k, $n, $now)
ON CONFLICT(child, parent, kind) DO NOTHING"""
    cmd.Parameters.AddWithValue("$c", child) |> ignore
    cmd.Parameters.AddWithValue("$p", parent) |> ignore
    cmd.Parameters.AddWithValue("$k", kind) |> ignore
    cmd.Parameters.AddWithValue("$n", dbv note) |> ignore
    cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString "o") |> ignore
    cmd.ExecuteNonQuery() |> ignore

let removeUserEdge (conn: SqliteConnection) (child: string) (parent: string) (kind: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "DELETE FROM health_user_edges WHERE child = $c AND parent = $p AND kind = $k"
    cmd.Parameters.AddWithValue("$c", child) |> ignore
    cmd.Parameters.AddWithValue("$p", parent) |> ignore
    cmd.Parameters.AddWithValue("$k", kind) |> ignore
    cmd.ExecuteNonQuery() |> ignore

// ── Positions ────────────────────────────────────────────────────────────────
// Server-side so a hand-arranged layout is not trapped in one browser's
// localStorage (and cannot be lost by clearing it).

let loadPositions (conn: SqliteConnection) : Map<string, float * float> =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT node_key, x, y FROM health_node_pos"
    use r = cmd.ExecuteReader()
    let acc = ResizeArray()
    while r.Read() do acc.Add(r.GetString 0, (r.GetDouble 1, r.GetDouble 2))
    acc |> Map.ofSeq

let savePositions (conn: SqliteConnection) (positions: (string * float * float) list) =
    use tx = conn.BeginTransaction()
    for (k, x, y) in positions do
        use cmd = conn.CreateCommand()
        cmd.Transaction <- tx
        cmd.CommandText <- """
INSERT INTO health_node_pos (node_key, x, y, updated_at) VALUES ($k, $x, $y, $now)
ON CONFLICT(node_key) DO UPDATE SET x = $x, y = $y, updated_at = $now"""
        cmd.Parameters.AddWithValue("$k", k) |> ignore
        cmd.Parameters.AddWithValue("$x", x) |> ignore
        cmd.Parameters.AddWithValue("$y", y) |> ignore
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString "o") |> ignore
        cmd.ExecuteNonQuery() |> ignore
    tx.Commit()
