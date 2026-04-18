module AbeFsDaemon.main

open System
open System.Reflection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NetDaemon.AppModel
open NetDaemon.HassModel
open NetDaemon.Extensions.Logging
open NetDaemon.Extensions.Scheduler
open NetDaemon.Extensions.Tts
open NetDaemon.Runtime
open HomeAssistantGenerated
open Inventory.WebHost


let [<EntryPoint>] main args =
    try
        Host.CreateDefaultBuilder(args)
            .UseNetDaemonAppSettings()
            .UseNetDaemonDefaultLogging()
            .UseNetDaemonRuntime()
            .UseNetDaemonTextToSpeech()
            .ConfigureWebHostDefaults(configureWebHost)
            .ConfigureServices(fun ctx services ->
                services
                    .AddAppsFromAssembly(Assembly.GetExecutingAssembly())
                    .AddNetDaemonStateManager()
                    .AddNetDaemonScheduler()
                    .AddHomeAssistantGenerated()
                |> ignore
                addInventoryServices ctx.Configuration services
            )
            .Build()
            .RunAsync()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult()
        0
    with
    | e ->
        Console.WriteLine($"Failed to start host... {e}")
        reraise()