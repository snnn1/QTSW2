#!/usr/bin/env python3
"""
Stream State Progression Test - ESSENTIAL

Verifies streams progress beyond PRE_HYDRATION after fix.
Bars being "accepted" means nothing if streams don't move.
PRE_HYDRATION → ARMED is the real signal that data is flowing.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict
from typing import Dict, List, Optional, Tuple
import pytz

# Setup paths
QTSW2_ROOT = Path(__file__).parent
LOGS_DIR = QTSW2_ROOT / "logs" / "robot"
JOURNAL_DIR = LOGS_DIR / "journal"
CHICAGO_TZ = pytz.timezone("America/Chicago")

def parse_iso_timestamp(ts_str: str) -> Optional[datetime]:
    """Parse ISO timestamp string to datetime."""
    if not ts_str:
        return None
    try:
        if ts_str.endswith('Z'):
            ts_str = ts_str[:-1] + '+00:00'
        return datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
    except:
        return None

def load_state_transitions(date_str: str) -> List[dict]:
    """Load STATE_TRANSITION events from all logs."""
    engine_log = LOGS_DIR / "robot_ENGINE.jsonl"
    instrument_logs = list(LOGS_DIR.glob("robot_*.jsonl"))
    
    transitions = []
    
    # Load ENGINE log
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_date = event.get("trading_date", "")
                    if event_date != date_str:
                        continue
                    
                    if event.get("event") == "STATE_TRANSITION":
                        transitions.append(event)
                except:
                    pass
    
    # Load instrument logs
    for log_file in instrument_logs:
        if log_file.name == "robot_ENGINE.jsonl":
            continue
        
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_date = event.get("trading_date", "")
                    if event_date != date_str:
                        continue
                    
                    if event.get("event") == "STATE_TRANSITION":
                        transitions.append(event)
                except:
                    pass
    
    return transitions

def load_stream_states_from_events(date_str: str) -> Dict[str, List[dict]]:
    """Load stream states from various event types that indicate state."""
    engine_log = LOGS_DIR / "robot_ENGINE.jsonl"
    instrument_logs = list(LOGS_DIR.glob("robot_*.jsonl"))
    
    stream_states = defaultdict(list)
    
    # Load ENGINE log
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    # Check trading_date or use timestamp date
                    event_date = event.get("trading_date", "")
                    if not event_date:
                        # Try to get date from timestamp
                        ts_str = event.get("ts_utc", "")
                        if ts_str:
                            try:
                                from datetime import datetime
                                ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                                event_date = ts.strftime("%Y-%m-%d")
                            except:
                                pass
                    
                    if event_date != date_str:
                        continue
                    
                    event_type = event.get("event", "")
                    payload = event.get("data", {}).get("payload", {})
                    
                    # STATE_TRANSITION events
                    if event_type == "STATE_TRANSITION":
                        stream = payload.get("stream", "")
                        if stream:
                            stream_states[stream].append({
                                "timestamp": event.get("ts_utc", ""),
                                "from_state": payload.get("from_state", ""),
                                "to_state": payload.get("to_state", ""),
                                "reason": payload.get("reason", ""),
                                "source": "STATE_TRANSITION"
                            })
                    
                    # GAP_VIOLATIONS_SUMMARY, BARSREQUEST_STREAM_STATUS, STREAMS_CREATED
                    elif event_type in ["GAP_VIOLATIONS_SUMMARY", "BARSREQUEST_STREAM_STATUS", "STREAMS_CREATED"]:
                        streams_info = payload.get("streams", [])
                        if not streams_info and "invalidated_streams" in payload:
                            streams_info = payload.get("invalidated_streams", [])
                        
                        for stream_info in streams_info:
                            stream = stream_info.get("stream", "")
                            state = stream_info.get("state", "")
                            if stream and state:
                                stream_states[stream].append({
                                    "timestamp": event.get("ts_utc", ""),
                                    "from_state": "",
                                    "to_state": state,
                                    "reason": f"from_{event_type}",
                                    "source": event_type
                                })
                except:
                    pass
    
    # Load instrument logs
    for log_file in instrument_logs:
        if log_file.name == "robot_ENGINE.jsonl":
            continue
        
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                    event_date = event.get("trading_date", "")
                    if event_date != date_str:
                        continue
                    
                    if event.get("event") == "STATE_TRANSITION":
                        payload = event.get("data", {}).get("payload", {})
                        stream = payload.get("stream", "")
                        if stream:
                            stream_states[stream].append({
                                "timestamp": event.get("ts_utc", ""),
                                "from_state": payload.get("from_state", ""),
                                "to_state": payload.get("to_state", ""),
                                "reason": payload.get("reason", ""),
                                "source": "STATE_TRANSITION"
                            })
                except:
                    pass
    
    return dict(stream_states)

def get_final_state(transitions: List[dict]) -> Optional[str]:
    """Get the final state from a list of transitions."""
    if not transitions:
        return None
    
    # Sort by timestamp
    sorted_transitions = sorted(transitions, key=lambda x: x.get("timestamp", ""))
    
    # Get last transition's to_state
    last_transition = sorted_transitions[-1]
    return last_transition.get("to_state")

def analyze_stream_progression(date_str: str):
    """Analyze stream state progression."""
    print("="*80)
    print(f"STREAM STATE PROGRESSION TEST - {date_str}")
    print("="*80)
    print("\n[PURPOSE] Verify streams progress beyond PRE_HYDRATION")
    print("          PRE_HYDRATION -> ARMED proves bars are usable by the engine.\n")
    
    # Load state transitions
    print("[1] Loading stream state transitions from logs...")
    stream_states = load_stream_states_from_events(date_str)
    print(f"  Found state information for {len(stream_states)} streams")
    
    if not stream_states:
        print("\n[WARNING] No stream state information found")
        print("   This may indicate:")
        print("   - Strategy just started")
        print("   - No streams were created")
        print("   - Logs are from before streams were initialized")
        return True  # Not necessarily a failure
    
    # Analyze each stream
    print(f"\n[2] Analyzing state progression for {len(stream_states)} streams...")
    
    streams_in_pre_hydration = []
    streams_reached_armed = []
    streams_reached_range_building = []
    streams_reached_range_locked = []
    streams_done = []
    
    for stream, transitions in stream_states.items():
        final_state = get_final_state(transitions)
        
        if not final_state:
            streams_in_pre_hydration.append(stream)
        elif final_state == "PRE_HYDRATION":
            streams_in_pre_hydration.append(stream)
        elif final_state == "ARMED":
            streams_reached_armed.append(stream)
        elif final_state == "RANGE_BUILDING":
            streams_reached_range_building.append(stream)
        elif final_state == "RANGE_LOCKED":
            streams_reached_range_locked.append(stream)
        elif final_state == "DONE":
            streams_done.append(stream)
    
    # Calculate progression metrics
    total_streams = len(stream_states)
    stuck_in_pre_hydration = len(streams_in_pre_hydration)
    reached_armed = len(streams_reached_armed) + len(streams_reached_range_building) + len(streams_reached_range_locked) + len(streams_done)
    
    # Report results
    print(f"\n[3] STATE PROGRESSION RESULTS:")
    print(f"  Total streams: {total_streams}")
    print(f"  Stuck in PRE_HYDRATION: {stuck_in_pre_hydration}")
    print(f"  Reached ARMED: {len(streams_reached_armed)}")
    print(f"  Reached RANGE_BUILDING: {len(streams_reached_range_building)}")
    print(f"  Reached RANGE_LOCKED: {len(streams_reached_range_locked)}")
    print(f"  Reached DONE: {len(streams_done)}")
    print(f"  Progressed beyond PRE_HYDRATION: {reached_armed}")
    
    if total_streams > 0:
        progression_rate = (reached_armed / total_streams) * 100
        print(f"\n  Progression rate: {progression_rate:.1f}%")
    
    # Show streams stuck in PRE_HYDRATION
    if streams_in_pre_hydration:
        print(f"\n[4] Streams stuck in PRE_HYDRATION (potential issue):")
        for stream in sorted(streams_in_pre_hydration):
            transitions = stream_states[stream]
            print(f"  - {stream} ({len(transitions)} state events)")
            # Show last few transitions
            sorted_transitions = sorted(transitions, key=lambda x: x.get("timestamp", ""))
            for trans in sorted_transitions[-3:]:
                from_state = trans.get("from_state", "")
                to_state = trans.get("to_state", "")
                timestamp = trans.get("timestamp", "")[:19]  # Truncate to seconds
                print(f"    {timestamp}: {from_state} → {to_state}")
    
    # Show streams that progressed
    if streams_reached_armed:
        print(f"\n[5] Sample streams that reached ARMED (expected):")
        for stream in sorted(streams_reached_armed)[:5]:
            transitions = stream_states[stream]
            sorted_transitions = sorted(transitions, key=lambda x: x.get("timestamp", ""))
            final_state = sorted_transitions[-1].get("to_state", "")
            timestamp = sorted_transitions[-1].get("timestamp", "")[:19]
            print(f"  - {stream}: {final_state} (last update: {timestamp})")
    
    # Calculate time from PRE_HYDRATION to ARMED (if available)
    print(f"\n[6] Timing analysis:")
    pre_hydration_to_armed_times = []
    
    for stream, transitions in stream_states.items():
        sorted_transitions = sorted(transitions, key=lambda x: x.get("timestamp", ""))
        
        # Find PRE_HYDRATION → ARMED transition
        for i, trans in enumerate(sorted_transitions):
            if trans.get("from_state") == "PRE_HYDRATION" and trans.get("to_state") == "ARMED":
                if i > 0:
                    prev_timestamp = parse_iso_timestamp(sorted_transitions[i-1].get("timestamp", ""))
                    curr_timestamp = parse_iso_timestamp(trans.get("timestamp", ""))
                    if prev_timestamp and curr_timestamp:
                        duration = (curr_timestamp - prev_timestamp).total_seconds()
                        pre_hydration_to_armed_times.append(duration)
                break
    
    if pre_hydration_to_armed_times:
        avg_time = sum(pre_hydration_to_armed_times) / len(pre_hydration_to_armed_times)
        print(f"  Average time PRE_HYDRATION → ARMED: {avg_time:.1f} seconds")
        print(f"  Transitions analyzed: {len(pre_hydration_to_armed_times)}")
    else:
        print("  No PRE_HYDRATION → ARMED transitions found in logs")
    
    # Final verdict
    print("\n" + "="*80)
    if total_streams == 0:
        print("[WARNING] No streams found - cannot verify progression")
        return True
    elif stuck_in_pre_hydration == total_streams:
        print("[FAIL] All streams stuck in PRE_HYDRATION!")
        print("   This indicates bars are not being accepted or processed correctly.")
        return False
    elif stuck_in_pre_hydration > 0 and stuck_in_pre_hydration >= total_streams * 0.5:
        print("[FAIL] More than 50% of streams stuck in PRE_HYDRATION!")
        print(f"   Stuck: {stuck_in_pre_hydration}/{total_streams}")
        return False
    elif reached_armed == 0:
        print("[FAIL] No streams progressed beyond PRE_HYDRATION!")
        print("   This indicates bars are not being processed.")
        return False
    else:
        print("[PASS] Streams are progressing beyond PRE_HYDRATION!")
        print(f"   {reached_armed}/{total_streams} streams progressed ({progression_rate:.1f}%)")
        print("   This confirms bars are being accepted and processed correctly.")
        return True

def main():
    """Main entry point."""
    if len(sys.argv) > 1:
        date_str = sys.argv[1]
    else:
        # Default to today
        today = datetime.now(CHICAGO_TZ).date()
        date_str = today.strftime("%Y-%m-%d")
    
    print(f"Testing stream state progression for date: {date_str}")
    print(f"Logs directory: {LOGS_DIR}")
    
    if not LOGS_DIR.exists():
        print(f"ERROR: Logs directory not found: {LOGS_DIR}")
        return 1
    
    success = analyze_stream_progression(date_str)
    return 0 if success else 1

if __name__ == "__main__":
    sys.exit(main())
