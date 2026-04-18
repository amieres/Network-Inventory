namespace Extensions.Scheduling

open NetDaemon.HassModel.Entities;
open System;
open System.Reactive.Linq;
open Microsoft.Extensions.Logging;
open NetDaemon.AppModel;
open NetDaemon.HassModel;
open HomeAssistantGenerated;

open NetDaemon.Extensions.Scheduler
open NetDaemon.AppModel
open System

[<NetDaemonApp>]
type SchedulingApp(ha: IHaContext, scheduler: INetDaemonScheduler) =
    let mutable count = 0
    do
        scheduler.RunEvery(TimeSpan.FromSeconds(5.0), fun () ->
            // Make sure we do not flood the notifications :)
            if count < 3 then
                ha.CallService(
                    "notify",
                    "persistent_notification",
                    data = {| message = "This is a scheduled action!"; title = "Schedule!" |}
                )
                count <- count + 1
        )
        |> ignore