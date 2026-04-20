module Inventory.Api

open System
open System.Text
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Inventory
open Inventory.ScanService

// ── JSON response helpers ─────────────────────────────────────────────────────

let private ok200 data : HttpHandler =
    Response.ofJson data

let private notFound msg : HttpHandler =
    Response.withStatusCode 404 >> Response.ofJson {| error = msg |}

let private badRequest msg : HttpHandler =
    Response.withStatusCode 400 >> Response.ofJson {| error = msg |}

let private noContent : HttpHandler =
    Response.withStatusCode 204 >> Response.ofEmpty

// ── Flat JSON projection (Address / Ip are DUs; STJ can't serialize them) ────

type AddrEntryJ = {
    addrType   : string
    address    : string
    iface      : string option
    isActive   : bool
    isReserved : bool
    reservedIp : string option
    source     : string
    firstSeen  : DateTimeOffset
    lastSeen   : DateTimeOffset
}

type IpEntryJ = {
    ip        : string
    ipVer     : int
    hostname  : string option
    pairedMac : string option
    connType  : string option
    isCurrent : bool
    source    : string
    firstSeen : DateTimeOffset
    lastSeen  : DateTimeOffset
}

type DeviceJ = {
    id         : Guid
    name       : string option
    category   : string
    model      : string option
    webUiUrl   : string option
    haEntities : string list
    isOnline   : bool
    firstSeen  : DateTimeOffset
    lastSeen   : DateTimeOffset
    addrs      : AddrEntryJ list
    ips        : IpEntryJ   list
    notes      : Note       list
    scanAttrs  : ScanAttr   list
}

let private flatAddr (e: AddrEntry) : AddrEntryJ =
    let addrType, address, iface =
        match e.address with
        | NetworkMac(mac, Wifi)     -> "mac",       mac, Some "wifi"
        | NetworkMac(mac, Ethernet) -> "mac",       mac, Some "ethernet"
        | BluetoothAddr a           -> "bluetooth", a,         None
        | Irk k                     -> "irk",       k,         None
        | Rpa a                     -> "rpa",       a,         None
        | BeaconId u                -> "beacon_id", u,         None
    { addrType   = addrType;      address    = address;      iface      = iface
      isActive   = e.isActive;   isReserved = e.isReserved; reservedIp = e.reservedIp
      source     = e.source
      firstSeen  = e.firstSeen;  lastSeen   = e.lastSeen }

let private flatIp (e: IpEntry) : IpEntryJ =
    { ip        = e.ip.value;   ipVer     = e.ip.ver
      hostname  = e.hostname;   pairedMac = e.pairedMac
      connType  = e.connType;   isCurrent = e.isCurrent
      source    = e.source
      firstSeen = e.firstSeen;  lastSeen  = e.lastSeen }

let private flatDevice (d: Device) : DeviceJ =
    { id         = d.id;        name       = d.name;       category   = d.category
      model      = d.model;     webUiUrl   = d.webUiUrl;   haEntities = d.haEntities
      isOnline   = d.isOnline;  firstSeen  = d.firstSeen;  lastSeen   = d.lastSeen
      addrs      = d.addrs |> List.map flatAddr
      ips        = d.ips   |> List.map flatIp
      notes      = d.notes
      scanAttrs  = d.scanAttrs }

let private okDevice  (d : Device     ) : HttpHandler = ok200 (flatDevice d)
let private okDevices (ds: Device list) : HttpHandler = ok200 (ds |> List.map flatDevice)

// ── Route value helper ────────────────────────────────────────────────────────

let private routeStr key (ctx: HttpContext) =
    (Request.getRoute ctx).TryGetString key |> Option.defaultValue ""

// ── GET /api/devices ──────────────────────────────────────────────────────────

let getDevices (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        use conn    = svc.GetConnection()
        let devices = Database.getAll conn
        return! okDevices devices ctx
    }

// ── GET /api/devices/{id} ─────────────────────────────────────────────────────

let getDevice (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            use conn = svc.GetConnection()
            match Database.getById conn id with
            | None     -> return! notFound "Device not found" ctx
            | Some dev -> return! okDevice dev ctx
    }

// ── GET /api/devices/by-ip/{ip} ───────────────────────────────────────────────

let getByIp (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let ip = routeStr "ip" ctx
        use conn = svc.GetConnection()
        match Database.getByIp conn ip with
        | None     -> return! notFound "Device not found" ctx
        | Some dev -> return! okDevice dev ctx
    }

// ── GET /api/devices/by-mac/{mac} ─────────────────────────────────────────────

let getByMac (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let mac = routeStr "mac" ctx
        use conn = svc.GetConnection()
        match Database.getByMac conn mac with
        | None     -> return! notFound "Device not found" ctx
        | Some dev -> return! okDevice dev ctx
    }

// ── PUT /api/devices/{id} ─────────────────────────────────────────────────────

let updateDevice (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            let! upd = Request.getJson<DeviceUpdate> ctx
            use conn = svc.GetConnection()
            Database.updateDevice conn id upd
            match Database.getById conn id with
            | None     -> return! notFound "Device not found" ctx
            | Some dev -> return! okDevice dev ctx
    }

// ── DELETE /api/devices/{id} ──────────────────────────────────────────────────

let deleteDevice (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            use conn = svc.GetConnection()
            Database.deleteDevice conn id
            return! noContent ctx
    }

// ── POST /api/devices/merge ────────────────────────────────────────────────────

[<CLIMutable>]
type MergeRequest = { keepId: string; mergeId: string }

let mergeDevices (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let! req = Request.getJson<MergeRequest> ctx
        match Guid.TryParse(req.keepId), Guid.TryParse(req.mergeId) with
        | (true, keepId), (true, mergeId) ->
            use conn = svc.GetConnection()
            Database.mergeDevices conn keepId mergeId
            match Database.getById conn keepId with
            | None     -> return! notFound "Device not found after merge" ctx
            | Some dev -> return! okDevice dev ctx
        | _ -> return! badRequest "Invalid UUIDs" ctx
    }

// ── POST /api/devices/{id}/addrs ──────────────────────────────────────────────

[<CLIMutable>]
type AddAddrRequest = { addrType: string; address: string; label: string }

let addAddr (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            let! req = Request.getJson<AddAddrRequest> ctx
            let addrOpt =
                match req.addrType with
                | "mac"       -> let iface = if req.label = "ethernet" then Ethernet else Wifi
                                 Some (NetworkMac(req.address.ToUpperInvariant(), iface))
                | "bluetooth" -> Some (BluetoothAddr req.address)
                | "irk"       -> Some (Irk req.address)
                | "rpa"       -> Some (Rpa req.address)
                | "beacon_id" -> Some (BeaconId req.address)
                | _           -> None
            match addrOpt with
            | None      -> return! badRequest "Unknown addrType" ctx
            | Some addr ->
                let ts = DateTimeOffset.UtcNow
                use conn = svc.GetConnection()
                Database.upsertAddr conn (string id) { address = addr; isActive = true; isReserved = false; reservedIp = None; source = "manual"; firstSeen = ts; lastSeen = ts }
                match Database.getById conn id with
                | None     -> return! notFound "Device not found" ctx
                | Some dev -> return! okDevice dev ctx
    }

// ── DELETE /api/devices/{id}/addrs/{address} ─────────────────────────────────

let deleteAddr (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr  = routeStr "id"      ctx
        let addrEnc = routeStr "address" ctx
        let addr   = Uri.UnescapeDataString(addrEnc)
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            use conn = svc.GetConnection()
            Database.deleteAddr conn (string id) addr
            match Database.getById conn id with
            | None     -> return! notFound "Device not found" ctx
            | Some dev -> return! okDevice dev ctx
    }

// ── POST /api/devices/{id}/addrs/reserve ─────────────────────────────────────

[<CLIMutable>]
type ReserveAddrRequest = { address: string; reservedIp: string option }

let reserveAddr (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            let! req = Request.getJson<ReserveAddrRequest> ctx
            use conn = svc.GetConnection()
            Database.setAddrReservation conn (string id) req.address req.reservedIp
            match Database.getById conn id with
            | None     -> return! notFound "Device not found" ctx
            | Some dev -> return! okDevice dev ctx
    }

// ── POST /api/devices/{id}/entities ───────────────────────────────────────────

[<CLIMutable>]
type AddEntityRequest = { entityId: string }

let addEntity (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            let! req = Request.getJson<AddEntityRequest> ctx
            use conn = svc.GetConnection()
            Database.addEntity conn id req.entityId
            match Database.getById conn id with
            | None     -> return! notFound "Device not found" ctx
            | Some dev -> return! okDevice dev ctx
    }

// ── DELETE /api/devices/{id}/entities/{entityId} ──────────────────────────────

let removeEntity (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr       = routeStr "id"       ctx
        let entityIdEnc = routeStr "entityId" ctx
        let entityId    = Uri.UnescapeDataString(entityIdEnc)
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            use conn = svc.GetConnection()
            Database.removeEntity conn id entityId
            return! noContent ctx
    }

// ── POST /api/devices/{id}/notes ──────────────────────────────────────────────

[<CLIMutable>]
type AddNoteRequest = { note: string }

let addNote (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            let! req = Request.getJson<AddNoteRequest> ctx
            use conn = svc.GetConnection()
            Database.addNote conn id req.note
            match Database.getById conn id with
            | None     -> return! notFound "Device not found" ctx
            | Some dev -> return! okDevice dev ctx
    }

// ── DELETE /api/devices/{id}/notes/{noteId} ───────────────────────────────────

let deleteNote (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr     = routeStr "id"     ctx
        let noteIdStr = routeStr "noteId" ctx
        match Guid.TryParse(idStr), Int32.TryParse(noteIdStr) with
        | (true, _), (true, noteId) ->
            use conn = svc.GetConnection()
            Database.deleteNote conn noteId
            return! noContent ctx
        | _ -> return! badRequest "Invalid ID" ctx
    }

// ── GET /api/stats ────────────────────────────────────────────────────────────

let getStats (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        use conn  = svc.GetConnection()
        let stats = Database.getStats conn
        return! ok200 stats ctx
    }

// ── GET /api/scan/status ──────────────────────────────────────────────────────

let getScanStatus (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        return! ok200 (svc.GetStatus()) ctx
    }

// ── POST /api/scan/trigger ────────────────────────────────────────────────────

let triggerScan (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        svc.TriggerScan()
        return! ok200 {| queued = true |} ctx
    }

// ── POST /api/devices/seed ────────────────────────────────────────────────────

let reseed (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        use conn = svc.GetConnection()
        Database.importSeedData conn SeedData.devices
        let n = Database.getAll conn |> List.length
        return! ok200 {| imported = SeedData.devices.Length; total = n |} ctx
    }

// ── GET /api/export/csv ───────────────────────────────────────────────────────

let exportCsv (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        use conn = svc.GetConnection()
        let csv  = Database.exportCsv conn
        let bytes = Encoding.UTF8.GetBytes(csv)
        ctx.Response.ContentType <- "text/csv"
        ctx.Response.Headers["Content-Disposition"] <- "attachment; filename=\"devices.csv\""
        ctx.Response.ContentLength <- int64 bytes.Length
        do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
    }

// ── POST /api/devices/{id}/refresh-bermuda ───────────────────────────────────

let refreshBermuda (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        let idStr = routeStr "id" ctx
        match Guid.TryParse(idStr) with
        | false, _ -> return! badRequest "Invalid UUID" ctx
        | true, id ->
            let! result = svc.RefreshBermudaDevice(id) |> Async.StartAsTask
            match result with
            | None     -> return! notFound "Device not found" ctx
            | Some dev -> return! okDevice dev ctx
    }

// ── POST /api/cleanup ─────────────────────────────────────────────────────

let cleanup (svc: ScanService) : HttpHandler = fun ctx ->
    task {
        use conn = svc.GetConnection()
        let maxAgeMinutes =
            let q = ctx.Request.Query
            if q.ContainsKey("maxAgeMinutes") then
                match System.Int32.TryParse(string q["maxAgeMinutes"]) with
                | true, n when n > 0 -> n
                | _ -> 240
            else 240
        let purgeAttrs =
            let q = ctx.Request.Query
            q.ContainsKey("purgeAttrs") && string q["purgeAttrs"] = "true"
        let cutoff = DateTimeOffset.UtcNow.AddMinutes(-float maxAgeMinutes)
        let purgedDevices = Database.purgeTransientDevices conn cutoff
        let purgedAddrs   = Database.purgeStaleAddresses   conn cutoff
        let purgedAttrs   = if purgeAttrs then Database.purgeStaleAttrs conn cutoff else 0
        return! ok200 {| purgedDevices = purgedDevices; purgedAddresses = purgedAddrs; purgedAttrs = purgedAttrs; cutoff = cutoff |} ctx
    }

// ── GET /api ──────────────────────────────────────────────────────────────────

let apiDescription : HttpHandler = fun ctx ->
    let ep m p d = {| method = m; path = p; description = d |}
    ok200 [|
        ep "GET"    "/api"                              "This endpoint — all available routes"
        ep "GET"    "/api/devices"                      "List all devices (addrs, ips, notes included)"
        ep "GET"    "/api/devices/{id}"                 "Get device by UUID"
        ep "GET"    "/api/devices/by-ip/{ip}"           "Get device by current IP address"
        ep "GET"    "/api/devices/by-mac/{mac}"         "Get device by MAC address"
        ep "PUT"    "/api/devices/{id}"                 "Update editable fields: name, category, model, webUiUrl"
        ep "DELETE" "/api/devices/{id}"                 "Delete device and all child rows"
        ep "POST"   "/api/devices/merge"                "Merge two devices: body { keepId, mergeId }"
        ep "POST"   "/api/devices/seed"                 "Import / refresh seed data from SeedData.fs"
        ep "POST"   "/api/devices/{id}/addrs"           "Add address: body { addrType, address, label } — addrType: mac | bluetooth | irk | rpa"
        ep "POST"   "/api/devices/{id}/entities"        "Link HA entity: body { entityId }"
        ep "DELETE" "/api/devices/{id}/entities/{eid}"  "Unlink HA entity"
        ep "POST"   "/api/devices/{id}/notes"           "Append note: body { note }"
        ep "DELETE" "/api/devices/{id}/notes/{noteId}"  "Delete note by ID"
        ep "GET"    "/api/stats"                        "Aggregate counts: total, online, wired, wireless24, wireless5, bluetooth, withWebUi, unknown"
        ep "GET"    "/api/scan/status"                  "Current scan state: isRunning, lastScanStarted, lastScanCompleted, devicesFound"
        ep "POST"   "/api/scan/trigger"                 "Enqueue an immediate scan"
        ep "GET"    "/api/export/csv"                   "Download all devices as CSV"
        ep "POST"   "/api/cleanup"                      "Purge transient BLE/random-MAC devices not seen in 4 hours"
    |] ctx

// ── Route table ───────────────────────────────────────────────────────────────

let routes (svc: ScanService) (log: ILogger) : HttpEndpoint list = [
    get    "/api"                                    apiDescription
    get    "/api/devices"                          (getDevices     svc)
    get    "/api/devices/by-ip/{ip}"               (getByIp        svc)
    get    "/api/devices/by-mac/{mac}"             (getByMac       svc)
    get    "/api/devices/{id}"                     (getDevice      svc)
    put    "/api/devices/{id}"                     (updateDevice   svc)
    delete "/api/devices/{id}"                     (deleteDevice   svc)
    post   "/api/devices/merge"                    (mergeDevices   svc)
    post   "/api/devices/seed"                     (reseed         svc)
    post   "/api/devices/{id}/addrs"               (addAddr        svc)
    delete "/api/devices/{id}/addrs/{address}"     (deleteAddr     svc)
    post   "/api/devices/{id}/addrs/reserve"       (reserveAddr    svc)
    post   "/api/devices/{id}/entities"            (addEntity      svc)
    delete "/api/devices/{id}/entities/{entityId}" (removeEntity   svc)
    post   "/api/devices/{id}/notes"               (addNote        svc)
    post   "/api/devices/{id}/refresh-bermuda"    (refreshBermuda svc)
    delete "/api/devices/{id}/notes/{noteId}"      (deleteNote     svc)
    get    "/api/stats"                            (getStats       svc)
    get    "/api/scan/status"                      (getScanStatus  svc)
    post   "/api/scan/trigger"                     (triggerScan    svc)
    get    "/api/export/csv"                       (exportCsv      svc)
    post   "/api/cleanup"                          (cleanup        svc)
]
