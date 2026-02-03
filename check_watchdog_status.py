#!/usr/bin/env python3
"""
Watchdog Status Diagnostic
Checks why watchdog is showing FAIL-CLOSED, BROKER DISCONNECTED, DATA STALLED
"""

import sys
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

# Add project root to path
qtsw2_root = Path(__file__).parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

CHICAGO_TZ = pytz.timezone("America/Chicago")

def check_recovery_state():
    """Check recovery state from recent events"""
    print("\n" + "="*80)
    print("1. RECOVERY STATE ANALYSIS")
    print("="*80)
    
    feed_file = qtsw2_root / "logs" / "robot" / "frontend_feed.jsonl"
    if not feed_file.exists():
        print("[ERROR] Feed file not found")
        return
    
    recovery_events = []
    with open(feed_file, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
        recent_lines = lines[-5000:] if len(lines) > 5000 else lines
        
        for line in recent_lines:
            try:
                event = json.loads(line.strip())
                event_type = event.get("event_type", "")
                if "DISCONNECT" in event_type or "RECOVERY" in event_type:
                    recovery_events.append({
                        "type": event_type,
                        "timestamp": event.get("timestamp_utc", ""),
                        "run_id": event.get("run_id", "")[:8]
                    })
            except:
                continue
    
    if recovery_events:
        print(f"Found {len(recovery_events)} recovery/disconnect events:")
        for evt in recovery_events[-10:]:
            print(f"  {evt['timestamp'][:19]} - {evt['type']} (run_id: {evt['run_id']}...)")
        
        latest = recovery_events[-1]
        print(f"\nLatest recovery event: {latest['type']} at {latest['timestamp']}")
        
        if "FAIL_CLOSED" in latest['type']:
            print("[ISSUE] Latest event is DISCONNECT_FAIL_CLOSED_ENTERED")
            print("  This sets recovery_state to DISCONNECT_FAIL_CLOSED")
            print("  Check if DISCONNECT_RECOVERY_COMPLETE was ever received")
        elif "RECOVERY_STARTED" in latest['type']:
            print("[ISSUE] Recovery started but may not have completed")
            print("  Check if DISCONNECT_RECOVERY_COMPLETE was received")
        elif "RECOVERY_COMPLETE" in latest['type']:
            print("[OK] Recovery completed - state should be CONNECTED_OK")
        elif "RECOVERED" in latest['type']:
            print("[OK] Connection recovered - state should be Connected")
    else:
        print("[OK] No recovery/disconnect events found in recent feed")
        print("  Recovery state should default to CONNECTED_OK")

def check_connection_status():
    """Check connection status from recent events"""
    print("\n" + "="*80)
    print("2. CONNECTION STATUS ANALYSIS")
    print("="*80)
    
    feed_file = qtsw2_root / "logs" / "robot" / "frontend_feed.jsonl"
    if not feed_file.exists():
        print("[ERROR] Feed file not found")
        return
    
    connection_events = []
    with open(feed_file, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
        recent_lines = lines[-5000:] if len(lines) > 5000 else lines
        
        for line in recent_lines:
            try:
                event = json.loads(line.strip())
                event_type = event.get("event_type", "")
                if "CONNECTION" in event_type:
                    connection_events.append({
                        "type": event_type,
                        "timestamp": event.get("timestamp_utc", ""),
                        "run_id": event.get("run_id", "")[:8]
                    })
            except:
                continue
    
    if connection_events:
        print(f"Found {len(connection_events)} connection events:")
        for evt in connection_events[-10:]:
            print(f"  {evt['timestamp'][:19]} - {evt['type']} (run_id: {evt['run_id']}...)")
        
        latest = connection_events[-1]
        print(f"\nLatest connection event: {latest['type']} at {latest['timestamp']}")
        
        if "LOST" in latest['type']:
            print("[ISSUE] Latest event is CONNECTION_LOST")
            print("  This sets connection_status to ConnectionLost")
            print("  Check if CONNECTION_RECOVERED was received")
        elif "RECOVERED" in latest['type']:
            print("[OK] Connection recovered - status should be Connected")
    else:
        print("[OK] No connection events found in recent feed")
        print("  Connection status should default to Connected")

def check_data_stall():
    """Check data stall detection logic"""
    print("\n" + "="*80)
    print("3. DATA STALL ANALYSIS")
    print("="*80)
    
    feed_file = qtsw2_root / "logs" / "robot" / "frontend_feed.jsonl"
    if not feed_file.exists():
        print("[ERROR] Feed file not found")
        return
    
    bar_events = []
    tick_events = []
    
    with open(feed_file, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
        recent_lines = lines[-5000:] if len(lines) > 5000 else lines
        
        for line in recent_lines:
            try:
                event = json.loads(line.strip())
                event_type = event.get("event_type", "")
                timestamp_str = event.get("timestamp_utc", "")
                
                if event_type in ("BAR_ACCEPTED", "BAR_RECEIVED_NO_STREAMS", "ONBARUPDATE_CALLED"):
                    bar_events.append({
                        "type": event_type,
                        "timestamp": timestamp_str,
                        "instrument": event.get("execution_instrument_full_name") or event.get("instrument", "")
                    })
                elif event_type == "ENGINE_TICK_CALLSITE":
                    tick_events.append({
                        "timestamp": timestamp_str
                    })
            except:
                continue
    
    print(f"Found {len(bar_events)} bar events and {len(tick_events)} tick events in recent feed")
    
    if bar_events:
        latest_bar = bar_events[-1]
        print(f"\nLatest bar event: {latest_bar['type']} at {latest_bar['timestamp']}")
        print(f"  Instrument: {latest_bar['instrument']}")
        
        try:
            bar_time = datetime.fromisoformat(latest_bar['timestamp'].replace('Z', '+00:00'))
            if bar_time.tzinfo is None:
                bar_time = bar_time.replace(tzinfo=timezone.utc)
            now = datetime.now(timezone.utc)
            age_seconds = (now - bar_time).total_seconds()
            print(f"  Age: {age_seconds:.1f} seconds ({age_seconds/60:.1f} minutes)")
            
            if age_seconds > 120:
                print(f"[ISSUE] Bar age ({age_seconds:.1f}s) exceeds DATA_STALL_THRESHOLD (120s)")
                print("  This will trigger DATA STALLED if market is open")
            else:
                print(f"[OK] Bar age ({age_seconds:.1f}s) is within threshold")
        except Exception as e:
            print(f"[ERROR] Failed to parse bar timestamp: {e}")
    else:
        print("[ISSUE] No bar events found in recent feed")
        print("  This will trigger DATA STALLED if market is open and bars are expected")
    
    if tick_events:
        latest_tick = tick_events[-1]
        print(f"\nLatest tick event: ENGINE_TICK_CALLSITE at {latest_tick['timestamp']}")
        try:
            tick_time = datetime.fromisoformat(latest_tick['timestamp'].replace('Z', '+00:00'))
            if tick_time.tzinfo is None:
                tick_time = tick_time.replace(tzinfo=timezone.utc)
            now = datetime.now(timezone.utc)
            age_seconds = (now - tick_time).total_seconds()
            print(f"  Age: {age_seconds:.1f} seconds")
            
            if age_seconds > 15:
                print(f"[ISSUE] Tick age ({age_seconds:.1f}s) exceeds ENGINE_TICK_STALL_THRESHOLD (15s)")
                print("  This will trigger ENGINE STALLED")
            else:
                print(f"[OK] Tick age ({age_seconds:.1f}s) is within threshold")
        except Exception as e:
            print(f"[ERROR] Failed to parse tick timestamp: {e}")
    else:
        print("[ISSUE] No tick events found in recent feed")
        print("  This will trigger ENGINE STALLED")

def check_market_status():
    """Check market open status"""
    print("\n" + "="*80)
    print("4. MARKET STATUS ANALYSIS")
    print("="*80)
    
    try:
        from modules.watchdog.market_session import is_market_open
        chicago_now = datetime.now(CHICAGO_TZ)
        market_open = is_market_open(chicago_now)
        print(f"Current Chicago time: {chicago_now.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        print(f"Market open: {market_open}")
        
        if market_open:
            print("[OK] Market is open - this is correct")
        else:
            print("[OK] Market is closed - DATA STALLED should not trigger")
    except Exception as e:
        print(f"[ERROR] Failed to check market status: {e}")

def check_identity_status():
    """Check identity invariants status"""
    print("\n" + "="*80)
    print("5. IDENTITY INVARIANTS ANALYSIS")
    print("="*80)
    
    feed_file = qtsw2_root / "logs" / "robot" / "frontend_feed.jsonl"
    if not feed_file.exists():
        print("[ERROR] Feed file not found")
        return
    
    identity_events = []
    with open(feed_file, 'r', encoding='utf-8-sig') as f:
        lines = f.readlines()
        recent_lines = lines[-5000:] if len(lines) > 5000 else lines
        
        for line in recent_lines:
            try:
                event = json.loads(line.strip())
                if event.get("event_type") == "IDENTITY_INVARIANTS_STATUS":
                    identity_events.append({
                        "timestamp": event.get("timestamp_utc", ""),
                        "data": event.get("data", {})
                    })
            except:
                continue
    
    if identity_events:
        latest = identity_events[-1]
        print(f"Found {len(identity_events)} identity events")
        print(f"Latest: {latest['timestamp']}")
        
        data = latest['data']
        pass_value = data.get("pass")
        violations = data.get("violations", [])
        
        print(f"  Pass: {pass_value}")
        print(f"  Violations: {violations}")
        
        if pass_value is True:
            print("[OK] Identity invariants passed - IDENTITY OK is correct")
        elif pass_value is False:
            print("[ISSUE] Identity invariants failed - should show IDENTITY VIOLATION")
        else:
            print("[WARN] Identity pass value is None or unclear")
    else:
        print("[WARN] No identity events found - status may be unknown")

def check_state_manager_logic():
    """Check state manager logic for potential issues"""
    print("\n" + "="*80)
    print("6. STATE MANAGER LOGIC CHECK")
    print("="*80)
    
    # Check if recovery state auto-clear logic is working
    print("Checking recovery state auto-clear logic...")
    print("  - Recovery state should auto-clear after RECOVERY_TIMEOUT_SECONDS (600s = 10 min)")
    print("  - If recovery_state == 'RECOVERY_RUNNING' for > 10 min, it auto-clears to CONNECTED_OK")
    
    # Check if connection status rebuild is working
    print("\nChecking connection status rebuild logic...")
    print("  - Connection status should be rebuilt from recent events on startup")
    print("  - If no connection events found, defaults to 'Connected'")
    
    # Check data stall smoothing
    print("\nChecking data stall smoothing logic...")
    print("  - Data stall detection uses smoothing/debouncing (last 3 polls)")
    print("  - Requires 2 consecutive polls with same status before changing")
    print("  - This prevents flickering from temporary threshold violations")

def main():
    print("="*80)
    print("WATCHDOG STATUS DIAGNOSTIC")
    print("="*80)
    print(f"Timestamp: {datetime.now().isoformat()}")
    
    check_recovery_state()
    check_connection_status()
    check_data_stall()
    check_market_status()
    check_identity_status()
    check_state_manager_logic()
    
    print("\n" + "="*80)
    print("SUMMARY")
    print("="*80)
    print("Review the analysis above to identify why watchdog is showing:")
    print("  - FAIL-CLOSED (recovery_state issue)")
    print("  - BROKER DISCONNECTED (connection_status issue)")
    print("  - DATA STALLED (data_stall_detected issue)")
    print("="*80)

if __name__ == "__main__":
    main()
