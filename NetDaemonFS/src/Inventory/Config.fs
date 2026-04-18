namespace Inventory

// ── Nested config sub-classes ─────────────────────────────────────────────────
// Must be classes (not records) for ASP.NET Options binding to set properties.

type NetgearConfig() =
    member val Host     : string = "http://192.168.5.2"      with get, set
    member val Password : string = ""                        with get, set

type EeroConfig() =
    member val Enabled       : bool   = true with get, set
    member val SessionCookie : string = ""   with get, set
    member val NetworkId     : string = ""   with get, set

// ── Primary config class ──────────────────────────────────────────────────────
// Bound to appsettings.json["NetworkInventory"] via
//   services.Configure<InventoryConfig>(config.GetSection("NetworkInventory"))
// Injected as IOptions<InventoryConfig> wherever needed.

type InventoryConfig() =
    member val DbPath       : string   = "./devices.db"                                   with get, set
    member val Subnets      : string[] = [| "192.168.4"; "192.168.5"; "192.168.6" |]     with get, set
    member val ScanInterval          : int      = 5    with get, set   // minutes between scans
    member val CleanupMaxAgeMinutes  : int      = 240  with get, set   // purge transient devices not seen in this many minutes
    member val PingTimeout  : int      = 500  with get, set   // ms per ICMP ping
    member val Netgear      : NetgearConfig = NetgearConfig() with get, set
    member val Eero         : EeroConfig    = EeroConfig()    with get, set
