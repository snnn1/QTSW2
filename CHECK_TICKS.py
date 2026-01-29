#!/usr/bin/env python3
"""
Check why ticks aren't being received in watchdog
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timezone

sys.path.insert(0, str(Path(__file__).parent))

from modules.watchdog.config import FRONTEND_FEED_FILE
from modules.watchdog.state_manager import WatchdogStateManager
from modules.watchdog.event_processor import EventProcessor

def main():
    print("=" * 80)
    print("CHECKING WHY TICKS AREN'T BEING RECEIVED")
    print("=" * 80)
    
    # 1. Check feed file for ticks
    print("\n1. CHECKING FEED FILE FOR TICKS")
    print("-" * 80)
    if not FRONTEND_FEED_FILE.exists():
        print(f"[ERROR] Feed file not found: {FRONTEND_FEED_FILE}")
        return
    
    ticks = []
    with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
        for line in f:
            try:
                event = json.loads(line.strip())
                if event.get("event_type") == "ENGINE_TICK_CALLSITE":
                    ticks.append(event)
            except:
                pass
    
    print(f"   Found {len(ticks)} ENGINE_TICK_CALLSITE events in feed")
    
    if ticks:
        latest = ticks[-1]
        latest_time = datetime.fromisoformat(latest['timestamp_utc'].replace('Z', '+00:00'))
        now = datetime.now(timezone.utc)
        age = (now - latest_time).total_seconds()
        print(f"   Latest tick: {latest['timestamp_utc']}")
        print(f"   Age: {age:.1f} seconds ({age/60:.1f} minutes)")
        print(f"   Run ID: {latest['run_id'][:8]}...")
    
    # 2. Check state manager
    print("\n2. CHECKING STATE MANAGER")
    print("-" * 80)
    state_manager = WatchdogStateManager()
    last_tick = state_manager._last_engine_tick_utc
    print(f"   Last engine tick UTC: {last_tick.isoformat() if last_tick else 'None'}")
    
    if last_tick:
        now = datetime.now(timezone.utc)
        age = (now - last_tick).total_seconds()
        print(f"   Tick age: {age:.1f} seconds ({age/60:.1f} minutes)")
        from modules.watchdog.config import ENGINE_TICK_STALL_THRESHOLD_SECONDS
        print(f"   Threshold: {ENGINE_TICK_STALL_THRESHOLD_SECONDS} seconds")
        print(f"   Engine alive: {age < ENGINE_TICK_STALL_THRESHOLD_SECONDS}")
    
    # 3. Process recent ticks
    print("\n3. PROCESSING RECENT TICKS")
    print("-" * 80)
    processor = EventProcessor(state_manager)
    
    processed = 0
    with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
        lines = list(f)
        for line in lines[-100:]:  # Last 100 lines
            try:
                event = json.loads(line.strip())
                if event.get("event_type") == "ENGINE_TICK_CALLSITE":
                    processor.process_event(event)
                    processed += 1
            except Exception as e:
                print(f"   Error processing: {e}")
    
    print(f"   Processed {processed} recent ticks")
    
    # Check state manager again
    last_tick_after = state_manager._last_engine_tick_utc
    print(f"   Last engine tick UTC after processing: {last_tick_after.isoformat() if last_tick_after else 'None'}")
    
    if last_tick_after:
        now = datetime.now(timezone.utc)
        age = (now - last_tick_after).total_seconds()
        print(f"   Tick age after processing: {age:.1f} seconds")
    
    print("\n" + "=" * 80)
    print("DIAGNOSTIC COMPLETE")
    print("=" * 80)

if __name__ == "__main__":
    main()
