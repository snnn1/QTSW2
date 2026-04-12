#!/usr/bin/env python3
"""
Diagnostic: Verify secondary 1-second series and break-even detection are working.

What to look for:
- BE_PATH_ACTIVE: Proves 1-second series (BarsInProgress==1) is firing when in position.
  Contains: bars_in_progress, has_secondary_series, active_intent_count
- BE_EVALUATION_TICK: Proves CheckBreakEvenTriggersTickBased ran.
  Contains: tick_price, active_intent_count (0 = no intents need BE, >0 = intents awaiting trigger)
- ONBARUPDATE_DIAGNOSTIC: Shows bars_in_progress (0=primary, 1=secondary) when diagnostic logs enabled
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

def parse_ts(s):
    if not s:
        return None
    try:
        s = str(s).replace("Z", "+00:00")
        if "+" not in s:
            s += "+00:00"
        dt = datetime.fromisoformat(s)
        return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt
    except Exception:
        return None

def load_events(log_dir, cutoff, include_archive=False):
    events = []
    patterns = [
        "robot_*.jsonl",
    ]
    if include_archive:
        patterns.append("archive/robot_*.jsonl")
    for pat in patterns:
        for f in sorted(log_dir.glob(pat)):
            if f.name.count("_") > 2 and "archive" not in str(f):
                continue
            try:
                for line in open(f, "r", encoding="utf-8"):
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        e = json.loads(line)
                        ts = parse_ts(e.get("ts_utc", ""))
                        if ts and ts >= cutoff:
                            e["_source"] = f.name
                            events.append(e)
                    except Exception:
                        pass
            except Exception:
                pass
    events.sort(key=lambda x: parse_ts(x.get("ts_utc", "")) or datetime.min.replace(tzinfo=timezone.utc))
    return events

def main():
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=48)

    print("=" * 85)
    print("SECONDARY SERIES & BREAK-EVEN DETECTION DIAGNOSTIC")
    print("=" * 85)
    print(f"Cutoff: {cutoff.strftime('%Y-%m-%d %H:%M:%S')} UTC (last 48 hours)\n")

    events = load_events(log_dir, cutoff)

    # 1. BE_PATH_ACTIVE - proves 1-second series firing when in position
    be_path = [e for e in events if e.get("event") == "BE_PATH_ACTIVE"]
    be_path_bip1 = [e for e in be_path if e.get("data", {}).get("bars_in_progress") == 1]
    be_path_fallback = [e for e in be_path if e.get("data", {}).get("bars_in_progress") == 0]

    # 2. BE_EVALUATION_TICK - proves CheckBreakEvenTriggersTickBased ran
    be_eval = [e for e in events if e.get("event") == "BE_EVALUATION_TICK"]

    # 3. ONBARUPDATE_DIAGNOSTIC - shows bars_in_progress (0 or 1)
    onbar_diag = [e for e in events if e.get("event") == "ONBARUPDATE_DIAGNOSTIC"]
    onbar_bip1 = [e for e in onbar_diag if e.get("data", {}).get("bars_in_progress") == 1]
    onbar_bip0 = [e for e in onbar_diag if e.get("data", {}).get("bars_in_progress") == 0]

    # 4. Entry fills (for context)
    fills = [e for e in events if e.get("event") == "EXECUTION_FILLED"]
    entry_fills = [e for e in fills if "ENTRY" in str(e.get("data", {}).get("order_type", "")).upper() or not e.get("data", {}).get("order_type")]

    # 5. REALTIME_STATE_REACHED - shows secondary series at startup (no position needed)
    realtime = [e for e in events if e.get("event") == "REALTIME_STATE_REACHED"]
    realtime_with_sec = [e for e in realtime if e.get("data", {}).get("has_secondary_series") is True]
    realtime_no_sec = [e for e in realtime if e.get("data", {}).get("has_secondary_series") is False]

    # 6. BE trigger events
    be_reached = [e for e in events if e.get("event") == "BE_TRIGGER_REACHED"]
    be_failed = [e for e in events if e.get("event") == "BE_TRIGGER_FAILED"]

    print("1. SECONDARY 1-SECOND SERIES (BarsInProgress == 1)")
    print("-" * 85)
    if be_path_bip1:
        print(f"  [OK] BE_PATH_ACTIVE (BIP 1): {len(be_path_bip1)} events")
        print(f"       -> 1-second series IS firing when in position")
        for e in sorted(be_path_bip1, key=lambda x: parse_ts(x.get("ts_utc", "")) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_ts(e.get("ts_utc", ""))
            d = e.get("data", {})
            inst = d.get("instrument", "N/A")
            has_sec = d.get("has_secondary_series", "N/A")
            count = d.get("active_intent_count", "N/A")
            print(f"         {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {inst} | has_secondary={has_sec} | active_intents={count}")
    elif be_path_fallback:
        print(f"  [FALLBACK] BE_PATH_ACTIVE (BIP 0): {len(be_path_fallback)} events")
        print(f"       -> Secondary series disabled or not firing; using primary bar close")
        for e in sorted(be_path_fallback, key=lambda x: parse_ts(x.get("ts_utc", "")) or datetime.min.replace(tzinfo=timezone.utc))[-3:]:
            ts = parse_ts(e.get("ts_utc", ""))
            d = e.get("data", {})
            inst = d.get("instrument", "N/A")
            count = d.get("active_intent_count", "N/A")
            print(f"         {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {inst} | active_intents={count}")
    else:
        print("  [ISSUE] No BE_PATH_ACTIVE events found")
        print("       -> Either: 1) Never in position during session, 2) Strategy not compiled with BE path")
        print("       -> Or: 3) Old build without BE_PATH_ACTIVE logging")

    print("\n2. BE EVALUATION (CheckBreakEvenTriggersTickBased)")
    print("-" * 85)
    if be_eval:
        print(f"  [OK] BE_EVALUATION_TICK: {len(be_eval)} events")
        print(f"       -> BE evaluation is running (rate-limited ~1/sec per instrument)")
        by_inst = defaultdict(list)
        for e in be_eval:
            by_inst[e.get("data", {}).get("instrument", "?")].append(e)
        for inst, ev_list in sorted(by_inst.items()):
            latest = max(ev_list, key=lambda x: parse_ts(x.get("ts_utc", "")) or datetime.min.replace(tzinfo=timezone.utc))
            ts = parse_ts(latest.get("ts_utc", ""))
            d = latest.get("data", {})
            price = d.get("tick_price", "N/A")
            count = d.get("active_intent_count", "N/A")
            print(f"         {inst}: last {ts.strftime('%H:%M:%S') if ts else 'N/A'} | tick_price={price} | active_intents={count}")
    else:
        print("  [ISSUE] No BE_EVALUATION_TICK events found")
        print("       -> BE evaluation never ran (or no active intents when in position)")
        print("       -> If BE_PATH_ACTIVE exists but no BE_EVALUATION_TICK: GetActiveIntentsForBEMonitoring may return 0")

    print("\n3. ONBARUPDATE_DIAGNOSTIC (bars_in_progress)")
    print("-" * 85)
    if onbar_diag:
        print(f"  Total: {len(onbar_diag)} | BIP 0 (primary): {len(onbar_bip0)} | BIP 1 (secondary): {len(onbar_bip1)}")
        if onbar_bip1:
            print(f"  [OK] Secondary series (BIP 1) received bar updates")
        else:
            print(f"  [INFO] Secondary series (BIP 1) not seen in diagnostic")
    else:
        print("  [INFO] No ONBARUPDATE_DIAGNOSTIC (diagnostic_slow_logs or enable_diagnostic_logs may be off)")

    print("\n4. ENTRY FILLS (context)")
    print("-" * 85)
    if entry_fills:
        print(f"  Found {len(entry_fills)} entry fills")
        for e in sorted(entry_fills, key=lambda x: parse_ts(x.get("ts_utc", "")) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_ts(e.get("ts_utc", ""))
            d = e.get("data", {})
            inst = e.get("instrument", "N/A")
            stream = d.get("stream", "N/A")
            price = d.get("fill_price", "N/A")
            print(f"         {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {inst} {stream} | {price}")
    else:
        print("  No entry fills in window")

    print("\n5. REALTIME_STATE_REACHED (secondary series at startup)")
    print("-" * 85)
    if realtime:
        print(f"  Total: {len(realtime)} | has_secondary_series=True: {len(realtime_with_sec)} | False: {len(realtime_no_sec)}")
        for e in sorted(realtime, key=lambda x: parse_ts(x.get("ts_utc", "")) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_ts(e.get("ts_utc", ""))
            d = e.get("data", {})
            inst = d.get("instrument", "N/A")
            has_sec = d.get("has_secondary_series", "N/A")
            bars_len = d.get("bars_array_length", "N/A")
            path = d.get("be_detection_path", "N/A")
            print(f"         {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {inst} | bars_array={bars_len} | has_secondary={has_sec} | path={path}")
    else:
        print("  No REALTIME_STATE_REACHED events (or cutoff too old)")

    print("\n6. BE TRIGGER EVENTS")
    print("-" * 85)
    print(f"  BE_TRIGGER_REACHED: {len(be_reached)}")
    print(f"  BE_TRIGGER_FAILED:  {len(be_failed)}")
    if be_reached:
        for e in sorted(be_reached, key=lambda x: parse_ts(x.get("ts_utc", "")) or datetime.min.replace(tzinfo=timezone.utc))[-5:]:
            ts = parse_ts(e.get("ts_utc", ""))
            d = e.get("data", {})
            print(f"         {ts.strftime('%H:%M:%S') if ts else 'N/A'} | {d.get('stream','')} | trigger={d.get('be_trigger_price')}")

    print("\n7. SUMMARY")
    print("-" * 85)
    secondary_ok = bool(be_path_bip1 or (be_path_fallback and be_eval))
    be_eval_ok = bool(be_eval)
    be_trigger_ok = bool(be_reached)

    if realtime:
        if realtime_with_sec:
            print("  [OK] Secondary series at startup: has_secondary_series=True")
        elif realtime_no_sec:
            print("  [WARN] Secondary series at startup: has_secondary_series=False (using fallback)")
        else:
            print("  [INFO] REALTIME_STATE_REACHED seen (check has_secondary_series in data)")
    if secondary_ok:
        print("  [OK] BE path when in position: Active (BE_PATH_ACTIVE or BE_EVALUATION_TICK)")
    else:
        print("  [??] BE path when in position: No evidence (need to be in position, or old build)")

    if be_eval_ok:
        print("  [OK] BE evaluation: Running")
    else:
        print("  [??] BE evaluation: No evidence")

    if be_trigger_ok:
        print("  [OK] BE triggers: Firing")
    else:
        print("  [??] BE triggers: None detected")

    print("\n" + "=" * 85)

if __name__ == "__main__":
    main()
