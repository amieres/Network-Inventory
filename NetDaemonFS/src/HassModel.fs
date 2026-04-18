namespace HassModel

open NetDaemon.HassModel.Entities;
open System;
open System.Reactive.Linq;
open Microsoft.Extensions.Logging;
open NetDaemon.AppModel;
open NetDaemon.HassModel;
open HomeAssistantGenerated;

[<NetDaemonApp>] 
type LightOnMovement(ha:IHaContext) =
    let pairLightTo (name:string) title (light:LightEntity) (ent:BinarySensorEntity) where =
        ent .StateChanges()
            .Where( fun x -> where ent light x)
            .Subscribe(fun e ->
                light.Toggle()
                ha.CallService("notify", "persistent_notification",
                            data = {|
                                    message = $"Toggled {name} light it was {light.State} "
                                    title   = title
                                |}
                    )
            )
        |> ignore

    let entities = new Entities(ha);
    do pairLightTo 
        "Game Room" "F# Kids Room Occupancy!" 
        entities.Light.GameRoom 
        entities.BinarySensor.KidsRoomOccupancy
        (fun ent light _ -> ent.IsOn() <> light.IsOn()) 
    do pairLightTo 
        "GameRoom"  "F# Kids Room Motion!"    
        entities.Light.GameRoom 
        entities.BinarySensor.KidsRoomMotion
        (fun ent light _ -> ent.IsOn() && light.IsOff()) 

