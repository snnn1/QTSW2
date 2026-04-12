#!/usr/bin/env python3
"""Test watchdog fixes"""

import sys
from pathlib import Path
from datetime import datetime, timezone

qtsw2_root = Path(__file__).parent
sys.path.insert(0, str(qtsw2_root))

from modules.watchdog.state_manager import WatchdogStateManager

def test_auto_clear():
    print("Testing auto-clear logic...")
    
    sm = WatchdogStateManager()
    
    # Set stuck states
    sm._recovery_state = 'DISCONNECT_FAIL_CLOSED'
    sm._connection_status = 'ConnectionLost'
    
    print(f"Before: recovery_state={sm._recovery_state}, connection_status={sm._connection_status}")
    
    # Update engine tick (simulating engine is alive)
    sm.update_engine_tick(datetime.now(timezone.utc))
    
    # Compute status (this should trigger auto-clear)
    status = sm.compute_watchdog_status()
    
    print(f"After: recovery_state={status['recovery_state']}, connection_status={status['connection_status']}")
    
    if status['recovery_state'] == 'CONNECTED_OK' and status['connection_status'] == 'Connected':
        print("[OK] Auto-clear logic working correctly")
        return True
    else:
        print("[FAIL] Auto-clear logic not working")
        return False

if __name__ == "__main__":
    test_auto_clear()
