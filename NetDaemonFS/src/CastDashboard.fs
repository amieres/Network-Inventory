namespace CastDashboard

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NetDaemon.AppModel
open NetDaemon.HassModel
open NetDaemon.HassModel.Integration
open NetDaemon.Extensions.Scheduler
open SkiaSharp

// ---------------------------------------------------------------------------
// Casts a camera grid to a Chromecast without using the Home Assistant Cast
// receiver apps (A078F6B0 / B45F4572). Current Chromecast firmware refuses to
// launch those -- probed directly from a PC with HA out of the loop, on two
// device generations:
//     CC1AD845 (Google default media receiver) -> launches fine
//     A078F6B0 / B45F4572 (HA receivers)       -> "Failed to execute start app"
// which is why cast.show_lovelace_view only ever flickers the TV. See
// home-assistant/core#177170.
//
// Instead we render the grid here, publish it as MJPEG over the NetDaemon web
// host, and hand that URL to media_player.play_media -- the plain media path
// that still works (the same reason camera.play_stream kept working).
//
// Snapshots come from /api/camera_proxy/<entity>, which takes 2-4s per camera,
// so they are fetched in parallel and the composed frame is cached; the MJPEG
// endpoint serves whatever the latest composition is.
// ---------------------------------------------------------------------------

type Config() =
    member val Cameras     : ResizeArray<string> = ResizeArray() with get, set
    member val MediaPlayer : string = ""                 with get, set
    member val HaUrl       : string = "http://127.0.0.1:8123" with get, set
    member val Token       : string = ""                 with get, set
    /// Publicly reachable base URL of this NetDaemon web host, as the
    /// Chromecast will see it (must be an IP or resolvable name, not localhost).
    member val SelfUrl     : string = ""                 with get, set
    member val Columns     : int    = 2                  with get, set
    member val Width       : int    = 1280               with get, set
    member val Height      : int    = 720                with get, set
    /// Minimum gap between snapshots of the *same* camera. 0 means "as fast as
    /// the camera answers" -- /api/camera_proxy already takes 2-5s per ONVIF
    /// camera because HA transcodes a frame from RTSP on every request, so any
    /// extra delay here is added on top of that, not overlapped with it.
    member val RefreshSecs : int    = 0                  with get, set
    /// Frames per second served on the MJPEG endpoint, which go2rtc consumes
    /// and transcodes. The underlying camera snapshots only change every 1-4s,
    /// so this mostly buys smooth playback rather than new information.
    member val StreamFps   : int    = 10                 with get, set
    /// go2rtc URL for the composed grid, transcoded to H.264 so the Chromecast
    /// plays it as real video. Use stream.mp4 (fragmented MP4), not
    /// stream.m3u8: go2rtc mints a fresh session id on every master-playlist
    /// request, so the receiver's second fetch lands on a different session
    /// and the stream dies immediately.
    member val StreamUrl : string =
        "http://192.168.5.70:1984/api/stream.mp4?src=cameras_grid" with get, set

module Render =
    let private bg    = SKColors.Black
    let private label = new SKColor(236uy, 239uy, 241uy)

    /// Draws the snapshots into a grid, letterboxing each tile so cameras with
    /// different aspect ratios (2592x1944 vs 16:9) are not distorted.
    let compose (width: int) (height: int) (columns: int)
                (tiles: (string * SKBitmap option) []) : byte[] =
        let rows    = max 1 ((tiles.Length + columns - 1) / columns)
        let cellW   = width  / columns
        let cellH   = height / rows
        use surface = SKSurface.Create(new SKImageInfo(width, height))
        let canvas  = surface.Canvas
        canvas.Clear bg

        // NoDependencies build has no fontconfig, so there may be no system
        // font at all; guard so a missing typeface degrades to "no text"
        // rather than throwing mid-frame.
        let typeface = try SKTypeface.Default with _ -> null
        use paint = new SKPaint(Color = label, IsAntialias = true)
        use font  = new SKFont((if isNull typeface then SKTypeface.CreateDefault() else typeface), 22.0f)
        let drawText (text: string) (x: float32) (y: float32) (f: SKFont) =
            try canvas.DrawText(text, x, y, f, paint) with _ -> ()

        tiles |> Array.iteri (fun i (name, bmp) ->
            let cx = (i % columns) * cellW
            let cy = (i / columns) * cellH
            match bmp with
            | Some b when b.Width > 0 && b.Height > 0 ->
                // preserve aspect ratio inside the cell
                let scale = min (float32 cellW / float32 b.Width)
                                (float32 cellH / float32 b.Height)
                let dw    = float32 b.Width  * scale
                let dh    = float32 b.Height * scale
                let dx    = float32 cx + (float32 cellW - dw) / 2.0f
                let dy    = float32 cy + (float32 cellH - dh) / 2.0f
                canvas.DrawBitmap(b, new SKRect(dx, dy, dx + dw, dy + dh))
            | _ ->
                drawText $"{name}: unavailable"
                         (float32 cx + 20.0f) (float32 cy + 40.0f) font)

        // clock overlay so the stream is visibly live
        use clockFont = new SKFont(font.Typeface, 28.0f)
        drawText (DateTime.Now.ToString "HH:mm:ss")
                 (float32 width - 130.0f) (float32 height - 18.0f) clockFont

        use image = surface.Snapshot()
        // 60 rather than 80: every MJPEG frame is a full keyframe, so quality
        // costs bandwidth on every frame into go2rtc's transcode. The
        // difference is invisible once re-encoded to H.264.
        use data  = image.Encode(SKEncodedImageFormat.Jpeg, 60)
        data.ToArray()

/// Holds the most recently composed frame. The MJPEG endpoint reads from here,
/// so slow camera fetches never stall a connected Chromecast.
type FrameBuffer() =
    let mutable frame : byte[] = Array.empty
    let gate = obj ()
    member val FrameDelayMs = 250 with get, set
    member _.Set(bytes: byte[]) = lock gate (fun () -> frame <- bytes)
    member _.Get()              = lock gate (fun () -> frame)

[<NetDaemonApp>]
type CameraCast(ha     : IHaContext,
                sched  : INetDaemonScheduler,
                config : IAppConfig<Config>,
                appCfg : Microsoft.Extensions.Configuration.IConfiguration,
                buffer : FrameBuffer,
                logger : ILogger<CameraCast>) =

    let cfg = config.Value

    // The Supervisor injects its own HomeAssistant__Token into the add-on's
    // environment, which outranks appsettings.json in the config chain. That
    // token is only good for the Supervisor API and gets a 401 from
    // /api/camera_proxy, so read the long-lived token straight from the file.
    let tokenFromAppSettings () =
        try
            let path =
                IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json")
            if IO.File.Exists path then
                use doc = Text.Json.JsonDocument.Parse(IO.File.ReadAllText path)
                match doc.RootElement.TryGetProperty "HomeAssistant" with
                | true, ha ->
                    match ha.TryGetProperty "Token" with
                    | true, t -> t.GetString()
                    | _       -> null
                | _ -> null
            else null
        with _ -> null

    let token =
        [ cfg.Token; tokenFromAppSettings (); appCfg.["HomeAssistant:Token"] ]
        |> List.tryFind (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue ""
    let http  = new HttpClient(Timeout = TimeSpan.FromSeconds 20.0)
    do http.DefaultRequestHeaders.Authorization <-
        AuthenticationHeaderValue("Bearer", token)

    /// last time a warning was logged per camera, to keep retries from
    /// filling the add-on log
    let lastWarn = Collections.Generic.Dictionary<string, DateTime>()

    let snapshot (entity: string) : Task<string * SKBitmap option> =
        task {
            try
                let  url   = $"{cfg.HaUrl}/api/camera_proxy/{entity}"
                let! bytes = http.GetByteArrayAsync url
                return entity, (if bytes.Length > 0
                                then Option.ofObj (SKBitmap.Decode bytes)
                                else None)
            with ex ->
                // one line per entity per minute, not per refresh cycle
                let now = DateTime.UtcNow
                let quiet =
                    match lastWarn.TryGetValue entity with
                    | true, t -> now - t < TimeSpan.FromMinutes 1.0
                    | _       -> false
                if not quiet then
                    lastWarn[entity] <- now
                    // log the whole chain: native load failures hide the real
                    // reason (missing libfontconfig etc.) in InnerException
                    logger.LogWarning(ex, "camera {Entity} snapshot failed",
                                      entity)
                return entity, None
        }

    /// Latest decoded tile per camera. Each camera refreshes on its own loop,
    /// so one slow camera (a 5s ONVIF transcode) no longer holds back the
    /// whole grid the way a lockstep Task.WhenAll did.
    let tiles = Collections.Concurrent.ConcurrentDictionary<string, SKBitmap option>()

    /// Recomposes from whatever tiles are currently cached.
    let recompose () =
        let current =
            cfg.Cameras
            |> Seq.map (fun e ->
                match tiles.TryGetValue e with
                | true, b -> e, b
                | _       -> e, None)
            |> Seq.toArray
        buffer.Set(Render.compose cfg.Width cfg.Height cfg.Columns current)

    /// Continuously refreshes one camera, replacing its tile as each snapshot
    /// lands. Staggered start so N cameras do not all hit HA at once.
    let runCameraLoop (entity: string) (offsetMs: int) =
        task {
            do! Task.Delay offsetMs
            while true do
                let! (_, bmp) = snapshot entity
                match bmp with
                | Some _ ->
                    match tiles.TryGetValue entity with
                    | true, Some old -> old.Dispose()
                    | _ -> ()
                    tiles[entity] <- bmp
                    recompose ()
                | None ->
                    // keep the last good frame rather than blanking the tile
                    if not (tiles.ContainsKey entity) then
                        tiles[entity] <- None
                        recompose ()
                do! Task.Delay(max 250 (cfg.RefreshSecs * 1000))
        } :> Task

    // Getting a live grid onto a Chromecast took three attempts:
    //   MJPEG  -- receiver loads it but stays 'paused' behind the running app,
    //             so the TV never actually switches to it.
    //   stills -- do take the foreground, but each refresh is a full media
    //             load, so animating them means constant flicker.
    //   HLS    -- real video: takes the foreground and plays continuously.
    // go2rtc (already on the HA host, with ffmpeg built in) transcodes our
    // MJPEG endpoint to H.264 and serves it as HLS; see go2rtc.yaml stream
    // `cameras_grid`.
    let playGrid () =
        ha.CallService("media_player", "play_media",
            target = NetDaemon.HassModel.Entities.ServiceTarget.FromEntity cfg.MediaPlayer,
            data   = {| media_content_id   = cfg.StreamUrl
                        media_content_type = "video/mp4"
                        extra = {| stream_type = "LIVE"
                                   metadata    = {| title = "Cameras" |} |} |})

    /// If another app (PBS, YouTube, ...) is in the foreground, play_media
    /// alone loads the receiver *behind* it and the TV never switches. Quitting
    /// the current app first makes the cast take the screen.
    let castNow () =
        logger.LogInformation("casting {Url} -> {Player}",
                              cfg.StreamUrl, cfg.MediaPlayer)
        ha.CallService("media_player", "turn_off",
            target = NetDaemon.HassModel.Entities.ServiceTarget.FromEntity cfg.MediaPlayer)
        sched.RunIn(TimeSpan.FromSeconds 6.0, playGrid) |> ignore

    let stopCasting () =
        ha.CallService("media_player", "turn_off",
            target = NetDaemon.HassModel.Entities.ServiceTarget.FromEntity cfg.MediaPlayer)

    do
        if cfg.Cameras.Count = 0 || String.IsNullOrWhiteSpace cfg.SelfUrl then
            logger.LogWarning "CameraCast: Cameras/SelfUrl not configured; idle."
        else
            buffer.FrameDelayMs <- 1000 / (max 1 cfg.StreamFps)

            // one independent loop per camera, staggered so they do not
            // stampede /api/camera_proxy together
            let window  = max 2000 (cfg.RefreshSecs * 1000)
            let stagger = window / (max 1 cfg.Cameras.Count)
            cfg.Cameras
            |> Seq.iteri (fun i entity ->
                runCameraLoop entity (i * stagger) |> ignore)

            ha.RegisterServiceCallBack<obj>("cast_cameras",      fun _ -> castNow ())
            ha.RegisterServiceCallBack<obj>("cast_cameras_stop", fun _ ->
                stopCasting ()
                ha.CallService("media_player", "turn_off",
                    target = NetDaemon.HassModel.Entities.ServiceTarget.FromEntity cfg.MediaPlayer))
            logger.LogInformation
                "CameraCast ready: netdaemon.cast_cameras / cast_cameras_stop."

// ---------------------------------------------------------------------------
// Web plumbing. Kept here (rather than in Inventory.WebHost) so this file can
// compile before it and stay self-contained.
// ---------------------------------------------------------------------------

module Web =
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Http
    open Microsoft.Extensions.DependencyInjection

    let addServices (services: IServiceCollection) =
        services.AddSingleton<FrameBuffer>() |> ignore

    /// multipart/x-mixed-replace: the Chromecast's default media receiver
    /// renders this as a live image without needing HLS or a transcode.
    let private streamMjpeg (buffer: FrameBuffer) (ctx: HttpContext) =
        task {
            let boundary = "ndframe"
            ctx.Response.ContentType <-
                $"multipart/x-mixed-replace; boundary={boundary}"
            ctx.Response.Headers.CacheControl <- "no-cache, no-store"
            let body = ctx.Response.Body
            // Wait for the first composition rather than starting with an
            // empty body: go2rtc's ffmpeg producer gives up with EOF if the
            // stream opens without a frame.
            let mutable waited = 0
            while buffer.Get().Length = 0 && waited < 30000
                  && not ctx.RequestAborted.IsCancellationRequested do
                do! Task.Delay(200, ctx.RequestAborted)
                waited <- waited + 200

            let sw = Diagnostics.Stopwatch.StartNew()
            let mutable sent = 0L
            while not ctx.RequestAborted.IsCancellationRequested do
                let frame = buffer.Get()
                if frame.Length > 0 then
                    let header =
                        Text.Encoding.ASCII.GetBytes(
                            $"--{boundary}\r\nContent-Type: image/jpeg\r\n" +
                            $"Content-Length: {frame.Length}\r\n\r\n")
                    do! body.WriteAsync(header, 0, header.Length)
                    do! body.WriteAsync(frame, 0, frame.Length)
                    do! body.WriteAsync(Text.Encoding.ASCII.GetBytes "\r\n", 0, 2)
                    do! body.FlushAsync()
                    sent <- sent + 1L
                // pace against elapsed time so slow writes do not compound
                // into ever-increasing drift
                let target = sent * int64 buffer.FrameDelayMs
                let drift  = target - sw.ElapsedMilliseconds
                if drift > 0L then
                    do! Task.Delay(int drift, ctx.RequestAborted)
        } :> Task

    /// Single still, handy for checking the composition in a browser.
    let private stillJpeg (buffer: FrameBuffer) (ctx: HttpContext) =
        task {
            let frame = buffer.Get()
            if frame.Length = 0 then
                ctx.Response.StatusCode <- 503
            else
                ctx.Response.ContentType <- "image/jpeg"
                do! ctx.Response.Body.WriteAsync(frame, 0, frame.Length)
        } :> Task

    let mapEndpoints (app: IApplicationBuilder) =
        app.Map("/cast/cameras.mjpeg", fun b ->
            b.Run(fun ctx ->
                streamMjpeg (ctx.RequestServices.GetRequiredService<FrameBuffer>()) ctx))
        |> ignore
        app.Map("/cast/cameras.jpg", fun b ->
            b.Run(fun ctx ->
                stillJpeg (ctx.RequestServices.GetRequiredService<FrameBuffer>()) ctx))
        |> ignore
