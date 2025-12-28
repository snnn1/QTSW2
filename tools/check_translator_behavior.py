"""
Check Translator Behavior - Verify if translator is actually translating
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
    print("TRANSLATOR BEHAVIOR CHECK")
    print("="*80)
    
    # Find latest pipeline log
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    if not pipeline_files:
        print("No pipeline files found")
        return
    
    latest_file = pipeline_files[0]
    print(f"\n[LATEST PIPELINE RUN]")
    print(f"  File: {latest_file.name}")
    mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
    print(f"  Last modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Read events
    events = []
    with open(latest_file, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass
    
    # Check translator events
    translator_events = [e for e in events if e.get('stage') == 'translator']
    
    print(f"\n[TRANSLATOR EVENTS]")
    print(f"  Total translator events: {len(translator_events)}")
    
    for event in translator_events:
        event_type = event.get('event', '')
        msg = event.get('msg', '')
        ts = event.get('timestamp', '')
        data = event.get('data', {})
        
        print(f"\n  Event: {event_type}")
        print(f"    Time: {ts}")
        print(f"    Message: {msg}")
        if data:
            print(f"    Data: {data}")
    
    # Check if files were actually written
    print(f"\n[TRANSLATED FILES STATUS]")
    today = datetime.now().strftime('%Y-%m-%d')
    translated_dir = qtsw2_root / "data" / "translated"
    translated_files = list(translated_dir.rglob(f'*{today}*.parquet'))
    
    if translated_files:
        print(f"  Found {len(translated_files)} translated file(s) for today")
        for f in sorted(translated_files)[:3]:
            mtime = datetime.fromtimestamp(f.stat().st_mtime)
            print(f"    {f.name}")
            print(f"      Last modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Check raw files
    print(f"\n[RAW FILES STATUS]")
    raw_dir = qtsw2_root / "data" / "raw"
    raw_files = list(raw_dir.rglob(f'*{today}*.csv'))
    
    if raw_files:
        print(f"  Found {len(raw_files)} raw file(s) for today")
        latest_raw = max([f.stat().st_mtime for f in raw_files])
        latest_raw_dt = datetime.fromtimestamp(latest_raw)
        print(f"    Latest raw file: {latest_raw_dt.strftime('%Y-%m-%d %H:%M:%S')}")
        
        if translated_files:
            latest_trans = max([f.stat().st_mtime for f in translated_files])
            latest_trans_dt = datetime.fromtimestamp(latest_trans)
            print(f"    Latest translated: {latest_trans_dt.strftime('%Y-%m-%d %H:%M:%S')}")
            
            if latest_raw > latest_trans:
                diff_minutes = (latest_raw - latest_trans) / 60
                print(f"    [WARNING] Raw files are {diff_minutes:.1f} minutes newer - should be re-translated!")
            else:
                print(f"    [OK] Translated files are up to date")

if __name__ == "__main__":
    main()








