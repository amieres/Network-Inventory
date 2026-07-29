namespace EG4Battery

open System
open System.Net.Sockets
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open MQTTnet
open MQTTnet.Client
open NetDaemon.AppModel
open NetDaemon.Extensions.Scheduler
open NetDaemon.Extensions.MqttEntityManager

// ---------------------------------------------------------------------------
// Talks to the two EG4 LifePower4 batteries over the ESP32-S3's `stream_server`
// TCP bridge (raw bytes over the USB-OTG-connected FTDI cable -- see the
// ESP32 YAML for the usb_host/usb_uart/stream_server setup). The ESP32 does
// no parsing of its own; all protocol logic lives here.
//
// Frame format: 7E <addr> <cmd> <len> <payload...> <checksum> 0D
// Checksums for the "general status" command (cmd 0x01, len 0x00) were found
// by sweeping all 256 values against the real hardware -- they aren't a
// documented/standard CRC:
//   Battery 1 (DIP address 1): 7E 01 01 00 FE 0D
//   Battery 2 (DIP address 2): 7E 02 01 00 FC 0D
// The response payload layout (16 cells, current, remaining/full capacity,
// 6 temps, 5 alarm-word pairs, cycles, pack voltage, SOH) was confirmed
// byte-for-byte against real captured responses from both packs.
// ---------------------------------------------------------------------------

type Config() =
    member val EspHost : string = ""   with get, set
    member val EspPort : int    = 6638 with get, set

type BatteryReading =
    { Cells       : float[]   // 16 cell voltages, volts
      Temps       : float[]   // 6 temperature sensors, degrees C
      Current     : float     // amps, positive = discharging
      RemainingAh : float     // derived: Soc% * FullAh (BMS reports no remaining directly)
      FullAh      : float
      Cycles      : int
      PackVoltage : float
      Soh         : float     // percent
      Soc         : float     // percent, read directly from the BMS
      Alarms      : string[]  // decoded active protection flags, empty when clear
      Alarm       : bool      // true when any alarm/warning bit is set (known or not)
      AlarmText   : string    // "OK" only if no bits set; names for known bits; "unknown" if undecoded bits are set
      AlarmRaw    : string    // diagnostic: full alarm group as hex
      TempsRaw    : string }  // diagnostic: full temperature group as hex

module Protocol =
    let batt1Request: byte[] = [| 0x7Euy; 0x01uy; 0x01uy; 0x00uy; 0xFEuy; 0x0Duy |]
    let batt2Request: byte[] = [| 0x7Euy; 0x02uy; 0x01uy; 0x00uy; 0xFCuy; 0x0Duy |]

    let private readExact (stream: NetworkStream) (count: int) : byte[] =
        let buf = Array.zeroCreate<byte> count
        let mutable offset = 0
        while offset < count do
            let n = stream.Read(buf, offset, count - offset)
            if n <= 0 then failwith "EG4 battery: connection closed while reading"
            offset <- offset + n
        buf

    /// Sends a request frame and reads back one complete, length-delimited
    /// response frame. Frame boundaries are found via the length byte
    /// (index 3), not by scanning for 0x0D, since 0x0D can legitimately
    /// appear inside cell-voltage payload bytes.
    let query (stream: NetworkStream) (request: byte[]) : byte[] =
        stream.Write(request, 0, request.Length)
        let header = readExact stream 4 // 7E, addr, cmd, len
        let len = int header.[3]
        let rest = readExact stream (len + 2) // payload + checksum + 0x0D
        Array.append header rest

    // Protection-flag bits in group 5's second short (low byte). Mapping and the
    // whole group-walking layout come from the reference EG4 driver / powermon:
    //   https://github.com/mr-manuel/venus-os_dbus-serialbattery -> bms/eg4_lifepower.py
    //   https://github.com/slim-bean/powermon
    let private alarmBits =
        [| 0b0000_1000, "Charge over-current"
           0b0001_0000, "Over-voltage"
           0b0010_0000, "Under-voltage"
           0b0100_0000, "Charge over-temperature"
           0b1000_0000, "Charge under-temperature" |]

    // Bits in short 4 of the alarm group (undocumented word, confirmed empirically).
    let private warnBits =
        [| 0x0040, "Charging high temperature"
           0x0800, "SOC low" |]

    /// Splits the response payload into its 10 length-prefixed groups. Each group
    /// is `<group#> <shortCount> <shortCount big-endian u16s>`, so the layout is
    /// self-describing -- no fixed byte offsets that break if a count changes.
    let private readGroups (p: byte[]) : int[][] =
        let u16 i = (int p.[i] <<< 8) ||| int p.[i + 1]
        let groups = ResizeArray<int[]>()
        let mutable i = 0
        for _ in 1 .. 10 do
            let count = int p.[i + 1]
            let start = i + 2
            groups.Add [| for k in 0 .. count - 1 -> u16 (start + k * 2) |]
            i <- start + count * 2
        groups.ToArray()

    let parse (frame: byte[]) : BatteryReading =
        let p = frame.[4..] // payload starts right after the 4-byte header
        let g = readGroups p // throws IndexOutOfRange on a short/garbled frame -> caught by caller

        // Top two bits of each cell short are a high-voltage flag; mask to 0x3FFF.
        let cells = [| for v in g.[0] -> float (v &&& 0x3FFF) / 1000.0 |]
        let current = float (30000 - g.[1].[0]) / 100.0
        let soc = float g.[2].[0] / 100.0
        let fullAh = float g.[3].[0] / 100.0
        let temps = [| for v in g.[4] -> float (v &&& 0xFF) - 50.0 |]
        let cycles = g.[6].[0]
        let packVoltage = float g.[7].[0] / 100.0
        let soh = if g.Length > 8 && g.[8].Length > 0 then float g.[8].[0] / 100.0 else 0.0

        // BMS reports no remaining-Ah directly; derive it from SOC and full capacity.
        let remainingAh = soc / 100.0 * fullAh

        // Group 5 alarm word. short1 = protection bits + charge/discharge STATUS
        // (0x01 charging / 0x02 discharging -- normal, not an alarm), short4 = the
        // undocumented warning word (0x0800 SOC low, 0x0040 charging high temp).
        // Many bits are still unknown; we must NOT report "OK" when an undecoded bit
        // is set -- report "unknown" instead so we don't hide a real alarm.
        let statusMask = 0x0003                                        // short1 charge/discharge status, ignore
        let protMask   = alarmBits |> Array.sumBy fst                  // known short1 alarm bits
        let warnMask   = warnBits  |> Array.sumBy fst                  // known short4 bits
        let protWord = if g.[5].Length > 1 then g.[5].[1] else 0
        let warnWord = if g.[5].Length > 4 then g.[5].[4] else 0
        let alarms = [| for bit, name in alarmBits do if protWord &&& bit <> 0 then name
                        for bit, name in warnBits  do if warnWord &&& bit <> 0 then name |]
        // Any set bit outside the status + known-alarm masks is an undecoded flag.
        let unknownProt = protWord &&& ~~~(statusMask ||| protMask)
        let unknownWarn = warnWord &&& ~~~warnMask
        let unknownOther = g.[5] |> Array.mapi (fun i v -> if i = 1 || i = 4 then 0 else v) |> Array.sum
        let hasUnknown = unknownProt <> 0 || unknownWarn <> 0 || unknownOther <> 0
        let anyBits = alarms.Length > 0 || hasUnknown
        let alarmText =
            if alarms.Length > 0 && hasUnknown then String.Join(", ", alarms) + ", unknown"
            elif alarms.Length > 0            then String.Join(", ", alarms)
            elif hasUnknown                   then "unknown"
            else "OK"
        // Diagnostics: full alarm and temperature groups as hex, to decode remaining bits.
        let alarmRaw = g.[5] |> Array.map (sprintf "%04X") |> String.concat " "
        let tempsRaw = g.[4] |> Array.map (sprintf "%04X") |> String.concat " "

        { Cells       = cells
          Temps       = temps
          Current     = current
          RemainingAh = remainingAh
          FullAh      = fullAh
          Cycles      = cycles
          PackVoltage = packVoltage
          Soh         = soh
          Soc         = soc
          Alarms      = alarms
          Alarm       = anyBits
          AlarmText   = alarmText
          AlarmRaw    = alarmRaw
          TempsRaw    = tempsRaw }

[<NetDaemonApp>]
type EG4BatteryApp
    (
        scheduler: INetDaemonScheduler,
        entityManager: IMqttEntityManager,
        config: IAppConfig<Config>,
        mqttConfig: IOptions<MqttConfiguration>,
        logger: ILogger<EG4BatteryApp>
    ) =
    let cfg = config.Value
    // Reuse the exact broker config the MqttEntityManager library connects with
    // (host + injected credentials), so our raw client authenticates the same way.
    let mq = mqttConfig.Value

    // State values are published to a clean, self-describing tree (Bluetti-style)
    // separate from the homeassistant/ discovery namespace, e.g.
    //   eg4/state/battery_1/soc  ->  18.7
    // The discovery config below points each entity's state_topic here. The
    // MqttEntityManager library can only publish state to its own derived topic,
    // so state publishing is done directly with an MQTTnet client on the same
    // broker the library uses (Mqtt:Host in appsettings, anonymous on core-mosquitto).
    let stateTopic num key = $"eg4/state/battery_{num}/{key}"

    let mqtt = (MqttFactory()).CreateMqttClient()

    let mqttOpts =
        let port = if mq.Port > 0 then mq.Port else 1883
        let b = MqttClientOptionsBuilder().WithTcpServer(mq.Host, port).WithClientId("eg4-battery-app")
        let b = if String.IsNullOrEmpty mq.UserName then b else b.WithCredentials(mq.UserName, mq.Password)
        b.Build()

    // Connect lazily and reconnect on demand so a broker hiccup never crashes the
    // NetDaemon host -- publishes are best-effort and resume once the broker is back.
    let ensureConnected () =
        if not mqtt.IsConnected then
            mqtt.ConnectAsync(mqttOpts) |> Async.AwaitTask |> Async.RunSynchronously |> ignore

    let publish (topic: string) (payload: string) =
        try
            ensureConnected ()
            MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag(true)
                .Build()
            |> fun m -> mqtt.PublishAsync(m) |> Async.AwaitTask |> Async.RunSynchronously |> ignore
        with ex ->
            logger.LogWarning(ex, "EG4 battery: MQTT publish to {Topic} failed", topic)

    let deviceFor (num: int) =
        {| identifiers = [| $"eg4_battery_{num}" |]
           name = $"EG4 Battery {num}"
           model = "LifePower4"
           manufacturer = "EG4" |}

    let awaitTask (t: Threading.Tasks.Task) = t |> Async.AwaitTask |> Async.RunSynchronously

    let sensorId       num key = $"sensor.eg4_battery_{num}_{key}"
    let binarySensorId num key = $"binary_sensor.eg4_battery_{num}_{key}"

    // Explicit unique_id/object_id (the library otherwise derives a broken unique_id
    // from the config topic) and an explicit state_topic pointing at the separate
    // state tree. suggested_display_precision fixes how many decimals HA shows
    // (the raw published value keeps full precision).
    let createSensorP (num: int) (key: string) (name: string) (deviceClass: string) (unit: string) (precision: int) =
        let uid   = $"eg4_battery_{num}_{key}"
        let opts  = EntityCreationOptions(Name = name, DeviceClass = deviceClass, UniqueId = uid)
        let extra = {| unit_of_measurement = unit; object_id = uid; state_topic = stateTopic num key
                       suggested_display_precision = precision; device = deviceFor num |}
        entityManager.CreateAsync(sensorId num key, opts, extra) |> awaitTask

    let createSensor num key name deviceClass unit = createSensorP num key name deviceClass unit 1

    // Plain text sensor: no unit/device_class/precision so HA keeps the string as-is.
    let createTextSensor (num: int) (key: string) (name: string) =
        let uid   = $"eg4_battery_{num}_{key}"
        let opts  = EntityCreationOptions(Name = name, UniqueId = uid)
        let extra = {| object_id = uid; state_topic = stateTopic num key; icon = "mdi:alert"; device = deviceFor num |}
        entityManager.CreateAsync(sensorId num key, opts, extra) |> awaitTask

    let createBinarySensor (num: int) (key: string) (name: string) =
        let uid   = $"eg4_battery_{num}_{key}"
        let opts  = EntityCreationOptions(Name = name, DeviceClass = "problem", UniqueId = uid)
        let extra = {| payload_on = "true"; payload_off = "false"; object_id = uid; state_topic = stateTopic num key; device = deviceFor num |}
        entityManager.CreateAsync(binarySensorId num key, opts, extra) |> awaitTask

    let setupEntitiesFor (num: int) =
        for i in 1..16 do
            createSensorP num $"cell_{i}" $"Cell {i} Voltage" "voltage" "V" 2

        // Group 4 holds 6 temps: 4 cell/pack sensors, then ENV and MOS (per vendor app).
        for i in 1..4 do
            createSensor num $"temp_{i}" $"Temperature {i}" "temperature" "°C"
        createSensor num "env_temp" "ENV Temperature" "temperature" "°C"
        createSensor num "mos_temp" "MOS Temperature" "temperature" "°C"

        createSensorP num "pack_voltage"      "Pack Voltage"       "voltage" "V"  2
        createSensor  num "current"            "Current"            "current" "A"
        createSensor  num "remaining_capacity" "Remaining Capacity" null      "Ah"
        createSensor  num "full_capacity"      "Full Capacity"      null      "Ah"
        createSensor  num "soc"                "State of Charge"    "battery" "%"
        createSensor  num "soh"                "State of Health"    null      "%"
        createSensorP num "cycles"             "Cycles"             null      "cycles" 0
        createTextSensor num "alarm_text" "Alarm Text"
        createTextSensor num "alarm_raw"  "Alarm Raw"
        createTextSensor num "temps_raw"  "Temps Raw"
        createBinarySensor num "alarm" "Alarm"

    let inv (v: float) = v.ToString(Globalization.CultureInfo.InvariantCulture)

    let setSensor num key (value: string) = publish (stateTopic num key) value

    let publishReading (num: int) (r: BatteryReading) =
        r.Cells |> Array.iteri (fun i v -> setSensor num $"cell_{i + 1}" (inv v))
        // Temps: indices 0-3 are Temperature 1-4, 4 = ENV, 5 = MOS.
        let tempKey i = if i < 4 then $"temp_{i + 1}" elif i = 4 then "env_temp" else "mos_temp"
        r.Temps |> Array.iteri (fun i v -> if i < 6 then setSensor num (tempKey i) (inv v))

        setSensor num "pack_voltage"       (inv r.PackVoltage)
        setSensor num "current"            (inv r.Current)
        setSensor num "remaining_capacity" (inv r.RemainingAh)
        setSensor num "full_capacity"      (inv r.FullAh)
        setSensor num "soc"                (inv r.Soc)
        setSensor num "soh"                (inv r.Soh)
        setSensor num "cycles"             (string r.Cycles)
        setSensor num "alarm_text"         r.AlarmText
        setSensor num "alarm_raw"          r.AlarmRaw
        setSensor num "temps_raw"          r.TempsRaw
        setSensor num "alarm"              (if r.Alarm then "true" else "false")

    let queryBattery stream num request =
        try
            Protocol.query stream request |> Protocol.parse |> publishReading num
        with ex ->
            logger.LogWarning(ex, "EG4 battery {Num} query failed", num)

    let pollOnce () =
        try
            use client = new TcpClient()
            client.Connect(cfg.EspHost, cfg.EspPort)
            use stream = client.GetStream()
            stream.ReadTimeout  <- 3000
            stream.WriteTimeout <- 3000

            queryBattery stream 1 Protocol.batt1Request

            // Stagger the two requests so they don't collide on the shared bus
            // (same reasoning as the 1s delay used when this ran on-device).
            Threading.Thread.Sleep(1000)

            queryBattery stream 2 Protocol.batt2Request
        with ex ->
            logger.LogWarning(ex, "EG4 battery poll failed (connection level)")

    do
        setupEntitiesFor 1
        setupEntitiesFor 2
        scheduler.RunEvery(TimeSpan.FromSeconds(5.0), pollOnce) |> ignore
