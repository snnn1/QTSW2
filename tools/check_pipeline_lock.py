"""
Check Pipeline Lock - Diagnose lock issues
"""

import sys
from pathlib import Path
import json
from datetime import datetime

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    print("="*80)
    print("PIPELINE LOCK DIAGNOSTIC")
    print("="*80)
    
    # Check lock file
    lock_file = qtsw2_root / "automation" / "logs" / "pipeline.lock"
    lock_file_alt = qtsw2_root / "automation" / "logs" / ".pipeline.lock"
    
    print(f"\n[LOCK FILE CHECK]")
    print(f"  Standard location: {lock_file}")
    print(f"    Exists: {lock_file.exists()}")
    
    print(f"  Alternative location: {lock_file_alt}")
    print(f"    Exists: {lock_file_alt.exists()}")
    
    # Check which lock file exists
    active_lock = None
    if lock_file.exists():
        active_lock = lock_file
    elif lock_file_alt.exists():
        active_lock = lock_file_alt
    
    if active_lock:
        print(f"\n[ACTIVE LOCK FILE]")
        print(f"  Path: {active_lock}")
        
        try:
            # Read lock data
            with open(active_lock, 'r') as f:
                lock_data = json.load(f)
            
            print(f"  Lock data: {json.dumps(lock_data, indent=2)}")
            
            # Check age
            if 'acquired_at' in lock_data:
                acquired_at_str = lock_data['acquired_at']
                try:
                    acquired_at = datetime.fromisoformat(acquired_at_str)
                    now = datetime.now()
                    age = (now - acquired_at).total_seconds()
                    age_minutes = age / 60
                    
                    print(f"\n  Lock Age:")
                    print(f"    Acquired at: {acquired_at_str}")
                    print(f"    Current time: {now.isoformat()}")
                    print(f"    Age: {age:.1f} seconds ({age_minutes:.1f} minutes)")
                    
                    # Check if stale (max runtime is 3600 seconds = 1 hour)
                    max_runtime = 3600
                    if age > max_runtime:
                        print(f"    [STALE] Lock is older than {max_runtime/60:.0f} minutes - should be cleared!")
                    else:
                        print(f"    [ACTIVE] Lock is within {max_runtime/60:.0f} minute limit")
                except Exception as e:
                    print(f"    Error parsing timestamp: {e}")
            
            # Check file modification time
            mtime = datetime.fromtimestamp(active_lock.stat().st_mtime)
            file_age = (datetime.now() - mtime).total_seconds()
            print(f"\n  File Modification Time:")
            print(f"    Last modified: {mtime.isoformat()}")
            print(f"    File age: {file_age:.1f} seconds ({file_age/60:.1f} minutes)")
            
        except Exception as e:
            print(f"  [ERROR] Failed to read lock file: {e}")
    else:
        print(f"\n[NO LOCK FILE FOUND]")
        print(f"  Lock file does not exist - pipeline should be able to run")
    
    # Check orchestrator state
    state_file = qtsw2_root / "automation" / "logs" / "orchestrator_state.json"
    print(f"\n[ORCHESTRATOR STATE]")
    print(f"  State file: {state_file}")
    print(f"    Exists: {state_file.exists()}")
    
    if state_file.exists():
        try:
            with open(state_file, 'r') as f:
                content = f.read().strip()
                if content and content != 'null':
                    state_data = json.loads(content)
                    print(f"  State data: {json.dumps(state_data, indent=2)}")
                else:
                    print(f"  State file is empty or null")
        except Exception as e:
            print(f"  [ERROR] Failed to read state file: {e}")
    
    # Recommendations
    print(f"\n[RECOMMENDATIONS]")
    if active_lock:
        try:
            with open(active_lock, 'r') as f:
                lock_data = json.load(f)
            acquired_at_str = lock_data.get('acquired_at', '')
            if acquired_at_str:
                acquired_at = datetime.fromisoformat(acquired_at_str)
                age = (datetime.now() - acquired_at).total_seconds()
                if age > 3600:  # 1 hour
                    print(f"  [ACTION NEEDED] Lock is stale (older than 1 hour)")
                    print(f"    You can safely delete the lock file:")
                    print(f"    {active_lock}")
                    print(f"    Or wait for it to expire automatically")
                else:
                    print(f"  [INFO] Lock appears active - pipeline may be running")
                    print(f"    Run ID: {lock_data.get('run_id', 'unknown')}")
                    print(f"    If pipeline is not actually running, you can delete the lock file")
        except:
            print(f"  [ACTION NEEDED] Lock file exists but may be corrupted")
            print(f"    You can safely delete it: {active_lock}")
    else:
        print(f"  [OK] No lock file found - pipeline should be able to run")
        print(f"    If you're still getting lock errors, check:")
        print(f"    1. Multiple backend instances running")
        print(f"    2. File permissions on lock directory")
        print(f"    3. Network file system issues")

if __name__ == "__main__":
    main()








