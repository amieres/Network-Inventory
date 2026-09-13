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
    updated_at TEXT NOT NULL
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

// ── Overrides ────────────────────────────────────────────────────────────────

type Override = {
    nodeKey : string
    label   : string option
    area    : string option
    link    : string option
    size    : int option
    acInput : bool option
    kind    : string option
}

let private readOpt (r: SqliteDataReader) (i: int) =
    if r.IsDBNull i then None else Some (r.GetString i)

let loadOverrides (conn: SqliteConnection) : Map<string, Override> =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT node_key, label, area, link, size, ac_input, kind FROM health_node_overrides"
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
              kind    = readOpt r 6 })
    acc |> Map.ofSeq

let saveOverride (conn: SqliteConnection) (o: Override) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
INSERT INTO health_node_overrides (node_key, label, area, link, size, ac_input, kind, updated_at)
VALUES ($k, $label, $area, $link, $size, $ac, $kind, $now)
ON CONFLICT(node_key) DO UPDATE SET
    label = $label, area = $area, link = $link,
    size = $size, ac_input = $ac, kind = $kind, updated_at = $now"""
    cmd.Parameters.AddWithValue("$k", o.nodeKey) |> ignore
    cmd.Parameters.AddWithValue("$label", dbv o.label) |> ignore
    cmd.Parameters.AddWithValue("$area",  dbv o.area)  |> ignore
    cmd.Parameters.AddWithValue("$link",  dbv o.link)  |> ignore
    cmd.Parameters.AddWithValue("$size",  (match o.size with Some v -> box v | None -> box DBNull.Value)) |> ignore
    cmd.Parameters.AddWithValue("$ac",    (match o.acInput with Some v -> box (if v then 1 else 0) | None -> box DBNull.Value)) |> ignore
    cmd.Parameters.AddWithValue("$kind",  dbv o.kind)  |> ignore
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
            kind  = o.kind |> Option.defaultValue n.kind }

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
