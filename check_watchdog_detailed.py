#!/usr/bin/env python3
"""
Detailed Watchdog Status Check
"""

import sys
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

qtsw2_root = Path(__file__).parent
sys.path.insert(0, str(qtsw2_root))

from modules.watchdog.state_manager import WatchdogStateManager
from modules.watchdog.event_processor import EventProcessor
from modules.watchdog.market_session import is_market_open

CHICAGO_TZ = pytz.timezone("America/Chicago")

def main():
    print("="*80)
    print("WATCHDOG DETAILED STATUS CHECK")
    print("="*80)
    
    # Initialize state manager and processor
    sm = WatchdogStateManager()
    ep = EventProcessor(sm)
    
    # Process recent events
    feed_file = qtsw2_root / "logs" / "robot" / "frontend_feed.jsonl"
    if feed_file.exists():
        with open(feed_file, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
            recent_lines = lines[-2000:] if len(lines) > 2000 else lines
            
            processed = 0
            for line in recent_lines:
                if line.strip():
                    try:
                        event = json.loads(line.strip())
                        ep.process_event(event)
                        processed += 1
                    except:
                        continue
            print(f"Processed {processed} events from feed")
    
    # Compute status
    market_open = is_market_open(datetime.now(CHICAGO_TZ))
    status = sm.compute_watchdog_status()
    
    print("\n" + "="*80)
    print("STATUS SUMMARY")
    print("="*80)
    print(f"Engine alive: {status.get('engine_alive')}")
    print(f"Engine activity state: {status.get('engine_activity_state')}")
    print(f"Recovery state: {status.get('recovery_state')}")
    print(f"Connection status: {status.get('connection_status')}")
    print(f"Market open: {market_open}")
    print(f"Bars expected count: {status.get('bars_expected_count', 0)}")
    print(f"Worst bar age: {status.get('worst_last_bar_age_seconds')}s")
    
    print("\n" + "="*80)
    print("DATA STALL DETECTED")
    print("="*80)
    data_stalls = status.get('data_stall_detected', {})
    if data_stalls:
        print(f"Found {len(data_stalls)} instruments with stalls:")
        for inst, info in data_stalls.items():
            print(f"  {inst}:")
            print(f"    stall_detected: {info.get('stall_detected')}")
            print(f"    market_open: {info.get('market_open')}")
            print(f"    last_bar_chicago: {info.get('last_bar_chicago')}")
    else:
        print("No data stalls detected")
    
    print("\n" + "="*80)
    print("STREAM STATES")
    print("="*80)
    stream_states = sm._stream_states
    print(f"Total streams: {len(stream_states)}")
    for (trading_date, stream), info in list(stream_states.items())[:10]:
        exec_inst = getattr(info, 'execution_instrument', None)
        print(f"  {stream} ({trading_date}): state={info.state}, execution_instrument={exec_inst}")
    
    print("\n" + "="*80)
    print("BAR TRACKING")
    print("="*80)
    bar_tracking = sm._last_bar_utc_by_execution_instrument
    print(f"Instruments with bars tracked: {len(bar_tracking)}")
    now = datetime.now(timezone.utc)
    for inst, last_bar_utc in list(bar_tracking.items())[:10]:
        age = (now - last_bar_utc).total_seconds()
        bars_expected = sm.bars_expected(inst, market_open)
        print(f"  {inst}: last_bar_age={age:.1f}s, bars_expected={bars_expected}")

if __name__ == "__main__":
    main()
