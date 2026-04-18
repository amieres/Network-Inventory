namespace RebootRaspi

open NetDaemon.HassModel.Entities;
open System;
open System.Reactive.Linq;
open Microsoft.Extensions.Logging;
open NetDaemon.AppModel;
open NetDaemon.HassModel;
open NetDaemon.HassModel.Integration;
open HomeAssistantGenerated;

open NetDaemon.Extensions.Scheduler
open NetDaemon.AppModel
open System
open Renci.SshNet

type Config() =
    member val Computer  : string = "" with get, set
    member val IpAddress : string = "" with get, set
    member val User      : string = "" with get, set
    member val Pwd       : string = "" with get, set
    member val Command   : string = "" with get, set



[<NetDaemonApp>]
type CheckAC500s(ha: IHaContext, scheduler: INetDaemonScheduler, config: IAppConfig<Config>) =
    let config = config.Value
    let runCommand host user pwd command =
        try
            use client = new SshClient(host, user, password = pwd)
            client.Connect()
            use cmd = client.RunCommand command
            cmd.Result
        with ex -> $"Error: {ex.Message}"

    let rebootRaspi msg =
        ha.CallService("notify", "persistent_notification",
            data = {|
                message = runCommand config.IpAddress
                                     config.User     
                                     config.Pwd      
                                     config.Command  
                title = msg 
            |}
        )
    do ha.RegisterServiceCallBack("reboot_raspi", fun _ -> 
                rebootRaspi $"F# Service Callback Rebooting {config.Computer}!" )

    do
        scheduler.RunEvery(TimeSpan.FromMinutes(30.0), fun () ->
            let entities = new Entities(ha)
            if entities.BinarySensor.Ac500Connected.IsOff() || entities.BinarySensor.Ac500Connected2.IsOff() then
                rebootRaspi $"F# Rebooting {config.Computer}!" 
        )
        |> ignore