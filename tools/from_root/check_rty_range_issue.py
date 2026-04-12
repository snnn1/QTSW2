#!/usr/bin/env python3
"""Check RTY range lock and order submission issue"""
import json
from pathlib import Path
from datetime import datetime, timezone

log_file = Path("logs/robot/robot_RTY.jsonl")
if not log_file.exists():
    print("RTY log file not found")
    exit(1)

print("="*80)
print("RTY RANGE LOCK AND ORDER SUBMISSION ANALYSIS")
print("="*80)

events = []
with open(log_file, 'r', encoding='utf-8-sig') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

# Filter recent events (last 6 hours)
cutoff = datetime.now(timezone.utc).timestamp() - (6 * 3600)
recent_events = [e for e in events if e.get('ts_utc') and 
                 datetime.fromisoformat(e['ts_utc'].replace('Z', '+00:00')).timestamp() > cutoff]

print(f"\nFound {len(recent_events)} events in last 6 hours")

# Find RANGE_LOCKED events
range_locked = [e for e in recent_events if e.get('event') == 'RANGE_LOCKED']
print(f"\nFound {len(range_locked)} RANGE_LOCKED events:")

for rl in range_locked:
    ts = rl.get('ts_utc', '')[:19]
    data = rl.get('data', {})
    stream_id = data.get('stream_id', 'N/A')
    print(f"\n  {ts} | Stream: {stream_id}")
    
    # Handle extra_data - could be dict or string
    extra_data = data.get('extra_data', {})
    if isinstance(extra_data, dict):
        print(f"    Range High: {extra_data.get('range_high', 'N/A')}")
        print(f"    Range Low: {extra_data.get('range_low', 'N/A')}")
    else:
        print(f"    Extra Data: {str(extra_data)[:100]}")
    
    # Check for events after this RANGE_LOCKED
    rl_time = datetime.fromisoformat(rl['ts_utc'].replace('Z', '+00:00'))
    after_events = [e for e in recent_events 
                    if datetime.fromisoformat(e['ts_utc'].replace('Z', '+00:00')) > rl_time]
    
    # Look for STOP_BRACKETS_SUBMIT_ATTEMPT
    bracket_attempts = [e for e in after_events[:50] if 'STOP_BRACKETS_SUBMIT' in e.get('event', '')]
    if bracket_attempts:
        print(f"    [OK] STOP_BRACKETS_SUBMIT_ATTEMPT found")
        for ba in bracket_attempts:
            print(f"      {ba.get('ts_utc', '')[:19]} | {ba.get('event', '')}")
    else:
        print(f"    [MISSING] NO STOP_BRACKETS_SUBMIT_ATTEMPT found")
        
        # Check for conditions that might prevent submission
        entry_detected = [e for e in after_events[:20] if 'ENTRY_DETECTED' in e.get('event', '')]
        if entry_detected:
            print(f"    [WARN] ENTRY_DETECTED found - brackets skipped")
            
        # Check for market close
        market_close_check = [e for e in after_events[:20] if 'MARKET_CLOSE' in e.get('event', '') or 'DONE' in e.get('event', '')]
        if market_close_check:
            print(f"    [WARN] Market close/DONE state found")
            
        # Check for journal committed
        journal_committed = [e for e in after_events[:20] if 'COMMITTED' in e.get('event', '')]
        if journal_committed:
            print(f"    [WARN] Journal COMMITTED found")
            
        # Check for range invalidated
        range_invalidated = [e for e in after_events[:20] if 'RANGE_INVALIDATED' in e.get('event', '')]
        if range_invalidated:
            print(f"    [WARN] RANGE_INVALIDATED found")
            
        # Check for execution adapter errors
        adapter_errors = [e for e in after_events[:20] if 'EXECUTION_ERROR' in e.get('event', '') or 'EXECUTION_ADAPTER' in e.get('event', '')]
        if adapter_errors:
            print(f"    [WARN] Execution adapter errors found:")
            for ae in adapter_errors[:3]:
                print(f"      {ae.get('ts_utc', '')[:19]} | {ae.get('event', '')} | {ae.get('data', {}).get('error', 'N/A')[:80]}")

# Check for BREAKOUT_LEVELS_COMPUTED
breakout_levels = [e for e in recent_events if e.get('event') == 'BREAKOUT_LEVELS_COMPUTED']
print(f"\n\nFound {len(breakout_levels)} BREAKOUT_LEVELS_COMPUTED events:")
for bl in breakout_levels[-3:]:
    ts = bl.get('ts_utc', '')[:19]
    data = bl.get('data', {})
    print(f"  {ts} | Long: {data.get('brk_long_rounded', 'N/A')} | Short: {data.get('brk_short_rounded', 'N/A')}")

# Check for any order submission events
order_events = [e for e in recent_events if any(x in e.get('event', '') for x in 
    ['ORDER_SUBMIT', 'ENTRY_SUBMIT', 'STOP_BRACKETS', 'ORDER_CREATED'])]
print(f"\n\nFound {len(order_events)} order-related events:")
for oe in order_events[-10:]:
    ts = oe.get('ts_utc', '')[:19]
    event = oe.get('event', 'N/A')
    print(f"  {ts} | {event}")

print("\n" + "="*80)
