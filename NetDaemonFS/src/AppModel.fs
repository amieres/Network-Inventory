namespace AppModel

open NetDaemon.HassModel.Entities;
open System;
open System.Reactive.Linq;
open Microsoft.Extensions.Logging;
open NetDaemon.AppModel;
open NetDaemon.HassModel;
open HomeAssistantGenerated;


type HelloConfig() =
    member val HelloMessage: string option = None with get, set

[<NetDaemonApp>]
type HelloYamlApp(ha: IHaContext, config: IAppConfig<HelloConfig>) =
    do
        ha.CallService(
            "notify",
            "persistent_notification",
            data = {| 
                message = config.Value.HelloMessage
                title = "F# Hello yaml app!" 
            |}
        )