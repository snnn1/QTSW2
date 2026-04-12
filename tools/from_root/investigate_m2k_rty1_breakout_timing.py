#!/usr/bin/env python3
"""
Investigate: M2K had orders placed when breakout had already happened for RTY1

This script analyzes robot logs to understand the timeline of:
1. RTY1 range lock and breakout levels
2. When M2K orders were placed
3. Whether the breakout occurred before order placement (bar-close delay / chasing)

RTY1 uses M2K as execution instrument. Both receive bars from the same underlying (Russell 2000).
"""
import json
from pathlib import Path
from datetime import datetime

def load_events(log_path: Path, stream_filter: str = None, event_types: set = None):
    """Load events from JSONL log file."""
    events = []
    if not log_path.exists():
        return events
    with open(log_path, 'r', encoding='utf-8-sig') as f:
        for line in f:
            if not line.strip():
                continue
            try:
                ev = json.loads(line.strip())
                if stream_filter:
                    stream = ev.get('stream') or ev.get('data', {}).get('stream_id') or ev.get('data', {}).get('stream', '')
                    if stream_filter.upper() not in str(stream).upper():
                        continue
                if event_types:
                    et = ev.get('event_type') or ev.get('event', '')
                    if et not in event_types:
                        continue
                events.append(ev)
            except json.JSONDecodeError:
                pass
    return events


def main():
    log_dir = Path("logs/robot")
    m2k_log = log_dir / "robot_M2K.jsonl"
    rty_log = log_dir / "robot_RTY.jsonl"

    # Key event types for RTY1/M2K breakout timing (event field in logs)
    key_events = {
        "RANGE_LOCKED", "STREAM_STATE_TRANSITION",
        "STOP_BRACKETS_SUBMITTED", "STOP_BRACKETS_SUBMIT_ENTERED", "STOP_BRACKETS_SUBMIT_ATTEMPT",
        "INTENT_REGISTERED", "EXECUTION_FILLED", "ORDER_SUBMIT_SUCCESS", "ORDER_SUBMITTED",
        "BREAKOUT_LEVELS_COMPUTED", "RANGE_LOCK_VALIDATION_PASSED",
        "TRADE_COMPLETED", "PROTECTIVE_ORDERS_SUBMITTED",
    }

    print("=" * 90)
    print("M2K / RTY1 BREAKOUT TIMING INVESTIGATION")
    print("=" * 90)
    print()
    print("Hypothesis: M2K orders placed when RTY1 breakout had already occurred.")
    print("Possible causes:")
    print("  1. Bar-close delay: Lock at 09:01 when 09:00 bar arrives; breakout was at 09:00:xx")
    print("  2. RTY vs M2K bar timing: RTY bars arrive before M2K; we lock on RTY, place M2K orders late")
    print("  3. Stop fills at market: Price already past breakout -> stop fills at worse price")
    print()

    # Load from M2K log (primary - execution instrument)
    all_m2k = []
    for p in [m2k_log]:
        if p.exists():
            with open(p, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        all_m2k.append(json.loads(line.strip()))
                    except:
                        pass

    # Filter to RTY1 stream events (stream can be in event or in data.oco_group, data.stream, etc.)
    rty1_events = []
    for ev in all_m2k:
        stream = ev.get('stream') or ev.get('data', {}).get('stream_id') or ev.get('data', {}).get('stream', '')
        oco = ev.get('data', {}).get('oco_group', '')
        if 'RTY1' in str(stream).upper() or 'RTY1' in str(oco):
            rty1_events.append(ev)

    # Also check RTY log (if RTY chart exists - bars from RTY could route to RTY1 too)
    rty_events = []
    if rty_log.exists():
        with open(rty_log, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    ev = json.loads(line.strip())
                    stream = ev.get('stream') or ev.get('data', {}).get('stream_id') or ev.get('data', {}).get('stream', '')
                    oco = ev.get('data', {}).get('oco_group', '')
                    if 'RTY1' in str(stream).upper() or 'RTY1' in str(oco):
                        rty_events.append(ev)
                except:
                    pass

    def get_ts(e):
        return e.get('ts_utc') or e.get('timestamp_utc') or e.get('data', {}).get('timestamp_utc', '') or ''

    combined = []
    for e in rty1_events:
        e['_source'] = 'M2K'
        combined.append(e)
    for e in rty_events:
        e['_source'] = 'RTY'
        combined.append(e)

    # Load RANGE_LOCKED from ranges file (has lock timing - critical for breakout delay analysis)
    dates_in_combined = {get_ts(e)[:10] for e in combined if get_ts(e) and 'T' in get_ts(e)}
    for d in dates_in_combined:
        ranges_file = log_dir / f"ranges_{d}.jsonl"
        if ranges_file.exists():
            with open(ranges_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        ev = json.loads(line.strip())
                        if ev.get('stream_id') == 'RTY1' and ev.get('event_type') == 'RANGE_LOCKED':
                            ev['_source'] = 'ranges'
                            ev['ts_utc'] = ev.get('locked_at_utc', '')
                            ev['event'] = 'RANGE_LOCKED'
                            ev['data'] = {k: v for k, v in ev.items() if k not in ('event', 'ts_utc', '_source')}
                            combined.append(ev)
                    except:
                        pass

    combined.sort(key=lambda x: get_ts(x))

    if not combined:
        print("No RTY1 events found in M2K or RTY logs.")
        print("Check:")
        print("  - logs/robot/robot_M2K.jsonl")
        print("  - logs/robot/robot_RTY.jsonl")
        print("  - That you are running M2K chart for RTY streams (execution_policy has M2K enabled)")
        return

    # Find most recent day with RTY1 activity
    dates = set()
    for e in combined:
        ts = get_ts(e)
        if ts and 'T' in ts:
            dates.add(ts[:10])
    recent_date = max(dates) if dates else None

    if recent_date:
        combined = [e for e in combined if get_ts(e).startswith(recent_date)]
        print(f"Focusing on {recent_date} ({len(combined)} RTY1 events)")
        print()

    # Show RANGE_LOCKED timing from ranges file (critical for "breakout already happened" analysis)
    ranges_file = log_dir / f"ranges_{recent_date}.jsonl" if recent_date else None
    if ranges_file and ranges_file.exists():
        with open(ranges_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    r = json.loads(line.strip())
                    if r.get('stream_id') == 'RTY1':
                        slot_end = r.get('range_end_time_chicago', '')[:19] if r.get('range_end_time_chicago') else 'N/A'
                        locked_at = str(r.get('locked_at_chicago', ''))[:19] if r.get('locked_at_chicago') else 'N/A'
                        print("RTY1 RANGE_LOCKED (from ranges file):")
                        print(f"  Slot end (range_end): {slot_end} CT")
                        print(f"  Locked at:           {locked_at} CT")
                        print(f"  Breakout long/short: {r.get('breakout_long')} / {r.get('breakout_short')}")
                        print(f"  Freeze close:        {r.get('freeze_close')}")
                        if slot_end != 'N/A' and locked_at != 'N/A':
                            try:
                                from datetime import datetime
                                se = datetime.fromisoformat(slot_end.replace('Z', '+00:00'))
                                la = datetime.fromisoformat(locked_at.replace('Z', '+00:00'))
                                delay_mins = (la - se).total_seconds() / 60
                                print(f"  *** LOCK DELAY: {delay_mins:.1f} minutes after slot ***")
                            except Exception:
                                pass
                        print()
                        break
                except:
                    pass

    # Print timeline of key events
    print("RTY1 TIMELINE (key events)")
    print("-" * 90)
    for e in combined[-80:]:  # Last 80 events
        et = e.get('event_type') or e.get('event', '')
        ts = get_ts(e)[:19] if get_ts(e) else ''
        src = e.get('_source', '')
        data = e.get('data', {})

        # Highlight critical events (event can be in 'event' or 'event_type' field)
        if et in key_events or 'RANGE_LOCKED' in str(et) or 'STOP_BRACKETS' in str(et) or 'INTENT' in str(et) or 'FILL' in str(et) or 'TRADE_COMPLETED' in str(et):
            print(f"[{ts}] {et} (source: {src})")
            if 'stream' in data or 'stream_id' in data:
                print(f"       stream: {data.get('stream_id') or data.get('stream')}")
            if 'range_high' in data or 'range_low' in data:
                print(f"       range: {data.get('range_high')} - {data.get('range_low')}")
            brk_long = data.get('brk_long') or data.get('breakout_long')
            brk_short = data.get('brk_short') or data.get('breakout_short')
            if brk_long or brk_short:
                print(f"       breakout: long={brk_long} short={brk_short}")
            if 'range_end_time_chicago' in data or 'locked_at_chicago' in data:
                print(f"       slot_end: {data.get('range_end_time_chicago', '')[:19] if data.get('range_end_time_chicago') else ''} | locked_at: {str(data.get('locked_at_chicago', ''))[:19]}")
            if 'freeze_close' in data:
                print(f"       freeze_close: {data.get('freeze_close')}")
            if 'entry_price' in data or 'fill_price' in data:
                print(f"       price: {data.get('entry_price') or data.get('fill_price')}")
            if 'direction' in data:
                print(f"       direction: {data.get('direction')}")
            print()

    # Summary
    print()
    print("ANALYSIS CHECKLIST")
    print("-" * 90)
    print("1. RANGE_LOCKED timestamp vs first bar after slot:")
    print("   - Lock happens when bar with timestamp >= slot_time arrives (typically 1 min after slot)")
    print("   - RTY1 slot = 09:00 CT -> lock typically at ~09:01 when 09:00 bar closes")
    print()
    print("2. If breakout occurred BEFORE lock:")
    print("   - Freeze_close is last bar BEFORE slot (08:59). If 08:59 bar already shows breakout,")
    print("     we'd have immediate fill at lock. Check freeze_close vs brk_long/brk_short.")
    print()
    print("3. Bar source (M2K vs RTY):")
    print("   - If both M2K and RTY charts run, RTY1 gets bars from both.")
    print("   - Check _source in events: RTY bars vs M2K bars - timing may differ.")
    print()
    print("4. Stop order fill price:")
    print("   - If price already past breakout when stop placed, stop fills at market (worse fill).")
    print("   - Compare fill_price to brk_long/brk_short.")
    print()
    print("Run with specific date: python investigate_m2k_rty1_breakout_timing.py")
    print("Or inspect logs for a specific incident date.")


if __name__ == "__main__":
    main()
