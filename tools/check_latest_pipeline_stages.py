"""Check latest pipeline run stages"""
import json
from pathlib import Path
from datetime import datetime

qtsw2_root = Path(__file__).parent.parent
events_dir = qtsw2_root / "automation" / "logs" / "events"

pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
if not pipeline_files:
    print("No pipeline runs found")
    exit(1)

latest_file = pipeline_files[0]
print("="*80)
print(f"LATEST PIPELINE RUN: {latest_file.name}")
print("="*80)

mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
print(f"Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}\n")

with open(latest_file, 'r') as f:
    events = [json.loads(l) for l in f if l.strip()]

# Group by stage
stages = {}
for e in events:
    stage = e.get('stage', 'unknown')
    if stage not in stages:
        stages[stage] = []
    stages[stage].append(e)

print("[STAGES]")
for stage, stage_events in sorted(stages.items()):
    print(f"  {stage}: {len(stage_events)} events")
    event_types = set(e.get('event') for e in stage_events)
    print(f"    Event types: {sorted(event_types)}")
    
    # Show key events
    for event_type in ['start', 'success', 'failure', 'error']:
        matching = [e for e in stage_events if e.get('event') == event_type]
        if matching:
            for e in matching:
                msg = e.get('msg', '')[:100]
                print(f"      {event_type}: {msg}")

# Check for state transitions
print(f"\n[STATE TRANSITIONS]")
state_events = [e for e in events if e.get('event') == 'state_change']
for e in state_events:
    data = e.get('data', {})
    old_state = data.get('old_state', 'unknown')
    new_state = data.get('new_state', 'unknown')
    print(f"  {old_state} -> {new_state}")

# Check if merger should have run
print(f"\n[ANALYZER STATUS]")
analyzer_events = stages.get('analyzer', [])
analyzer_success = any(e.get('event') == 'success' for e in analyzer_events)
print(f"  Analyzer completed: {analyzer_success}")

print(f"\n[MERGER STATUS]")
merger_events = stages.get('merger', [])
if merger_events:
    merger_success = any(e.get('event') == 'success' for e in merger_events)
    print(f"  Merger ran: True")
    print(f"  Merger completed: {merger_success}")
else:
    print(f"  Merger ran: False")
    if analyzer_success:
        print(f"  [WARNING] Analyzer completed but merger did not run!")

