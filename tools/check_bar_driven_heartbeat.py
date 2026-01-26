"""Check bar-driven heartbeat activity"""
import json
from pathlib import Path
from datetime import datetime, timezone

def parse_timestamp(ts_str):
    try:
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def main():
    log_file = Path("logs/robot/robot_ENGINE.jsonl")
    
    events = []
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        print(f"Error reading log file: {e}")
        return
    
    # Get recent events (last 500)
    recent = events[-500:] if len(events) > 500 else events
    
    print("="*80)
    print("BAR-DRIVEN HEARTBEAT CHECK")
    print("="*80)
    
    # Find ENGINE_START
    starts = [e for e in recent if e.get('event') == 'ENGINE_START']
    if starts:
        latest_start = starts[-1]
        start_time = parse_timestamp(latest_start.get('ts_utc', ''))
        if start_time:
            now_utc = datetime.now(timezone.utc)
            seconds_ago = (now_utc - start_time.replace(tzinfo=timezone.utc)).total_seconds()
            print(f"\n[ENGINE_START]")
            print(f"  Time: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC')}")
            print(f"  Seconds ago: {seconds_ago:.1f}")
    
    # Check for bar events
    bar_accepted = [e for e in recent if e.get('event') == 'BAR_ACCEPTED']
    bar_delivery = [e for e in recent if e.get('event') == 'BAR_DELIVERY_TO_STREAM']
    bar_rejected = [e for e in recent if e.get('event') in ['BAR_DATE_MISMATCH', 'BAR_RECEIVED_BEFORE_DATE_LOCKED']]
    
    print(f"\n[BAR ACTIVITY]")
    print(f"  BAR_ACCEPTED: {len(bar_accepted)}")
    print(f"  BAR_DELIVERY_TO_STREAM: {len(bar_delivery)}")
    print(f"  BAR_REJECTED: {len(bar_rejected)}")
    
    if bar_accepted:
        print(f"\n  Recent BAR_ACCEPTED events:")
        for e in bar_accepted[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            payload = e.get('data', {}).get('payload', {})
            instrument = payload.get('instrument', 'N/A')
            bar_time = payload.get('bar_timestamp_utc', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {instrument} | bar_time={bar_time[:19] if isinstance(bar_time, str) else 'N/A'}")
    
    # Check for ENGINE_TICK_HEARTBEAT
    heartbeats = [e for e in recent if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    
    print(f"\n[ENGINE_TICK_HEARTBEAT]")
    print(f"  Total: {len(heartbeats)}")
    
    if heartbeats:
        print(f"\n  Recent heartbeats:")
        for e in heartbeats[-5:]:
            ts = parse_timestamp(e.get('ts_utc', ''))
            payload = e.get('data', {}).get('payload', {})
            instrument = payload.get('instrument', 'N/A')
            bar_time_utc = payload.get('bar_time_utc', 'N/A')
            bars_since = payload.get('bars_since_last_heartbeat', 'N/A')
            note = payload.get('note', 'N/A')
            print(f"    {ts.strftime('%H:%M:%S UTC') if ts else 'N/A':19} | {instrument} | bar_time={bar_time_utc[:19] if isinstance(bar_time_utc, str) else 'N/A'} | bars_since={bars_since} | {note}")
    else:
        print(f"\n  [WARNING] No ENGINE_TICK_HEARTBEAT events found!")
        if bar_accepted:
            print(f"    But {len(bar_accepted)} bars were accepted - heartbeat should have fired")
            print(f"    Possible issues:")
            print(f"      1. Heartbeat rate limit (60s) hasn't elapsed yet")
            print(f"      2. Heartbeat emission code not executing")
            print(f"      3. Logs not being written")
        else:
            print(f"    No bars accepted yet - heartbeat will fire when bars arrive")
    
    # Check timing relationship
    if bar_accepted and heartbeats:
        print(f"\n[TIMING ANALYSIS]")
        last_bar = bar_accepted[-1]
        last_heartbeat = heartbeats[-1]
        bar_ts = parse_timestamp(last_bar.get('ts_utc', ''))
        hb_ts = parse_timestamp(last_heartbeat.get('ts_utc', ''))
        if bar_ts and hb_ts:
            time_diff = (hb_ts - bar_ts).total_seconds()
            print(f"  Last bar accepted: {bar_ts.strftime('%H:%M:%S UTC')}")
            print(f"  Last heartbeat: {hb_ts.strftime('%H:%M:%S UTC')}")
            print(f"  Time difference: {time_diff:.1f} seconds")
            if time_diff < 0:
                print(f"    [WARNING] Heartbeat is BEFORE bar acceptance - this shouldn't happen!")
            elif time_diff > 60:
                print(f"    [INFO] Heartbeat is rate-limited (60s interval)")

if __name__ == '__main__':
    main()
