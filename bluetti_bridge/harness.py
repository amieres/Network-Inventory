"""One-off test of bluetti_bridge.py against AC500 2233000085979 (switch.ac500_*).

Run with the service stopped. Prints what the bridge would publish, the raw PV
registers 86-88, one pack sweep, then sends these commands and prints the
read-back after each:
  auto_sleep_mode     FIVE_MINUTES   (its current value - proves the write path)
  dc_output_on        ON, then OFF   (DC is OFF now)
  battery_range_start 36, then 35    (35 now)
Does NOT touch the AC output.
"""
import asyncio, logging, time
import bluetti_bridge as b

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
for n in ("bluetti_bt_lib", "bleak"):
    logging.getLogger(n).setLevel(logging.ERROR)

class FakeMq:
    def publish(self, t, p): print("PUB", t.split("/", 3)[3], p, flush=True)

async def main():
    u = b.Unit("24:4C:AB:2C:C7:DE", "2233000085979", FakeMq(), asyncio.Lock())

    async def script():
        while u.reader is None or not u.reader.encryption.is_ready_for_commands:
            await asyncio.sleep(1)
        await asyncio.sleep(3)
        raw = await u.read_block(86, 3)
        print("RAW 86-88:", [b.u16(raw, i) for i in range(3)] if raw else None, flush=True)
        while time.monotonic() < u.next_pack_sweep - b.PACK_SECS + 75:   # let the first pack sweep finish
            await asyncio.sleep(2)
        for topic, payload, pause in [("auto_sleep_mode", "FIVE_MINUTES", 5),
                                      ("dc_output_on", "ON", 15), ("dc_output_on", "OFF", 10),
                                      ("battery_range_start", "36", 10), ("battery_range_start", "35", 10)]:
            print(">>> COMMAND", topic, payload, flush=True)
            u.enqueue(topic, payload)
            await asyncio.sleep(pause)
        raise SystemExit

    b.POLL_SECS = 20
    await asyncio.gather(u.session(), script())

try:
    asyncio.run(main())
except (SystemExit, RuntimeError):
    pass
