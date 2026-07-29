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
    member val Switch    : string = "" with get, set   // smart-switch entity_id used to power-cycle when SSH is unreachable
    member val OffSecs   : int    = 10 with get, set    // seconds to hold the switch off before turning it back on



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

    // SSH connect failures (e.g. "No route to host") leave the Pi unreachable, so sudo reboot never runs.
    let isUnreachable (result: string) =
        let r = result.ToLowerInvariant()
        [ "route"; "unreachable"; "timed out"; "timeout"
          "connection refused"; "could not connect"; "no such host" ; "error"]
        |> List.exists r.Contains

    let powerCycle () =
        let plug = SwitchEntity(ha, config.Switch)
        plug.CallService "turn_off"
        scheduler.RunIn(TimeSpan.FromSeconds(float config.OffSecs), fun () ->
            plug.CallService "turn_on")
        |> ignore

    let notify msg body =
        ha.CallService("notify", "persistent_notification",
            data = {| message = body; title = msg |})

    let rebootRaspi msg =
        let result = runCommand config.IpAddress config.User config.Pwd config.Command
        if isUnreachable result then
            powerCycle ()
            notify msg $"SSH reboot failed ({config.IpAddress}):\n{result}\n\nPower-cycling {config.Switch}: off {config.OffSecs}s, then on."
        else
            notify msg result
    do ha.RegisterServiceCallBack("reboot_raspi", fun _ -> 
                rebootRaspi $"F# Service Callback Rebooting {config.Computer}!" )

    do
        scheduler.RunEvery(TimeSpan.FromMinutes(30.0), fun () ->
            let entities = new Entities(ha)
            if entities.BinarySensor.Ac500Connected.IsOff() || entities.BinarySensor.Ac500Connected2.IsOff() then
                rebootRaspi $"F# Rebooting {config.Computer}!" 
        )
        |> ignore