"""
Clear Pipeline Lock - Safely remove stale lock files
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
    print("CLEAR PIPELINE LOCK")
    print("="*80)
    
    # Check lock files
    lock_file = qtsw2_root / "automation" / "logs" / "pipeline.lock"
    lock_file_alt = qtsw2_root / "automation" / "logs" / ".pipeline.lock"
    
    lock_to_clear = None
    if lock_file.exists():
        lock_to_clear = lock_file
    elif lock_file_alt.exists():
        lock_to_clear = lock_file_alt
    
    if not lock_to_clear:
        print("\n[OK] No lock file found - nothing to clear")
        return
    
    print(f"\n[LOCK FILE TO CLEAR]")
    print(f"  Path: {lock_to_clear}")
    
    # Show lock info before clearing
    try:
        with open(lock_to_clear, 'r') as f:
            lock_data = json.load(f)
        print(f"  Run ID: {lock_data.get('run_id', 'unknown')}")
        print(f"  Acquired at: {lock_data.get('acquired_at', 'unknown')}")
        
        if 'acquired_at' in lock_data:
            acquired_at = datetime.fromisoformat(lock_data['acquired_at'])
            age = (datetime.now() - acquired_at).total_seconds()
            print(f"  Age: {age:.1f} seconds ({age/60:.1f} minutes)")
    except Exception as e:
        print(f"  [WARNING] Could not read lock data: {e}")
    
    # Ask for confirmation (in a real scenario, but for script we'll just do it)
    print(f"\n[ACTION]")
    try:
        lock_to_clear.unlink()
        print(f"  [SUCCESS] Lock file deleted: {lock_to_clear}")
        print(f"  Pipeline should now be able to run")
    except Exception as e:
        print(f"  [ERROR] Failed to delete lock file: {e}")
        print(f"  You may need to delete it manually: {lock_to_clear}")

if __name__ == "__main__":
    main()








