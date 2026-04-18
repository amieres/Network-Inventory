namespace HelloWorld

open NetDaemon.HassModel.Entities;
open System;
open System.Reactive.Linq;
open Microsoft.Extensions.Logging;
open NetDaemon.AppModel;
open NetDaemon.HassModel;
open HomeAssistantGenerated;

/// <summary>
///     Hello world showcase using the new HassModel API
/// </summary>
[<NetDaemonApp>]
type HelloWorldApp(ha: IHaContext) =
    do
        ha.CallService(
            "notify",
            "persistent_notification",
            data = dict [
                "message", box "Notify me"
                "title", box "Hello world from F#!"
            ]
        )
        |> ignore