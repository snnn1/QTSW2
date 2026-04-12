#!/usr/bin/env python3
"""Check recent trading events: fills, protective orders, BE triggers"""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

log_dir = Path("logs/robot")
if not log_dir.exists():
    print("Log directory not found")
    exit(1)

# Find all recent log files
log_files = list(log_dir.glob("robot_*.jsonl"))
log_files.sort(key=lambda p: p.stat().st_mtime, reverse=True)

print("="*80)
print("RECENT TRADING EVENTS ANALYSIS")
print("="*80)

# Track events by intent_id
intent_events = defaultdict(list)
all_recent_events = []

# Process recent log files (last 3 hours)
cutoff_time = datetime.now(timezone.utc).timestamp() - (3 * 3600)

for log_file in log_files[:10]:  # Check top 10 most recent files
    try:
        mtime = log_file.stat().st_mtime
        if mtime < cutoff_time:
            continue
            
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    ts_str = event.get('ts_utc', '')
                    if not ts_str:
                        continue
                    
                    # Parse timestamp
                    try:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts.timestamp() < cutoff_time:
                            continue
                    except:
                        continue
                    
                    event_type = event.get('event', '')
                    data = event.get('data', {})
                    intent_id = data.get('intent_id', '')
                    
                    # Track relevant events
                    if any(keyword in event_type for keyword in [
                        'EXECUTION_FILLED', 'ENTRY_FILL', 'PROTECTIVE', 
                        'BE_TRIGGER', 'INTENT_REGISTERED', 'STOP', 'TARGET',
                        'MODIFY', 'OnMarketData', 'ERROR', 'FAIL'
                    ]):
                        all_recent_events.append({
                            'ts': ts,
                            'file': log_file.name,
                            'event': event_type,
                            'intent_id': intent_id,
                            'data': data,
                            'raw': event
                        })
                        
                        if intent_id:
                            intent_events[intent_id].append({
                                'ts': ts,
                                'event': event_type,
                                'data': data
                            })
                except:
                    pass
    except Exception as e:
        print(f"Error reading {log_file.name}: {e}")

# Sort all events by time
all_recent_events.sort(key=lambda x: x['ts'])

print(f"\nFound {len(all_recent_events)} relevant events in last 3 hours")
print(f"Found {len(intent_events)} unique intents with events\n")

# Show most recent events
print("="*80)
print("MOST RECENT EVENTS (last 30)")
print("="*80)
for event in all_recent_events[-30:]:
    ts_str = event['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')
    intent_str = event['intent_id'][:8] if event['intent_id'] else 'N/A'
    print(f"{ts_str} | {event['file']:20} | {event['event']:35} | Intent: {intent_str}")

# Analyze fills and protective orders
print("\n" + "="*80)
print("FILL ANALYSIS")
print("="*80)

fills = [e for e in all_recent_events if 'FILL' in e['event']]
print(f"\nFound {len(fills)} fill events:")

for fill in fills:
    intent_id = fill['intent_id']
    ts_str = fill['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')
    fill_price = fill['data'].get('fill_price', 'N/A')
    fill_qty = fill['data'].get('fill_quantity', 'N/A')
    
    print(f"\n  Fill at {ts_str}:")
    print(f"    Intent: {intent_id[:8] if intent_id else 'N/A'}")
    print(f"    Price: {fill_price}, Qty: {fill_qty}")
    print(f"    File: {fill['file']}")
    
    # Check for protective orders after this fill
    if intent_id:
        intent_evts = intent_events[intent_id]
        protective = [e for e in intent_evts if 'PROTECTIVE' in e['event'] and e['ts'] > fill['ts']]
        stops = [e for e in intent_evts if 'STOP' in e['event'] and 'ENTRY' not in e['event'] and e['ts'] > fill['ts']]
        targets = [e for e in intent_evts if 'TARGET' in e['event'] and e['ts'] > fill['ts']]
        be_triggers = [e for e in intent_evts if 'BE_TRIGGER' in e['event'] and e['ts'] > fill['ts']]
        
        print(f"    Protective orders after fill: {len(protective)}")
        print(f"    Stop orders after fill: {len(stops)}")
        print(f"    Target orders after fill: {len(targets)}")
        print(f"    BE triggers after fill: {len(be_triggers)}")
        
        if protective:
            for p in protective:
                print(f"      - {p['ts'].strftime('%H:%M:%S')} | {p['event']}")
        if stops:
            for s in stops[:3]:
                print(f"      - {s['ts'].strftime('%H:%M:%S')} | {s['event']}")
        if targets:
            for t in targets[:3]:
                print(f"      - {t['ts'].strftime('%H:%M:%S')} | {t['event']}")
        if be_triggers:
            for be in be_triggers[:3]:
                print(f"      - {be['ts'].strftime('%H:%M:%S')} | {be['event']}")

# Check for BE trigger events
print("\n" + "="*80)
print("BREAK-EVEN TRIGGER ANALYSIS")
print("="*80)

be_events = [e for e in all_recent_events if 'BE_TRIGGER' in e['event']]
print(f"\nFound {len(be_events)} BE trigger events:")

for be in be_events:
    ts_str = be['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')
    intent_id = be['intent_id'][:8] if be['intent_id'] else 'N/A'
    print(f"  {ts_str} | {be['file']:20} | Intent: {intent_id} | {be['event']}")

# Check for OnMarketData events (tick-based BE detection)
print("\n" + "="*80)
print("OnMarketData EVENTS (Tick-based processing)")
print("="*80)

market_data_events = [e for e in all_recent_events if 'OnMarketData' in e['event'] or 'MARKET_DATA' in e['event']]
print(f"\nFound {len(market_data_events)} market data events")

if market_data_events:
    for md in market_data_events[-10:]:
        ts_str = md['ts'].strftime('%Y-%m-%d %H:%M:%S UTC')
        print(f"  {ts_str} | {md['file']:20} | {md['event']}")

print("\n" + "="*80)
