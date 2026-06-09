#!/usr/bin/env python3
"""Investigate why PRED-SNAP-OVERFLOW fires at C0 (zero latency).

Hypothesis 1: engine freezes from rollback resims cause the prediction
buffer to trim/clear at the wrong moment, leaving snap-overflows.

Hypothesis 2: clock-sync corrections (coarse step) bump _currentTick in
a way that makes incoming snapshots fall outside the buffer.

For each PRED-SNAP-OVERFLOW event, capture:
  - the snapshot tick
  - the client's current tick at that moment
  - the difference (depth at snap)
  - whether a recent rollback or engine freeze fired in the previous N events

Usage: python snap_overflow_root_cause.py <client.log>
"""
import os, re, sys

# Time-ordered event types we care about
SNAP_OVF_RX  = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[PRED-(?:SNAP|PVB)-OVERFLOW\] tick=(\d+) entities=\d+")
NET_SNAP_RX  = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[NET-SNAP-RX\] tick=(\d+) entities=\d+ .*?curTick=(-?\d+) rawTick=(-?\d+) rawAge=(-?\d+) avgLat=(-?\d+) jitter=(-?\d+) depth=(-?\d+)")
ROLLBACK_RX  = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[PRED-ROLLBACK\] tick=(\d+) entities=\d+ resimTicks=(\d+)")
TICK_RX      = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[CLIENT-TICK\] tick=(\d+) dt=([\d.]+)")
CLOCK_SYNC   = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[CLOCK-SYNC-RX\] sample=\d+ rttMs=(\d+) halfRttMs=(\d+) halfRttTicks=(\d+) srvTick=(\d+) cliTick=(\d+) immediateOffset=(-?\d+)")
PRED_REG     = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[PRED-REG\] tick=(\d+) eid=1 ")
CATCHUP_RX   = re.compile(r"\[(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3})\].*?\[CLIENT-TICK-CATCHUP\] back-filling ticks (\d+)\.\.(\d+) \((\d+) dropped")


def parse(log):
    events = []
    with open(log, encoding="utf-8", errors="ignore") as f:
        for i, line in enumerate(f):
            for rx, kind in [
                (NET_SNAP_RX,  "snap_rx"),
                (SNAP_OVF_RX,  "snap_ovf"),
                (ROLLBACK_RX,  "rollback"),
                (TICK_RX,      "tick"),
                (CLOCK_SYNC,   "clock_sync"),
                (PRED_REG,     "pred_reg"),
                (CATCHUP_RX,   "catchup"),
            ]:
                m = rx.search(line)
                if m:
                    events.append({"i": i, "kind": kind, "m": m.groups()})
                    break
    return events


def main():
    log = sys.argv[1]
    events = parse(log)
    print(f"parsed {len(events)} events from {log}")

    # Index of last events by kind so we can look up context for each snap_ovf
    snap_ovfs = []
    last_freeze = None   # (i, dt_ms)
    last_rollback = None # (i, tick, depth)
    last_clock_sync_offset = None  # (i, offset)
    last_catchup = None  # (i, start, end, n)
    last_tick = None      # (i, client_tick, dt)

    for e in events:
        i = e["i"]
        if e["kind"] == "tick":
            last_tick = (i, int(e["m"][1]), float(e["m"][2]))
            dt_ms = float(e["m"][2]) * 1000
            if dt_ms > 30:
                last_freeze = (i, dt_ms)
        elif e["kind"] == "rollback":
            last_rollback = (i, int(e["m"][1]), int(e["m"][2]))
        elif e["kind"] == "clock_sync":
            offset = int(e["m"][5])
            if abs(offset) >= 5:
                last_clock_sync_offset = (i, offset)
        elif e["kind"] == "catchup":
            last_catchup = (i, int(e["m"][1]), int(e["m"][2]), int(e["m"][3]))
        elif e["kind"] == "snap_ovf":
            snap_ovfs.append({
                "i": i,
                "snap_tick": int(e["m"][1]),
                "last_tick": last_tick,
                "last_freeze": last_freeze,
                "last_rollback": last_rollback,
                "last_clock_sync_offset": last_clock_sync_offset,
                "last_catchup": last_catchup,
            })

    # Categorize each snap_ovf by likely root cause
    freeze_window = 5    # event-index distance — "recent"
    rollback_window = 8
    catchup_window = 5
    clock_window = 50

    by_cause = {"freeze": 0, "rollback": 0, "catchup": 0, "clock-jump": 0, "unattributed": 0}
    examples_by_cause = {k: [] for k in by_cause}

    for s in snap_ovfs:
        i = s["i"]
        attribution = []
        if s["last_freeze"] and (i - s["last_freeze"][0]) <= freeze_window:
            attribution.append(f"freeze({s['last_freeze'][1]:.0f}ms,{i - s['last_freeze'][0]}evs ago)")
        if s["last_catchup"] and (i - s["last_catchup"][0]) <= catchup_window:
            attribution.append(f"catchup({s['last_catchup'][3]}ticks,{i - s['last_catchup'][0]}evs ago)")
        if s["last_rollback"] and (i - s["last_rollback"][0]) <= rollback_window:
            attribution.append(f"rollback(depth={s['last_rollback'][2]},{i - s['last_rollback'][0]}evs ago)")
        if s["last_clock_sync_offset"] and (i - s["last_clock_sync_offset"][0]) <= clock_window:
            attribution.append(f"clock-jump({s['last_clock_sync_offset'][1]:+d},{i - s['last_clock_sync_offset'][0]}evs ago)")

        if attribution:
            kind = attribution[0].split("(")[0]
            by_cause[kind] = by_cause.get(kind, 0) + 1
            if len(examples_by_cause[kind]) < 3:
                examples_by_cause[kind].append((s["snap_tick"], attribution))
        else:
            by_cause["unattributed"] += 1
            if len(examples_by_cause["unattributed"]) < 3:
                examples_by_cause["unattributed"].append((s["snap_tick"], ["(nothing nearby)"]))

    print(f"\n=== SNAP-OVERFLOW root-cause attribution ({len(snap_ovfs)} events) ===")
    print(f"  Windows: freeze<={freeze_window} catchup<={catchup_window} rollback<={rollback_window} clock-jump<={clock_window} events back")
    print()
    total = sum(by_cause.values()) or 1
    # Priority order: freeze > catchup > rollback > clock-jump > unattributed
    for cause in ["freeze", "catchup", "rollback", "clock-jump", "unattributed"]:
        n = by_cause.get(cause, 0)
        pct = n / total * 100
        print(f"  {cause:>14}: {n:>4} ({pct:>5.1f}%)")
        for snap_tick, atts in examples_by_cause[cause]:
            print(f"      e.g. snap_tick={snap_tick}: {', '.join(atts)}")
    print()

    # Also report freeze counts for context
    freezes = sum(1 for e in events if e["kind"] == "tick" and float(e["m"][2]) > 0.030)
    big_freezes = sum(1 for e in events if e["kind"] == "tick" and float(e["m"][2]) > 0.050)
    catchups = sum(1 for e in events if e["kind"] == "catchup")
    clock_corrections = sum(1 for e in events if e["kind"] == "clock_sync" and abs(int(e["m"][5])) >= 5)
    print(f"=== context ===")
    print(f"  physics frames dt>30ms: {freezes}")
    print(f"  physics frames dt>50ms: {big_freezes}")
    print(f"  client-tick catchups:   {catchups}")
    print(f"  clock-sync corrections >=5 ticks: {clock_corrections}")


if __name__ == "__main__":
    main()
