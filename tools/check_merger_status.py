"""Check merger status and recent activity"""
import json
from pathlib import Path
from datetime import datetime

qtsw2_root = Path(__file__).parent.parent

print("="*80)
print("MERGER STATUS CHECK")
print("="*80)

# Check merger processed log
merger_log = qtsw2_root / "data" / "merger_processed.json"
if merger_log.exists():
    with open(merger_log, 'r') as f:
        data = json.load(f)
    analyzer_processed = data.get("analyzer", [])
    print(f"\n[PROCESSED FOLDERS]")
    print(f"  Total processed: {len(analyzer_processed)}")
    if analyzer_processed:
        print(f"  Latest processed: {analyzer_processed[-1]}")
else:
    print(f"\n[PROCESSED FOLDERS]")
    print(f"  Merger log file not found: {merger_log}")

# Check analyzer temp
temp_dir = qtsw2_root / "data" / "analyzer_temp"
print(f"\n[ANALYZER TEMP]")
print(f"  Directory: {temp_dir}")
print(f"  Exists: {temp_dir.exists()}")
if temp_dir.exists():
    folders = [d for d in temp_dir.iterdir() if d.is_dir()]
    print(f"  Unprocessed folders: {len(folders)}")
    if folders:
        print(f"  Unprocessed dates:")
        for f in sorted(folders):
            print(f"    {f.name}")
    else:
        print(f"  [OK] No unprocessed folders")

# Check latest pipeline run
events_dir = qtsw2_root / "automation" / "logs" / "events"
pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
if pipeline_files:
    latest_file = pipeline_files[0]
    print(f"\n[LATEST PIPELINE RUN]")
    print(f"  File: {latest_file.name}")
    mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
    print(f"  Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    
    with open(latest_file, 'r') as f:
        events = [json.loads(l) for l in f if l.strip()]
    
    merger_events = [e for e in events if e.get('stage') == 'merger']
    print(f"  Merger events: {len(merger_events)}")
    if merger_events:
        print(f"  Merger activity:")
        for e in merger_events[-5:]:
            event_type = e.get('event', 'unknown')
            msg = e.get('msg', '')[:80]
            print(f"    {event_type}: {msg}")
    else:
        print(f"  [WARNING] No merger events found in latest run")

# Check merger script
merger_script = qtsw2_root / "modules" / "merger" / "merger.py"
print(f"\n[MERGER SCRIPT]")
print(f"  Path: {merger_script}")
print(f"  Exists: {merger_script.exists()}")
