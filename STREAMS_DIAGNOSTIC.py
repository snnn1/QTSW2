#!/usr/bin/env python3
"""
Deep diagnostic script to check why streams aren't showing.
Checks feed file, state manager, and filtering logic.
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timezone
import pytz

# Add modules to path
sys.path.insert(0, str(Path(__file__).parent))

from modules.watchdog.config import FRONTEND_FEED_FILE, ROBOT_LOGS_DIR
from modules.watchdog.state_manager import WatchdogStateManager
from modules.watchdog.timetable_poller import TimetablePoller

CHICAGO_TZ = pytz.timezone("America/Chicago")

def main():
    print("=" * 80)
    print("STREAMS NOT SHOWING - DEEP DIAGNOSTIC")
    print("=" * 80)
    
    # 1. Check feed file
    print("\n1. CHECKING FEED FILE")
    print("-" * 80)
    if not FRONTEND_FEED_FILE.exists():
        print(f"[ERROR] Feed file not found: {FRONTEND_FEED_FILE}")
        return
    
    print(f"[OK] Feed file exists: {FRONTEND_FEED_FILE}")
    
    # Count STREAM_STATE_TRANSITION events
    stream_events = []
    with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                event = json.loads(line.strip())
                if event.get("event_type") == "STREAM_STATE_TRANSITION":
                    stream_events.append(event)
            except:
                pass
    
    print(f"   Found {len(stream_events)} STREAM_STATE_TRANSITION events")
    
    if stream_events:
        latest = stream_events[-1]
        print(f"\n   Latest STREAM_STATE_TRANSITION event:")
        print(f"     run_id: {latest.get('run_id')}")
        print(f"     event_seq: {latest.get('event_seq')}")
        print(f"     trading_date: {latest.get('trading_date')}")
        print(f"     stream: {latest.get('stream')}")
        print(f"     timestamp_utc: {latest.get('timestamp_utc')}")
        data = latest.get('data', {})
        print(f"     data.new_state: {data.get('new_state')}")
        print(f"     data.previous_state: {data.get('previous_state')}")
        
        # Group by trading_date
        by_date = {}
        for e in stream_events:
            td = e.get('trading_date') or 'null'
            by_date[td] = by_date.get(td, 0) + 1
        print(f"\n   Events by trading_date: {by_date}")
    
    # 2. Check timetable
    print("\n2. CHECKING TIMETABLE")
    print("-" * 80)
    poller = TimetablePoller()
    trading_date, enabled_streams, timetable_hash = poller.poll()
    print(f"   Current trading_date (from timetable): {trading_date}")
    print(f"   Enabled streams: {len(enabled_streams) if enabled_streams else 0} streams" if enabled_streams else "   Enabled streams: None (timetable unavailable)")
    print(f"   Timetable hash: {timetable_hash[:8] if timetable_hash else 'None'}")
    
    # 3. Check state manager (simulate)
    print("\n3. CHECKING STATE MANAGER")
    print("-" * 80)
    state_manager = WatchdogStateManager()
    current_td = state_manager.get_trading_date()
    print(f"   State manager trading_date: {current_td}")
    print(f"   State manager enabled_streams: {state_manager.get_enabled_streams()}")
    
    # Process recent events to populate state
    print("\n4. PROCESSING RECENT EVENTS")
    print("-" * 80)
    from modules.watchdog.event_processor import EventProcessor
    processor = EventProcessor(state_manager)
    
    recent_count = 0
    processed_count = 0
    with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
        # Read last 1000 lines
        lines = list(f)
        for line in lines[-1000:]:
            try:
                event = json.loads(line.strip())
                if event.get("event_type") == "STREAM_STATE_TRANSITION":
                    recent_count += 1
                    processor.process_event(event)
                    processed_count += 1
            except Exception as e:
                pass
    
    print(f"   Processed {processed_count} recent STREAM_STATE_TRANSITION events")
    
    # Check stream states
    stream_states_dict = getattr(state_manager, '_stream_states', {})
    print(f"\n   Streams in state manager: {len(stream_states_dict)}")
    for (td, stream), info in list(stream_states_dict.items())[:10]:
        print(f"     {stream} ({td}): state={info.state}")
    
    # 4. Simulate get_stream_states filtering
    print("\n5. SIMULATING get_stream_states() FILTERING")
    print("-" * 80)
    current_trading_date = state_manager.get_trading_date() or trading_date
    enabled_streams_set = state_manager.get_enabled_streams()
    
    print(f"   Filtering by trading_date: {current_trading_date}")
    print(f"   Filtering by enabled_streams: {len(enabled_streams_set) if enabled_streams_set else 'None (show all)'}")
    
    filtered_by_date = 0
    filtered_by_enabled = 0
    passed = 0
    
    for (td, stream), info in stream_states_dict.items():
        if td != current_trading_date:
            filtered_by_date += 1
            continue
        if enabled_streams_set is not None:
            if stream not in enabled_streams_set:
                filtered_by_enabled += 1
                continue
        passed += 1
        print(f"     [PASS] {stream} ({td}): state={info.state}")
    
    print(f"\n   Summary:")
    print(f"     Total streams: {len(stream_states_dict)}")
    print(f"     Filtered by trading_date: {filtered_by_date}")
    print(f"     Filtered by enabled_streams: {filtered_by_enabled}")
    print(f"     Passed filters: {passed}")
    
    print("\n" + "=" * 80)
    print("DIAGNOSTIC COMPLETE")
    print("=" * 80)

if __name__ == "__main__":
    main()
