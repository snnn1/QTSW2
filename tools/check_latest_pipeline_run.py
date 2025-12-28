"""
Check Latest Pipeline Run - Show details of the most recent pipeline execution
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
    print("LATEST PIPELINE RUN ANALYSIS")
    print("="*80)
    
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    
    if not events_dir.exists():
        print(f"[ERROR] Events directory not found: {events_dir}")
        return
    
    # Find most recent pipeline log
    pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    if not pipeline_files:
        print("[ERROR] No pipeline log files found")
        return
    
    latest_file = pipeline_files[0]
    mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
    
    print(f"\n[PIPELINE LOG FILE]")
    print(f"  File: {latest_file.name}")
    print(f"  Path: {latest_file}")
    print(f"  Last modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"  Size: {latest_file.stat().st_size / 1024:.2f} KB")
    
    # Read all events
    events = []
    try:
        with open(latest_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        event = json.loads(line)
                        events.append(event)
                    except:
                        pass
    except Exception as e:
        print(f"[ERROR] Failed to read log file: {e}")
        return
    
    if not events:
        print("[ERROR] No events found in log file")
        return
    
    print(f"\n[EVENT SUMMARY]")
    print(f"  Total events: {len(events)}")
    
    # Extract run_id
    run_id = events[0].get('run_id', 'unknown') if events else 'unknown'
    print(f"  Run ID: {run_id[:8]}...")
    
    # Group events by stage
    stage_events = {}
    for event in events:
        stage = event.get('stage', 'unknown')
        if stage not in stage_events:
            stage_events[stage] = []
        stage_events[stage].append(event)
    
    print(f"\n[EVENTS BY STAGE]")
    for stage in sorted(stage_events.keys()):
        print(f"  {stage}: {len(stage_events[stage])} event(s)")
    
    # Find key events
    print(f"\n[KEY EVENTS TIMELINE]")
    
    # Pipeline start
    pipeline_starts = [e for e in events if e.get('stage') == 'pipeline' and e.get('event') == 'start']
    if pipeline_starts:
        start = pipeline_starts[0]
        ts = start.get('timestamp', '')
        manual = start.get('data', {}).get('manual', False)
        print(f"  Pipeline Start: {ts}")
        print(f"    Type: {'Manual' if manual else 'Scheduled'}")
    
    # Scheduler events
    scheduler_events = [e for e in events if e.get('stage') == 'scheduler']
    if scheduler_events:
        for event in scheduler_events:
            event_type = event.get('event', '')
            msg = event.get('msg', '')
            ts = event.get('timestamp', '')
            print(f"  Scheduler {event_type}: {ts}")
            print(f"    {msg}")
    
    # Translator events
    translator_events = [e for e in events if e.get('stage') == 'translator']
    if translator_events:
        start = [e for e in translator_events if e.get('event') == 'start']
        success = [e for e in translator_events if e.get('event') == 'success']
        skipped = [e for e in translator_events if e.get('event') == 'skipped']
        failure = [e for e in translator_events if e.get('event') == 'failure']
        
        if start:
            print(f"  Translator Start: {start[0].get('timestamp', '')}")
        if skipped:
            data = skipped[0].get('data', {})
            print(f"  Translator Skipped: {skipped[0].get('timestamp', '')}")
            print(f"    Files written: {data.get('files_written', 0)}")
            print(f"    Files skipped: {data.get('files_skipped', 0)}")
            print(f"    Files failed: {data.get('files_failed', 0)}")
        if success:
            data = success[0].get('data', {})
            print(f"  Translator Success: {success[0].get('timestamp', '')}")
            print(f"    Files written: {data.get('files_written', 0)}")
            print(f"    Files skipped: {data.get('files_skipped', 0)}")
        if failure:
            print(f"  Translator Failure: {failure[0].get('timestamp', '')}")
            print(f"    Error: {failure[0].get('msg', '')}")
    
    # Analyzer events
    analyzer_events = [e for e in events if e.get('stage') == 'analyzer']
    if analyzer_events:
        start = [e for e in analyzer_events if e.get('event') == 'start']
        success = [e for e in analyzer_events if e.get('event') == 'success']
        failure = [e for e in analyzer_events if e.get('event') == 'failure']
        file_starts = [e for e in analyzer_events if e.get('event') == 'file_start']
        file_finish = [e for e in analyzer_events if e.get('event') == 'file_finish']
        
        if start:
            print(f"  Analyzer Start: {start[0].get('timestamp', '')}")
        if file_starts:
            instruments = [e.get('data', {}).get('instrument', '') for e in file_starts]
            print(f"  Analyzer Processing: {len(file_starts)} instrument(s)")
            print(f"    Instruments: {', '.join(instruments)}")
        if file_finish:
            successful = [e for e in file_finish if e.get('data', {}).get('status') == 'success']
            print(f"  Analyzer Completed: {len(file_finish)} instrument(s) finished")
            print(f"    Successful: {len(successful)}")
            print(f"    Failed: {len(file_finish) - len(successful)}")
        if success:
            data = success[0].get('data', {})
            print(f"  Analyzer Success: {success[0].get('timestamp', '')}")
            print(f"    Instruments processed: {data.get('instruments_processed', 0)}")
            print(f"    Instruments: {', '.join(data.get('instruments', []))}")
        if failure:
            print(f"  Analyzer Failure: {failure[0].get('timestamp', '')}")
            print(f"    Error: {failure[0].get('msg', '')}")
    
    # Merger events
    merger_events = [e for e in events if e.get('stage') == 'merger']
    if merger_events:
        start = [e for e in merger_events if e.get('event') == 'start']
        success = [e for e in merger_events if e.get('event') == 'success']
        failure = [e for e in merger_events if e.get('event') == 'failure']
        
        if start:
            print(f"  Merger Start: {start[0].get('timestamp', '')}")
        if success:
            print(f"  Merger Success: {success[0].get('timestamp', '')}")
            print(f"    {success[0].get('msg', '')}")
        if failure:
            print(f"  Merger Failure: {failure[0].get('timestamp', '')}")
            print(f"    Error: {failure[0].get('msg', '')}")
    
    # Pipeline completion
    state_changes = [e for e in events if e.get('stage') == 'pipeline' and e.get('event') == 'state_change']
    if state_changes:
        final_state = state_changes[-1]
        data = final_state.get('data', {})
        new_state = data.get('new_state', 'unknown')
        print(f"  Pipeline Final State: {new_state}")
        print(f"    Timestamp: {final_state.get('timestamp', '')}")
    
    # Calculate duration
    if events:
        first_event = events[0]
        last_event = events[-1]
        try:
            first_ts = datetime.fromisoformat(first_event.get('timestamp', '').replace('Z', '+00:00'))
            last_ts = datetime.fromisoformat(last_event.get('timestamp', '').replace('Z', '+00:00'))
            duration = (last_ts - first_ts).total_seconds()
            print(f"\n[PIPELINE DURATION]")
            print(f"  Start: {first_event.get('timestamp', '')}")
            print(f"  End: {last_event.get('timestamp', '')}")
            print(f"  Duration: {duration:.1f} seconds ({duration/60:.1f} minutes)")
        except:
            pass
    
    # Show all state transitions
    print(f"\n[STATE TRANSITIONS]")
    for event in state_changes:
        data = event.get('data', {})
        old_state = data.get('old_state', 'unknown')
        new_state = data.get('new_state', 'unknown')
        stage = data.get('current_stage', 'N/A')
        print(f"  {old_state} -> {new_state} (stage: {stage})")
        print(f"    {event.get('timestamp', '')}")

if __name__ == "__main__":
    main()








