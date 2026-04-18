namespace Inventory

// ── Known device seed record ──────────────────────────────────────────────────
// Flat, stringly-typed for easy editing in aligned table form.
// Import: Database.importSeedData runs on first startup (empty DB).
//         POST /api/devices/seed re-imports with merge (never deletes).
// Merge key: wifiMac or btMac — existing row updated, otherwise new UUID created.
//
// Field conventions:
//   ip         — "192.168.1.1" | "" for BT-only | "—" for offline/unknown
//   wifiMac    — WiFi or Ethernet MAC | "" if none
//   btMac      — Bluetooth MAC | "" if none
//   connType   — "Wired" | "2.4G Wireless" | "5G Wireless" | "Bluetooth" | "—"
//   haEntities — [] if none; glob patterns like "binary_sensor.camera_*" are fine
//   sshPort    — 0 if none
//   sshUser    — "" if none

type KnownDevice = {
    ip         : string
    name       : string
    category   : string
    connType   : string
    wifiMac    : string
    btMac      : string
    model      : string
    webUiUrl   : string
    haEntities : string list
    notes      : string
    sshPort    : int
    sshUser    : string
}

module SeedData =

    // Add your known devices here. They are merged into the DB on first startup
    // and whenever POST /api/devices/seed is called. Existing records are updated,
    // never deleted. Leave this array empty to start from scan-discovery only.
    let devices = [|

        // ── Example entries — replace with your own ───────────────────────────
        { ip = "192.168.1.1"  ; name = "Router"              ; category = "Infrastructure"; connType = "Wired"        ; wifiMac = "AA:BB:CC:DD:EE:FF"; btMac = ""; model = "My Router"   ; webUiUrl = "http://192.168.1.1" ; haEntities = []                  ; notes = ""           ; sshPort = 0 ; sshUser = ""  }
        { ip = "192.168.1.2"  ; name = "Home Assistant"      ; category = "Smart Home"    ; connType = "Wired"        ; wifiMac = "AA:BB:CC:DD:EE:FE"; btMac = ""; model = "HA Server"   ; webUiUrl = "http://192.168.1.2:8123"; haEntities = []               ; notes = ""           ; sshPort = 0 ; sshUser = ""  }

    |]
