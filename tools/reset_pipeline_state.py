"""
Reset Pipeline State - Clear stuck pipeline state
"""

import sys
import json
from pathlib import Path
from datetime import datetime

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    print("="*80)
    print("RESET PIPELINE STATE")
    print("="*80)
    
    # Clear orchestrator state
    state_file = qtsw2_root / "automation" / "logs" / "orchestrator_state.json"
    
    if state_file.exists():
        print(f"\n[ORCHESTRATOR STATE]")
        print(f"  File: {state_file}")
        
        # Read current state
        try:
            with open(state_file, 'r') as f:
                current_state = json.load(f)
            print(f"  Current state: {current_state.get('state', 'unknown')}")
            print(f"  Run ID: {current_state.get('run_id', 'unknown')}")
        except Exception as e:
            print(f"  Error reading state: {e}")
        
        # Clear state
        try:
            # Write null to clear state
            with open(state_file, 'w') as f:
                json.dump(None, f, indent=2)
            print(f"\n[SUCCESS] Orchestrator state cleared")
        except Exception as e:
            print(f"\n[ERROR] Failed to clear state: {e}")
            return 1
    else:
        print(f"\n[INFO] No orchestrator state file found (already clear)")
    
    # Check for lock files
    lock_file = qtsw2_root / "automation" / "logs" / "pipeline.lock"
    lock_file_alt = qtsw2_root / "automation" / "logs" / ".pipeline.lock"
    
    print(f"\n[LOCK FILES]")
    if lock_file.exists():
        print(f"  Found: {lock_file}")
        try:
            lock_file.unlink()
            print(f"  [SUCCESS] Deleted lock file")
        except Exception as e:
            print(f"  [ERROR] Failed to delete lock file: {e}")
    
    if lock_file_alt.exists():
        print(f"  Found: {lock_file_alt}")
        try:
            lock_file_alt.unlink()
            print(f"  [SUCCESS] Deleted alternative lock file")
        except Exception as e:
            print(f"  [ERROR] Failed to delete alternative lock file: {e}")
    
    if not lock_file.exists() and not lock_file_alt.exists():
        print(f"  [INFO] No lock files found")
    
    print(f"\n[COMPLETE] Pipeline state reset")
    print(f"  The pipeline should now be in 'idle' state")
    print(f"  You can start a new pipeline run from the dashboard")
    
    return 0

if __name__ == "__main__":
    sys.exit(main())
