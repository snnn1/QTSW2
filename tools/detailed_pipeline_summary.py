"""
Detailed Pipeline Summary - Show comprehensive details of latest pipeline run
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
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    
    if not pipeline_files:
        print("No pipeline files found")
        return
    
    latest_file = pipeline_files[0]
    
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
    
    print("="*80)
    print("MOST RECENT PIPELINE RUN - DETAILED SUMMARY")
    print("="*80)
    
    run_id = events[0].get('run_id', 'unknown') if events else 'unknown'
    print(f"\nRun ID: {run_id}")
    print(f"Log File: {latest_file.name}")
    
    # Pipeline start
    pipeline_start = [e for e in events if e.get('stage') == 'pipeline' and e.get('event') == 'start']
    if pipeline_start:
        start_event = pipeline_start[0]
        ts = start_event.get('timestamp', '')
        manual = start_event.get('data', {}).get('manual', False)
        print(f"\n[PIPELINE START]")
        print(f"  Time: {ts}")
        print(f"  Type: {'Manual' if manual else 'Scheduled'}")
    
    # Translator stage
    translator_events = [e for e in events if e.get('stage') == 'translator']
    if translator_events:
        skipped = [e for e in translator_events if e.get('event') == 'skipped']
        success = [e for e in translator_events if e.get('event') == 'success']
        
        print(f"\n[TRANSLATOR STAGE]")
        if skipped:
            data = skipped[0].get('data', {})
            print(f"  Status: SKIPPED (all files already translated)")
            print(f"  Files written: {data.get('files_written', 0)}")
            print(f"  Files skipped: {data.get('files_skipped', 0)}")
            print(f"  Files failed: {data.get('files_failed', 0)}")
        elif success:
            data = success[0].get('data', {})
            print(f"  Status: SUCCESS")
            print(f"  Files written: {data.get('files_written', 0)}")
            print(f"  Files skipped: {data.get('files_skipped', 0)}")
    
    # Analyzer stage
    analyzer_events = [e for e in events if e.get('stage') == 'analyzer']
    if analyzer_events:
        file_starts = [e for e in analyzer_events if e.get('event') == 'file_start']
        file_finish = [e for e in analyzer_events if e.get('event') == 'file_finish']
        success = [e for e in analyzer_events if e.get('event') == 'success']
        
        print(f"\n[ANALYZER STAGE]")
        if file_starts:
            instruments = sorted(set([e.get('data', {}).get('instrument', '') for e in file_starts]))
            print(f"  Instruments processed: {len(instruments)}")
            print(f"  Instruments: {', '.join(instruments)}")
        
        if file_finish:
            print(f"\n  Performance by Instrument:")
            for event in sorted(file_finish, key=lambda x: x.get('data', {}).get('finish_timestamp', 0)):
                data = event.get('data', {})
                inst = data.get('instrument', '?')
                duration = data.get('duration_seconds', 0)
                status = data.get('status', '?')
                print(f"    {inst}: {duration:.1f}s - {status}")
        
        if success:
            data = success[0].get('data', {})
            total_time = data.get('total_time_minutes', 0)
            successful = data.get('successful', 0)
            failed = data.get('failed', 0)
            print(f"\n  Summary:")
            print(f"    Total time: {total_time:.2f} minutes")
            print(f"    Successful: {successful}")
            print(f"    Failed: {failed}")
    
    # Merger stage
    merger_events = [e for e in events if e.get('stage') == 'merger']
    if merger_events:
        success = [e for e in merger_events if e.get('event') == 'success']
        print(f"\n[MERGER STAGE]")
        if success:
            print(f"  Status: SUCCESS")
            print(f"  Message: {success[0].get('msg', '')}")
    
    # Final state
    state_changes = [e for e in events if e.get('stage') == 'pipeline' and e.get('event') == 'state_change']
    if state_changes:
        final_state = state_changes[-1]
        data = final_state.get('data', {})
        new_state = data.get('new_state', 'unknown')
        print(f"\n[FINAL STATE]")
        print(f"  Status: {new_state.upper()}")
        print(f"  Timestamp: {final_state.get('timestamp', '')}")
    
    # Duration
    if events:
        first_event = events[0]
        last_event = events[-1]
        try:
            first_ts = datetime.fromisoformat(first_event.get('timestamp', '').replace('Z', '+00:00'))
            last_ts = datetime.fromisoformat(last_event.get('timestamp', '').replace('Z', '+00:00'))
            duration = (last_ts - first_ts).total_seconds()
            print(f"\n[PIPELINE DURATION]")
            print(f"  Total time: {duration:.1f} seconds ({duration/60:.2f} minutes)")
            print(f"  Start: {first_event.get('timestamp', '')}")
            print(f"  End: {last_event.get('timestamp', '')}")
        except:
            pass
    
    print("\n" + "="*80)

if __name__ == "__main__":
    main()








