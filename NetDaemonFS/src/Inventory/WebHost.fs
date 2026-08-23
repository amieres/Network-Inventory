module Inventory.WebHost

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Falco.Extensions   // IApplicationBuilder.UseFalco
open Inventory
open Inventory.ScanService

// ── ASP.NET Core host configuration ──────────────────────────────────────────

/// Add inventory-specific DI registrations.
let addInventoryServices (cfg: Microsoft.Extensions.Configuration.IConfiguration) (services: IServiceCollection) =
    services
        .Configure<InventoryConfig>(cfg.GetSection("NetworkInventory"))
        .AddHttpClient()
        .AddRouting()       // required for Falco endpoint routing
        |> ignore
    // "netgear" named client: cookies enabled so session cookie from
    // unauth.cgi handshake is carried into the subsequent AJAX retry.
    services.AddHttpClient("netgear") |> ignore
    // Register ScanService as both a resolvable singleton and a hosted service
    services.AddSingleton<ScanService>()           |> ignore
    services.AddHostedService<ScanService>(fun sp -> sp.GetRequiredService<ScanService>()) |> ignore

/// Configure the Kestrel web application pipeline.
let configureWebHost (webBuilder: IWebHostBuilder) =
    webBuilder.Configure(fun app ->
        let sp  = app.ApplicationServices
        let svc = sp.GetRequiredService<ScanService>()
        let log = sp.GetRequiredService<ILogger<ScanService>>()

        app.UseDefaultFiles()    // serves index.html for /
           .UseStaticFiles()
           .UseRouting()
           .UseFalco(Api.routes svc log)
        |> ignore
    ) |> ignore
