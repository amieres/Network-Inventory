"""AC500 <-> MQTT bridge for Bluetti IoT firmware that encrypts Bluetooth.

Replaces bluetti_mqtt for the two AC500s after their 2026-10-02 IoT update made
Bluetooth encrypted (bluetti_mqtt cannot do the handshake); the AC200M stays on
bluetti_mqtt. bluetti-bt-lib 0.1.8 supplies only the encryption handshake and
the Modbus framing - the register map, block reads, pack polling, payload
formats and command topics are bluetti_mqtt 0.16's, so the existing Home
Assistant entities (discovery is retained on the broker) keep working, controls
included.

Deliberate choices:
  * one long-lived connection per unit - connect churn is a suspect in the
    AC500 "wedged Bluetooth" fault; one connect attempt per cycle, exponential
    back-off, never bleak_retry_connector's 10 back-to-back attempts;
  * 3 block reads per poll (as bluetti_mqtt), not ~20 single-register reads;
  * commands older than COMMAND_MAX_AGE when the link can serve them are
    dropped, never replayed late; retained command messages are ignored;
  * every write is checked against the device's echo, then read back and the
    actual state is published.
"""

import asyncio
import json
import logging
import os
import struct
import time
from logging.handlers import RotatingFileHandler

import paho.mqtt.client as mqtt
from bleak import BleakClient, BleakScanner
from bluetti_bt_lib.bluetooth.device_reader import DeviceReader, DeviceReaderConfig
from bluetti_bt_lib.const import NOTIFY_UUID
from bluetti_bt_lib.registers import ReadableRegisters
from bluetti_bt_lib.registers.WriteableRegister import WriteableRegister
from bluetti_bt_lib.utils.device_builder import build_device

UNITS = {  # BLE MAC          : serial (topic name AC500-<serial>)
    "24:4C:AB:2C:C7:DE": "2233000085979",
    "10:97:BD:3F:32:72": "2237000194280",
}
POLL_SECS       = 30
PACK_SECS       = 300   # full pack sweep (6 packs x ~10 s settle) at most this often
PACK_SETTLE     = 10    # bluetti_mqtt: "wait after switching packs for the data to be available"
PACK_MAX        = 6
BACKOFF_MIN     = 30
BACKOFF_MAX     = 600
CONNECT_SECS    = 30
HANDSHAKE_SECS  = 60    # connect + handshake measured at 2-23 s
MAX_BAD_POLLS   = 3     # consecutive failed polls before dropping the link
COMMAND_MAX_AGE = 60
READBACK_SECS   = 3     # after a write, the unit reports the new value only seconds later
READBACK_TRIES  = 4

# ── register map (bluetti_mqtt 0.16 core/devices/ac500.py) ───────────────────
UPS_MODE   = {"CUSTOMIZED": 1, "PV_PRIORITY": 2, "STANDARD": 3, "TIME_CONTROL": 4}
SLEEP_MODE = {"THIRTY_SECONDS": 2, "ONE_MINUTE": 3, "FIVE_MINUTES": 4, "NEVER": 5}
OUT_MODE   = {0: "STOP", 1: "INVERTER_OUTPUT", 2: "BYPASS_OUTPUT_C", 3: "BYPASS_OUTPUT_D", 4: "LOAD_MATCHING"}
MACHINE    = {0: "SLAVE", 1: "MASTER"}

def u16 (b, i): return struct.unpack_from("!H", b, 2 * i)[0]
def dec (s):    return lambda b, i: u16(b, i) / 10 ** s
def dec32(s):   return lambda b, i: (u16(b, i + 1) << 16 | u16(b, i)) / 10 ** s   # low word first
def flag(b, i): return "ON" if u16(b, i) == 1 else "OFF"
def enum(m):    return lambda b, i: m.get(u16(b, i), str(u16(b, i)))
def ver (b, i): return (u16(b, i) + (u16(b, i + 1) << 16)) / 100

#          start, count, [(address, topic, decoder)]
BLOCKS = [
    (10, 40, [(23, "arm_version", ver), (25, "dsp_version", ver),
              (36, "dc_input_power", u16), (37, "ac_input_power", u16),
              (38, "ac_output_power", u16), (39, "dc_output_power", u16),
              (41, "power_generation", dec32(1)), (43, "total_battery_percent", u16)]),
    (70, 21, [(70, "ac_output_mode", enum(OUT_MODE)), (71, "internal_ac_voltage", dec(1)),
              (74, "internal_ac_frequency", dec(2)), (77, "ac_input_voltage", dec(1)),
              (80, "ac_input_frequency", dec(2)),
              (86, "dc_input_voltage1", dec(1)), (87, "dc_input_power1", u16),
              (88, "dc_input_current1", dec(1))]),
    (3001, 61, [(3001, "ups_mode", enum({v: k for k, v in UPS_MODE.items()})),
                (3004, "split_phase_on", flag), (3005, "split_phase_machine_mode", enum(MACHINE)),
                (3007, "ac_output_on", flag), (3008, "dc_output_on", flag),
                (3011, "grid_charge_on", flag), (3013, "time_control_on", flag),
                (3015, "battery_range_start", u16), (3016, "battery_range_end", u16),
                (3061, "auto_sleep_mode", enum({v: k for k, v in SLEEP_MODE.items()}))]),
]
SETTINGS_BLOCK = BLOCKS[2]
PACK_BLOCK     = (91, 37)   # 96 pack_num, 98 pack voltage /100, 99 percent, 105-120 cells /100
PACK_SELECTOR  = 3006
LOGGED_ONCE    = {"arm_version", "dsp_version"}   # logged, not published

def on_off(v):
    return {"ON": 1, "OFF": 0}[v.upper()]
def percent(v):
    n = int(float(v))
    if not 0 <= n <= 100:
        raise ValueError(f"{v} outside 0-100")
    return n

COMMANDS = {  # topic: (register, payload -> register value)
    "ac_output_on"       : (3007, on_off),
    "dc_output_on"       : (3008, on_off),
    "grid_charge_on"     : (3011, on_off),
    "time_control_on"    : (3013, on_off),
    "ups_mode"           : (3001, lambda v: UPS_MODE[v]),
    "battery_range_start": (3015, percent),
    "battery_range_end"  : (3016, percent),
    "auto_sleep_mode"    : (3061, lambda v: SLEEP_MODE[v]),
}

HERE = os.path.dirname(os.path.abspath(__file__))
log  = logging.getLogger("bridge")


def fmt(v) -> str:
    if isinstance(v, float):
        return str(int(v)) if v.is_integer() else f"{v:.2f}".rstrip("0").rstrip(".")
    return str(v)


class LinkLost(Exception):
    pass


class Unit:
    def __init__(self, mac, serial, mq, connect_lock):
        self.mac, self.serial, self.mq, self.connect_lock = mac, serial, mq, connect_lock
        self.tag      = f"AC500-{serial}"
        self.device   = build_device("AC500" + serial)
        self.commands: asyncio.Queue = asyncio.Queue()
        self.reader   = None
        self.next_pack_sweep = 0.0
        self.versions_logged = False

    # ── Modbus over the encrypted link ──────────────────────────────────────
    async def request(self, frame):
        resp = bytes(await self.reader._async_send_command(frame))
        if len(resp) < 5 or not frame.is_valid_response(resp) or frame.is_exception_response(resp):
            return None
        return resp

    async def read_block(self, start, count):
        resp = await self.request(ReadableRegisters(start, count))
        if resp is None or resp[2] != 2 * count:
            return None
        return resp[3:-2]

    async def write(self, address, value) -> bool:
        resp = await self.request(WriteableRegister(address, value))
        return resp is not None and resp[1] == 0x06 and struct.unpack_from("!HH", resp, 2) == (address, value)

    # ── polling ─────────────────────────────────────────────────────────────
    def publish(self, topic, payload):
        self.mq.publish(f"bluetti/state/{self.tag}/{topic}", payload)

    async def poll_block(self, block) -> bool:
        start, count, fields = block
        body = await self.read_block(start, count)
        if body is None:
            return False
        for address, topic, decode in fields:
            value = fmt(decode(body, address - start))
            if topic in LOGGED_ONCE:
                if not self.versions_logged:
                    log.info("%s %s %s", self.tag, topic, value)
            else:
                self.publish(topic, value)
        if start == 10:
            self.versions_logged = True
        return True

    async def poll(self) -> bool:
        ok = True
        for block in BLOCKS:
            ok = await self.poll_block(block) and ok
        return ok

    async def pack_sweep(self):
        good = None
        for pack in range(1, PACK_MAX + 1):
            if not await self.write(PACK_SELECTOR, pack):
                log.warning("%s pack %d select failed", self.tag, pack)
                return
            await self.idle(PACK_SETTLE)
            body = await self.read_block(*PACK_BLOCK)
            if body is None or u16(body, 96 - 91) != pack:
                log.warning("%s pack %d read failed or reported another pack", self.tag, pack)
                continue
            details = {"percent" : u16(body, 99 - 91),
                       "voltage" : u16(body, 98 - 91) / 100,
                       "voltages": [u16(body, 105 - 91 + c) / 100 for c in range(16)]}
            self.publish(f"pack_details{pack}", json.dumps(details, separators=(",", ":")))
            good = body
        if good is not None:
            self.publish("total_battery_voltage", fmt(u16(good, 92 - 91) / 10))

    # ── commands ────────────────────────────────────────────────────────────
    def enqueue(self, topic, payload):   # called on the asyncio loop
        self.commands.put_nowait((time.monotonic(), topic, payload))

    async def run_command(self, queued_at, topic, payload):
        age = time.monotonic() - queued_at
        if age > COMMAND_MAX_AGE:
            log.warning("%s dropped stale command %s=%s (%.0fs old)", self.tag, topic, payload, age)
            return
        if topic not in COMMANDS:
            log.warning("%s ignored unsupported command %s=%s", self.tag, topic, payload)
            return
        address, encode = COMMANDS[topic]
        try:
            value = encode(payload)
        except (KeyError, ValueError) as e:
            log.warning("%s rejected command %s=%r: %s", self.tag, topic, payload, e)
            return
        ok = await self.write(address, value)
        log.info("%s command %s=%s -> register %d=%d %s", self.tag, topic, payload, address, value,
                 "OK" if ok else "FAILED")
        # The AC500 acknowledges at once but reports the new value only seconds later
        # (measured: an immediate read-back still showed the old one), so wait before
        # publishing what the unit actually did - else HA's switch flicks back.
        start = SETTINGS_BLOCK[0]
        for _ in range(READBACK_TRIES):
            await asyncio.sleep(READBACK_SECS)
            body = await self.read_block(start, SETTINGS_BLOCK[1])
            if body is not None and u16(body, address - start) == value:
                break
        else:
            log.warning("%s %s still not %d after %ds", self.tag, topic, value, READBACK_TRIES * READBACK_SECS)
        await self.poll_block(SETTINGS_BLOCK)

    async def drain_stale(self):
        while not self.commands.empty():
            await self.run_command(*self.commands.get_nowait())

    async def idle(self, seconds):
        """Wait, executing any commands that arrive meanwhile."""
        end = time.monotonic() + seconds
        while (left := end - time.monotonic()) > 0:
            if not self.reader.client.is_connected:
                raise LinkLost()
            try:
                cmd = await asyncio.wait_for(self.commands.get(), timeout=min(left, 5))
            except asyncio.TimeoutError:
                continue
            await self.run_command(*cmd)

    # ── connection lifecycle ────────────────────────────────────────────────
    async def session(self):
        """One connection: connect, handshake, serve until it fails. Returns polls served."""
        loop   = asyncio.get_running_loop()
        reader = DeviceReader(self.mac, self.device, loop.create_future,
                              DeviceReaderConfig(use_encryption=True), lock=asyncio.Lock())
        client = None
        polls  = 0
        try:
            async with self.connect_lock:   # BlueZ handles one LE connect at a time
                dev = await BleakScanner.find_device_by_address(self.mac, timeout=20)
                if dev is None:
                    log.warning("%s not seen in scan", self.tag)
                    return 0
                client = BleakClient(dev, timeout=CONNECT_SECS)
                t0 = time.monotonic()
                await client.connect()
            reader.client = client
            self.reader   = reader
            await client.start_notify(NOTIFY_UUID, reader._notification_handler)
            for _ in range(HANDSHAKE_SECS):
                if reader.encryption.is_ready_for_commands:
                    break
                await asyncio.sleep(1)
            else:
                log.warning("%s connected but encryption handshake did not finish in %ss", self.tag, HANDSHAKE_SECS)
                return 0
            log.info("%s connected, handshake done in %.1fs", self.tag, time.monotonic() - t0)
            await self.drain_stale()
            bad = 0
            while client.is_connected:
                if await self.poll():
                    bad = 0
                    polls += 1
                else:
                    bad += 1
                    log.warning("%s incomplete poll (%d/%d)", self.tag, bad, MAX_BAD_POLLS)
                    if bad >= MAX_BAD_POLLS:
                        break
                if time.monotonic() >= self.next_pack_sweep:
                    self.next_pack_sweep = time.monotonic() + PACK_SECS
                    await self.pack_sweep()
                await self.idle(POLL_SECS)
            log.warning("%s link ended after %d polls (connected=%s)", self.tag, polls, client.is_connected)
        except LinkLost:
            log.warning("%s link lost after %d polls", self.tag, polls)
        except Exception as e:
            log.warning("%s %s: %s", self.tag, type(e).__name__, e)
        finally:
            self.reader = None
            if client is not None:
                try:
                    await client.disconnect()
                except Exception:
                    pass
        return polls

    async def run(self):
        backoff = BACKOFF_MIN
        while True:
            polls = await self.session()
            backoff = BACKOFF_MIN if polls else min(backoff * 2, BACKOFF_MAX)
            log.info("%s reconnecting in %ds", self.tag, backoff)
            await asyncio.sleep(backoff)


def load_config():
    with open(os.path.join(HERE, "config.json")) as f:
        return json.load(f)


async def main():
    cfg   = load_config()
    loop  = asyncio.get_running_loop()
    lock  = asyncio.Lock()
    mq    = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="bluetti-bridge-ac500")
    units = {sn: Unit(mac, sn, mq, lock) for mac, sn in UNITS.items()}

    def on_connect(client, userdata, flags, reason, props):
        for sn in units:
            client.subscribe(f"bluetti/command/AC500-{sn}/+")
        log.info("MQTT connected (%s), subscribed to command topics", reason)

    def on_message(client, userdata, msg):   # paho thread
        if msg.retain:
            return   # a retained command is a stale command
        _, _, dev, topic = msg.topic.split("/", 3)
        unit = units.get(dev.removeprefix("AC500-"))
        if unit:
            loop.call_soon_threadsafe(unit.enqueue, topic, msg.payload.decode(errors="replace").strip())

    mq.on_connect, mq.on_message = on_connect, on_message
    mq.username_pw_set(cfg["username"], cfg["password"])
    mq.connect(cfg["broker"], cfg.get("port", 1883))
    mq.loop_start()
    log.info("starting: %s", ", ".join(u.tag for u in units.values()))
    await asyncio.gather(*(u.run() for u in units.values()))


if __name__ == "__main__":
    handler = RotatingFileHandler(os.path.join(HERE, "bridge.log"), maxBytes=2_000_000, backupCount=5)
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)-7s %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler, logging.StreamHandler()])
    for noisy in ("bluetti_bt_lib", "bleak"):
        logging.getLogger(noisy).setLevel(logging.ERROR)
    asyncio.run(main())
