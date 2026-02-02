#!/usr/bin/env python3
"""
Audit Watchdog Feed Generation

Checks:
- Feed file contents
- Event types present
- Filtering is correct
- Payload extraction works
"""
import json
import sys
import re
from pathlib import Path
from collections import Counter
from datetime import datetime, timezone

# Add project root to path
project_root = Path(__file__).parent
sys.path.insert(0, str(project_root))

from modules.watchdog.config import FRONTEND_FEED_FILE, LIVE_CRITICAL_EVENT_TYPES

print("="*80)
print("WATCHDOG FEED GENERATION AUDIT")
print("="*80)

# 1. Check feed file
print("\n1. FEED FILE CHECK")
print("-" * 80)

if not FRONTEND_FEED_FILE.exists():
    print(f"[ERROR] Feed file not found: {FRONTEND_FEED_FILE}")
    sys.exit(1)

print(f"✅ Feed file exists: {FRONTEND_FEED_FILE}")
feed_size_mb = FRONTEND_FEED_FILE.stat().st_size / (1024 * 1024)
print(f"   Size: {feed_size_mb:.2f} MB")

# Read all events
with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8') as f:
    lines = f.readlines()

total_events = len(lines)
print(f"   Total events: {total_events:,}")

if not lines:
    print("❌ Feed file is empty!")
    sys.exit(1)

# 2. Check event types
print("\n2. EVENT TYPE ANALYSIS")
print("-" * 80)

all_events = []
for line in lines:
    if line.strip():
        try:
            event = json.loads(line.strip())
            all_events.append(event)
        except json.JSONDecodeError:
            continue

event_types = Counter(e.get('event_type', 'UNKNOWN') for e in all_events)
print(f"Total unique event types: {len(event_types)}")
print(f"\nTop 20 event types:")
for event_type, count in event_types.most_common(20):
    pct = (count / len(all_events)) * 100
    print(f"  {event_type}: {count:,} ({pct:.1f}%)")

# Check if critical events are present
print("\n3. CRITICAL EVENT TYPES CHECK")
print("-" * 80)

critical_events_found = {}
for event_type in LIVE_CRITICAL_EVENT_TYPES:
    count = event_types.get(event_type, 0)
    critical_events_found[event_type] = count
    if count == 0:
        print(f"  [WARN] {event_type}: NOT FOUND")
    else:
        print(f"  [OK] {event_type}: {count:,}")

# Check for BAR_RECEIVED_NO_STREAMS and BAR_ACCEPTED
bar_events = [e for e in all_events if e.get('event_type') in ('BAR_RECEIVED_NO_STREAMS', 'BAR_ACCEPTED')]
print(f"\nBar events: {len(bar_events)}")

# 4. Check payload extraction
print("\n4. PAYLOAD EXTRACTION TEST")
print("-" * 80)

bar_events_with_payload = [e for e in bar_events if 'payload' in e.get('data', {})]
print(f"Bar events with payload field: {len(bar_events_with_payload)}")

if bar_events_with_payload:
    # Test extraction
    sample = bar_events_with_payload[0]
    payload = sample.get('data', {}).get('payload', '')
    
    print(f"\nSample bar event:")
    print(f"  Event type: {sample.get('event_type')}")
    print(f"  Payload: {payload[:150]}...")
    
    # Test extraction logic
    execution_instrument_full_name = None
    instrument = None
    
    if isinstance(payload, str):
        # Extract execution_instrument_full_name
        match = re.search(r'execution_instrument_full_name\s*=\s*([^,}]+)', payload)
        if match:
            execution_instrument_full_name = match.group(1).strip()
        
        # Extract instrument
        inst_match = re.search(r'instrument\s*=\s*([^,}]+)', payload)
        if inst_match:
            instrument = inst_match.group(1).strip()
    
    print(f"\n  Extracted:")
    print(f"    execution_instrument_full_name: {execution_instrument_full_name}")
    print(f"    instrument: {instrument}")
    
    # Check if top-level fields exist
    top_level_exec = sample.get('execution_instrument_full_name')
    top_level_inst = sample.get('instrument')
    
    print(f"\n  Top-level fields:")
    print(f"    execution_instrument_full_name: {top_level_exec}")
    print(f"    instrument: {top_level_inst}")
    
    if not top_level_exec and not top_level_inst:
        print(f"  ⚠️  WARNING: Top-level fields missing - extraction may have failed")
    else:
        print(f"  ✅ Top-level fields present")

# Check for events missing instrument
print("\n5. MISSING INSTRUMENT CHECK")
print("-" * 80)

missing_instrument = []
for event in bar_events[:100]:  # Check first 100 bar events
    exec_inst = event.get('execution_instrument_full_name')
    inst = event.get('instrument')
    data_exec_inst = event.get('data', {}).get('execution_instrument_full_name')
    data_inst = event.get('data', {}).get('instrument')
    
    if not (exec_inst or inst or data_exec_inst or data_inst):
        missing_instrument.append(event)

if missing_instrument:
    print(f"⚠️  Found {len(missing_instrument)} bar events missing instrument")
    print(f"   Sample event seq: {missing_instrument[0].get('event_seq')}")
else:
    print(f"[OK] All bar events have instrument data")

# 6. Check filtering
print("\n6. FILTERING CHECK")
print("-" * 80)

# Check if STREAM_STATE_TRANSITION initialization events are filtered
init_transitions = [e for e in all_events if e.get('event_type') == 'STREAM_STATE_TRANSITION']
if init_transitions:
    init_with_unknown = []
    for event in init_transitions:
        data = event.get('data', {})
        old_state = data.get('old_state', '')
        if old_state == 'UNKNOWN' or old_state == '':
            init_with_unknown.append(event)
    
    print(f"STREAM_STATE_TRANSITION events: {len(init_transitions)}")
    print(f"  With UNKNOWN old_state: {len(init_with_unknown)}")
    
    if len(init_with_unknown) > len(init_transitions) * 0.5:
        print(f"  [WARN] WARNING: Many initialization transitions present - filtering may not be working")
    else:
        print(f"  [OK] Most initialization transitions filtered out")

print("\n" + "="*80)
print("AUDIT COMPLETE")
print("="*80)
