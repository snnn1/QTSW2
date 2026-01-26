"""Check Pattern 1 (Bar-Driven) implementation status"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

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
    
    # Get events since latest ENGINE_START
    starts = [e for e in events if e.get('event') == 'ENGINE_START']
    if not starts:
        print("No ENGINE_START found")
        return
    
    latest_start = starts[-1]
    start_time = parse_timestamp(latest_start.get('ts_utc', ''))
    if not start_time:
        print("Could not parse ENGINE_START time")
        return
    
    # Get events since start
    since_start = [e for e in events if parse_timestamp(e.get('ts_utc', '')) and parse_timestamp(e.get('ts_utc', '')).replace(tzinfo=timezone.utc) >= start_time.replace(tzinfo=timezone.utc)]
    
    print("="*80)
    print("PATTERN 1 (BAR-DRIVEN) STATUS CHECK")
    print("="*80)
    
    now_utc = datetime.now(timezone.utc)
    chicago_tz = pytz.timezone("America/Chicago")
    now_chicago = now_utc.astimezone(chicago_tz)
    
    print(f"\n[CURRENT TIME]")
    print(f"  UTC: {now_utc.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    print(f"  Chicago: {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
    
    print(f"\n[ENGINE START]")
    print(f"  Time: {start_time.strftime('%Y-%m-%d %H:%M:%S UTC')}")
    seconds_ago = (now_utc - start_time.replace(tzinfo=timezone.utc)).total_seconds()
    print(f"  Seconds ago: {seconds_ago:.1f}")
    
    # Check for bar-driven heartbeat
    heartbeats = [e for e in since_start if e.get('event') == 'ENGINE_TICK_HEARTBEAT']
    bar_accepted = [e for e in since_start if e.get('event') == 'BAR_ACCEPTED']
    bar_delivery = [e for e in since_start if e.get('event') == 'BAR_DELIVERY_TO_STREAM']
    
    print(f"\n[BAR-DRIVEN HEARTBEAT STATUS]")
    print(f"  ENGINE_TICK_HEARTBEAT events: {len(heartbeats)}")
    print(f"  BAR_ACCEPTED events: {len(bar_accepted)}")
    print(f"  BAR_DELIVERY_TO_STREAM events: {len(bar_delivery)}")
    
    if heartbeats:
        print(f"\n  [OK] Bar-driven heartbeats are working!")
        latest_hb = heartbeats[-1]
        hb_ts = parse_timestamp(latest_hb.get('ts_utc', ''))
        payload = latest_hb.get('data', {}).get('payload', {})
        instrument = payload.get('instrument', 'N/A')
        bar_time = payload.get('bar_time_utc', 'N/A')
        bars_since = payload.get('bars_since_last_heartbeat', 'N/A')
        note = payload.get('note', 'N/A')
        print(f"    Latest heartbeat: {hb_ts.strftime('%H:%M:%S UTC') if hb_ts else 'N/A'}")
        print(f"    Instrument: {instrument}")
        print(f"    Bar time: {bar_time[:19] if isinstance(bar_time, str) else 'N/A'}")
        print(f"    Bars since last: {bars_since}")
        print(f"    Note: {note}")
    elif bar_accepted:
        print(f"\n  [INFO] Bars accepted but no heartbeat yet")
        print(f"    This is normal - heartbeat is rate-limited to 60 seconds")
        print(f"    Heartbeat will fire on next bar after 60s interval")
    else:
        print(f"\n  [INFO] No bars accepted yet")
        print(f"    Heartbeat will fire when bars are accepted")
        print(f"    This is expected if:")
        print(f"      - Market is closed")
        print(f"      - Strategies just enabled (waiting for bars)")
        print(f"      - No market data flowing")
    
    # Check stream states
    stream_transitions = [e for e in since_start if e.get('event') == 'STREAM_STATE_TRANSITION']
    stream_armed = [e for e in since_start if e.get('event') == 'STREAM_ARMED']
    range_locked = [e for e in since_start if e.get('event') == 'RANGE_LOCKED']
    
    print(f"\n[STREAM STATUS]")
    print(f"  STREAM_STATE_TRANSITION: {len(stream_transitions)}")
    print(f"  STREAM_ARMED: {len(stream_armed)}")
    print(f"  RANGE_LOCKED: {len(range_locked)}")
    
    # Check for Tick() calls
    tick_heartbeat_audit = [e for e in since_start if e.get('event') == 'ENGINE_TICK_HEARTBEAT_AUDIT']
    print(f"\n[TICK() METHOD STATUS]")
    print(f"  ENGINE_TICK_HEARTBEAT_AUDIT (old timer-based): {len(tick_heartbeat_audit)}")
    if tick_heartbeat_audit:
        print(f"    [WARNING] Old timer-based heartbeat audit logs found!")
        print(f"    These should not exist in Pattern 1 implementation")
    else:
        print(f"    [OK] No timer-based heartbeat audit logs (expected)")
    
    # Summary
    print(f"\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    
    if len(heartbeats) > 0:
        print(f"\n[OK] Pattern 1 is working!")
        print(f"  Bar-driven heartbeats are being emitted")
    elif len(bar_accepted) > 0:
        print(f"\n[INFO] Bars are being accepted")
        print(f"  Heartbeat will fire when rate limit (60s) elapses")
    else:
        print(f"\n[INFO] Waiting for bars")
        print(f"  Engine is running but no bars received yet")
        print(f"  This is normal if:")
        print(f"    - Market is closed")
        print(f"    - Strategies just enabled")
        print(f"    - Waiting for market data")

if __name__ == '__main__':
    main()
