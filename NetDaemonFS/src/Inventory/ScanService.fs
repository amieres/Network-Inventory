module Inventory.ScanService

open System
open System.Net.Http
open System.Threading
open System.Threading.Channels
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Inventory

// ── Scan state (thread-safe) ──────────────────────────────────────────────────

type ScanState = {
    isRunning         : bool
    lastScanStarted   : DateTimeOffset option
    lastScanCompleted : DateTimeOffset option
    devicesFound      : int
}

// ── ScanService ───────────────────────────────────────────────────────────────
// Registered as both AddSingleton<ScanService> and AddHostedService so API
// handlers can inject it to call TriggerScan() or GetStatus().

type ScanService
    ( log    : ILogger<ScanService>
    , opts   : IOptions<InventoryConfig>
    , http   : IHttpClientFactory
    ) =

    let cfg      = opts.Value
    let connStr  = $"Data Source={cfg.DbPath};Foreign Keys=True"
    let trigger  = Channel.CreateBounded<unit>(BoundedChannelOptions(1, FullMode = BoundedChannelFullMode.DropOldest))
    let mutable state : ScanState = { isRunning = false; lastScanStarted = None; lastScanCompleted = None; devicesFound = 0 }

    let openConn () =
        let c = new SqliteConnection(connStr)
        c.Open()
        c

    // ── Run a single scan cycle ───────────────────────────────────────────────

    member private _.DoScan() : Async<unit> =
        async {
            state <- { state with isRunning = true; lastScanStarted = Some DateTimeOffset.UtcNow }
            try
                use conn   = openConn ()
                let client = http.CreateClient("netgear")
                let src : Scanner.ScanSources = { http = client; log = log; cfg = cfg }
                let! n = Scanner.runScan src conn
                // Cleanup transient BLE/random-MAC devices after every scan
                let cutoff = DateTimeOffset.UtcNow.AddMinutes(-float cfg.CleanupMaxAgeMinutes)
                let purgedDev  = Database.purgeTransientDevices conn cutoff
                let purgedAddr = Database.purgeStaleAddresses   conn cutoff
                if purgedDev > 0 || purgedAddr > 0 then
                    log.LogInformation("Cleanup: {d} transient devices, {a} stale addresses purged (before {cutoff})", purgedDev, purgedAddr, cutoff)

                state <- { state with
                               isRunning         = false
                               lastScanCompleted = Some DateTimeOffset.UtcNow
                               devicesFound      = n }
                log.LogInformation("Scan finished: {n} devices", n)
            with ex ->
                log.LogError(ex, "Scan failed")
                state <- { state with isRunning = false }
        }

    // ── Public API ────────────────────────────────────────────────────────────

    member _.TriggerScan() =
        trigger.Writer.TryWrite(()) |> ignore

    member _.GetStatus() : ScanStatus = {
        isRunning         = state.isRunning
        lastScanStarted   = state.lastScanStarted
        lastScanCompleted = state.lastScanCompleted
        devicesFound      = state.devicesFound
    }

    member _.GetConnection() = openConn ()

    // ── Refresh Bermuda data for a single device ──────────────────────────────

    member _.RefreshBermudaDevice(deviceId: Guid) : Async<Device option> =
        async {
            use conn   = openConn ()
            let client = http.CreateClient("netgear")
            match Database.getById conn deviceId with
            | None -> return None
            | Some dev ->
                let! bleDevices = BermudaClient.fetchDevices log client
                let ts = DateTimeOffset.UtcNow

                // Build lookup sets from device's known addresses
                let btAddrs   = dev.addrs |> List.choose (fun a -> match a.address with BluetoothAddr x -> Some (x.ToUpperInvariant()) | _ -> None) |> Set.ofList
                let irkKeys   = dev.addrs |> List.choose (fun a -> match a.address with Irk x -> Some (x.ToLowerInvariant()) | _ -> None) |> Set.ofList
                let beaconIds = dev.addrs |> List.choose (fun a -> match a.address with BeaconId x -> Some (x.ToLowerInvariant()) | _ -> None) |> Set.ofList
                let bermudaName = dev.scanAttrs |> List.tryFind (fun a -> a.key = "bermuda.name") |> Option.map (fun a -> a.value)

                let matchBle (d: BermudaClient.BleDevice) =
                    (d.mac <> "" && btAddrs   |> Set.contains (d.mac.ToUpperInvariant())) ||
                    (d.irk <> "" && irkKeys   |> Set.contains (d.irk.ToLowerInvariant())) ||
                    (d.name.StartsWith("bermuda_") &&
                        let parts = d.name.Split('_')
                        parts.Length >= 4 && parts.[1].Length = 32 &&
                        beaconIds |> Set.contains (parts.[1].ToLowerInvariant())) ||
                    (bermudaName |> Option.exists (fun n -> n = d.name))

                let matches = bleDevices |> List.filter matchBle
                if matches.IsEmpty then
                    log.LogInformation("RefreshBermuda: no Bermuda match for device {id}", deviceId)
                    return Some dev
                else
                    let upsert key value =
                        Database.upsertScanAttr conn (string deviceId)
                            { key = key; value = value; source = "bermuda"; updatedAt = ts }
                    for d in matches do
                        match d.rssi           with Some v -> upsert "bermuda.rssi"             (string v)         | None -> ()
                        match d.area           with Some v -> upsert "bermuda.area"             v                  | None -> ()
                        match d.distance       with Some v -> upsert "bermuda.distance_m"       (sprintf "%.2f" v) | None -> ()
                        match d.nearestScanner with Some v -> upsert "bermuda.nearest_scanner"  v                  | None -> ()
                        match d.floor          with Some v -> upsert "bermuda.floor"            v                  | None -> ()
                        match d.areaLastSeen   with Some v -> upsert "bermuda.area_last_seen"   v                  | None -> ()
                        match d.lastSeen       with Some dt -> upsert "bermuda.last_seen"       (dt.ToString("o")) | None -> ()
                        if d.name <> "" then upsert "bermuda.name" d.name
                        if d.isHome then Database.setOnlineStatus conn (string deviceId) true
                    log.LogInformation("RefreshBermuda: updated {id} from {n} Bermuda entries", deviceId, matches.Length)
                    return Database.getById conn deviceId
        }

    // ── IHostedService ────────────────────────────────────────────────────────

    interface IHostedService with

        member this.StartAsync(ct: CancellationToken) =
            task {
                // Migrate schema
                use conn = openConn ()
                Database.migrate conn

                // Seed DB on first run (empty DB)
                if Database.isDbEmpty conn then
                    log.LogInformation("Seeding DB with {n} known devices...", SeedData.devices.Length)
                    Database.importSeedData conn SeedData.devices
                    log.LogInformation("Seed complete")

                // Background loop
                let loop = async {
                    // Initial scan shortly after startup
                    do! Async.Sleep 5_000
                    do! this.DoScan()

                    while not ct.IsCancellationRequested do
                        // Wait for either a trigger or the configured interval
                        let intervalMs = cfg.ScanInterval * 60 * 1_000
                        use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        cts.CancelAfter(intervalMs)
                        try
                            let! _ = trigger.Reader.WaitToReadAsync(cts.Token).AsTask() |> Async.AwaitTask
                            let mutable item = ()
                            trigger.Reader.TryRead(&item) |> ignore   // drain
                        with :? OperationCanceledException -> ()

                        if not ct.IsCancellationRequested then
                            do! this.DoScan()
                }
                Async.Start(loop, ct)
            }

        member _.StopAsync(_ct: CancellationToken) =
            task { () }
