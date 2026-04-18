# Network Inventory

A [NetDaemon 5](https://netdaemon.xyz/) F# application for Home Assistant that provides a comprehensive **network device inventory** with a built-in web UI.

![Network Inventory UI](docs/screenshot.png)

## Features

- **Multi-source scanning** — aggregates device data from Netgear router, Eero mesh, Bermuda BLE tracker, and ARP pings
- **Rich address tracking** — WiFi/Ethernet MACs, Bluetooth addresses, BLE IRKs (Identity Resolving Keys), iBeacon UUIDs, and Resolvable Private Addresses
- **DHCP reservation tracking** — mark MAC addresses with their reserved IP; padlock icon visible even when device is offline
- **BLE / Bermuda integration** — resolves rotating BLE MAC addresses via IRK, tracks room-level presence
- **Interactive web UI** — sortable/resizable/reorderable columns, tri-state filters, category bar, full-text search
- **Device management** — merge duplicate entries, add notes, link Home Assistant entities, categorize and model devices
- **Manufacturer lookup** — OUI database lookup for MAC and Bluetooth addresses
- **CSV export** — download full inventory as CSV
- **Auto-cleanup** — purge transient BLE/random-MAC devices not seen within a configurable window

## Architecture

| Layer | Technology |
|---|---|
| Runtime | NetDaemon 5 (HA add-on) |
| Language | F# on .NET 9 |
| Web framework | [Falco](https://www.falcoframework.com/) (ASP.NET Core) |
| Database | SQLite via `Microsoft.Data.Sqlite` |
| UI | Vanilla JS + CSS (no framework) |

### Project structure

```
NetDaemonFS/
├── src/
│   ├── Inventory/
│   │   ├── Domain.fs          # Core types: Device, AddrEntry, IpEntry, …
│   │   ├── Database.fs        # SQLite access + schema migrations
│   │   ├── Api.fs             # REST API (Falco routes)
│   │   ├── Scanner.fs         # Scan orchestration + device upsert logic
│   │   ├── BermudaClient.fs   # Bermuda BLE presence integration
│   │   ├── EeroClient.fs      # Eero mesh API client
│   │   ├── NetgearClient.fs   # Netgear router scraper
│   │   ├── ScanService.fs     # Background scan service
│   │   ├── SeedData.fs        # Known-device seed import
│   │   ├── Config.fs          # Configuration types
│   │   └── WebHost.fs         # ASP.NET host setup
│   └── program.fs             # Entry point
├── wwwroot/
│   ├── index.html             # Single-page app shell
│   └── app.js                 # All UI logic
├── appsettings.example.json   # Config template (copy → appsettings.json)
└── publish.ps1                # Build + deploy to HA add-on share
```

## Setup

### Prerequisites

- Home Assistant with the [NetDaemon 5 add-on](https://netdaemon.xyz/docs/started/installation) installed
- .NET 9 SDK (for local development / publish)
- SMB access to your HA config share (`\\<ha-host>\config`)

### Configuration

1. Copy `NetDaemonFS/appsettings.example.json` → `NetDaemonFS/appsettings.json`
2. Fill in your values:

| Key | Description |
|---|---|
| `HomeAssistant.Host` | HA IP address |
| `HomeAssistant.Token` | Long-lived access token (HA → Profile → Security) |
| `NetworkInventory.Subnets` | IP prefixes to scan (e.g. `["192.168.1"]`) |
| `NetworkInventory.Netgear.Host` | Router web UI URL |
| `NetworkInventory.Netgear.Password` | Router admin password |
| `NetworkInventory.Eero.Enabled` | `true` to enable Eero cloud scanning |
| `NetworkInventory.Eero.SessionCookie` | Eero session cookie (`s=…`) |
| `NetworkInventory.Eero.NetworkId` | Eero network ID (from API URL) |

### Deploy

From the `NetDaemonFS/` directory:

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1
```

This stops the NetDaemon add-on, publishes via `dotnet publish`, copies to the HA SMB share, then restarts the add-on.

### Web UI

Once running, the inventory UI is available at:

```
http://<ha-host>:10000/inventory
```

## Eero session cookie

The Eero integration uses the private Eero cloud API (reverse-engineered). To get your session cookie:

1. Open browser DevTools → Network tab
2. Log in to [account.eero.com](https://account.eero.com)
3. Find a request to `api-user.e2ro.com` and copy the `s=…` cookie value
4. Set `NetworkInventory.Eero.SessionCookie` in your config

The session cookie expires periodically and will need refreshing.

## Bermuda BLE integration

The [Bermuda BLE Trilateration](https://github.com/agittins/bermuda) custom integration is supported. When enabled in Bermuda, it exposes device entities in HA that this app reads to update BLE addresses, IRKs, and room presence.

For devices with randomized BLE MACs (iPhones, modern Android), capture their IRK using [irk-capture](https://github.com/DerekSeaman/irk-capture) on an ESP32 and add it to the device in Bermuda. The inventory will then display and track the IRK.

## Database

The SQLite database is stored at the path configured in `NetworkInventory.DbPath` (default: `/config/devices.db` on the HA host). Schema migrations run automatically on startup.

## License

MIT
