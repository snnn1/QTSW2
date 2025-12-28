"""
Compare Manual vs Scheduled Pipeline Runs
"""

import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

qtsw2_root = Path(__file__).parent.parent

def analyze_pipeline_runs():
    """Analyze pipeline runs to find differences between manual and scheduled"""
    print("="*80)
    print("MANUAL VS SCHEDULED PIPELINE RUNS COMPARISON")
    print("="*80)
    
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    if not events_dir.exists():
        print("Events directory not found")
        return
    
    pipeline_files = sorted(
        events_dir.glob("pipeline_*.jsonl"),
        key=lambda p: p.stat().st_mtime,
        reverse=True
    )
    
    if not pipeline_files:
        print("No pipeline runs found")
        return
    
    manual_runs = []
    scheduled_runs = []
    
    print(f"\nAnalyzing {len(pipeline_files)} pipeline runs...\n")
    
    for pipeline_file in pipeline_files[:20]:  # Analyze last 20 runs
        with open(pipeline_file, 'r') as f:
            events = [json.loads(l) for l in f if l.strip()]
        
        # Find manual_requested or scheduler events
        is_manual = False
        is_scheduled = False
        run_id = None
        
        for event in events:
            if event.get('stage') == 'pipeline':
                if event.get('event') == 'manual_requested':
                    is_manual = True
                    run_id = event.get('run_id')
                elif event.get('stage') == 'scheduler' or 'scheduled' in event.get('msg', '').lower():
                    is_scheduled = True
                    run_id = event.get('run_id')
            
            if not run_id and event.get('run_id'):
                run_id = event.get('run_id')
        
        if is_manual:
            manual_runs.append((pipeline_file.name, run_id, events))
        elif is_scheduled:
            scheduled_runs.append((pipeline_file.name, run_id, events))
    
    print(f"[RUNS FOUND]")
    print(f"  Manual runs: {len(manual_runs)}")
    print(f"  Scheduled runs: {len(scheduled_runs)}")
    
    if manual_runs:
        print(f"\n[MANUAL RUNS - Latest]")
        for filename, run_id, events in manual_runs[:3]:
            mtime = datetime.fromtimestamp(Path(events_dir / filename).stat().st_mtime)
            print(f"  {filename} (Run ID: {run_id[:8] if run_id else 'None'}, {mtime.strftime('%Y-%m-%d %H:%M:%S')})")
            
            # Find key events
            manual_event = [e for e in events if e.get('event') == 'manual_requested']
            state_changes = [e for e in events if e.get('event') == 'state_change']
            
            if manual_event:
                print(f"    Manual requested event: {manual_event[0].get('timestamp')}")
            print(f"    State changes: {len(state_changes)}")
            if state_changes:
                print(f"      First: {state_changes[0].get('data', {}).get('old_state')} -> {state_changes[0].get('data', {}).get('new_state')}")
                print(f"      Last: {state_changes[-1].get('data', {}).get('old_state')} -> {state_changes[-1].get('data', {}).get('new_state')}")
    
    if scheduled_runs:
        print(f"\n[SCHEDULED RUNS - Latest]")
        for filename, run_id, events in scheduled_runs[:3]:
            mtime = datetime.fromtimestamp(Path(events_dir / filename).stat().st_mtime)
            print(f"  {filename} (Run ID: {run_id[:8] if run_id else 'None'}, {mtime.strftime('%Y-%m-%d %H:%M:%S')})")
            
            # Find scheduler events
            scheduler_events = [e for e in events if e.get('stage') == 'scheduler']
            state_changes = [e for e in events if e.get('event') == 'state_change']
            
            if scheduler_events:
                print(f"    Scheduler events: {len(scheduler_events)}")
                for se in scheduler_events[:2]:
                    print(f"      {se.get('event')}: {se.get('msg', '')[:60]}")
            print(f"    State changes: {len(state_changes)}")
            if state_changes:
                print(f"      First: {state_changes[0].get('data', {}).get('old_state')} -> {state_changes[0].get('data', {}).get('new_state')}")
                print(f"      Last: {state_changes[-1].get('data', {}).get('old_state')} -> {state_changes[-1].get('data', {}).get('new_state')}")

def explain_differences():
    """Explain the differences between manual and scheduled runs"""
    print("\n" + "="*80)
    print("KEY DIFFERENCES: MANUAL VS SCHEDULED RUNS")
    print("="*80)
    
    print("\n[1. TRIGGER SOURCE]")
    print("  Manual: Triggered by user clicking 'Run Pipeline Now' button")
    print("  Scheduled: Triggered by Windows Task Scheduler (runs every 15 minutes)")
    
    print("\n[2. API CALL]")
    print("  Manual: POST /api/pipeline/start with {manual: true}")
    print("  Scheduled: Windows Task Scheduler calls run_pipeline_standalone.py")
    
    print("\n[3. EVENT LOGGING]")
    print("  Manual: Emits 'manual_requested' event to EventBus")
    print("  Scheduled: Emits 'scheduler/start' event to EventBus")
    
    print("\n[4. EXECUTION PATH]")
    print("  Manual:")
    print("    - Dashboard UI -> POST /api/pipeline/start -> orchestrator.start_pipeline(manual=True)")
    print("    - Runs immediately, no delay")
    print("  Scheduled:")
    print("    - Windows Task Scheduler -> run_pipeline_standalone.py -> orchestrator.start_pipeline(manual=False)")
    print("    - Runs on schedule (every 15 minutes)")
    
    print("\n[5. METADATA]")
    print("  Manual: run_ctx.metadata.manual = True")
    print("  Scheduled: run_ctx.metadata.manual = False")
    
    print("\n[6. PIPELINE EXECUTION]")
    print("  Both run the EXACT SAME pipeline stages:")
    print("    - Translator")
    print("    - Analyzer")
    print("    - Merger")
    print("  The 'manual' flag only affects event logging, not execution logic")
    
    print("\n[7. COMPLETION EVENTS]")
    print("  Manual: No special completion event")
    print("  Scheduled: Emits 'scheduler/success' or 'scheduler/failed' event")
    
    print("\n[CONCLUSION]")
    print("  The pipeline execution is IDENTICAL for both manual and scheduled runs.")
    print("  The only differences are:")
    print("    - How they are triggered (UI vs Task Scheduler)")
    print("    - Event metadata (manual_requested vs scheduler events)")
    print("    - Timing (immediate vs scheduled)")

def main():
    analyze_pipeline_runs()
    explain_differences()
    
    print("\n" + "="*80)
    print("HOW TO CHECK IF A RUN IS MANUAL OR SCHEDULED")
    print("="*80)
    print("\nIn the pipeline events, look for:")
    print("  - Manual: event with stage='pipeline', event='manual_requested'")
    print("  - Scheduled: event with stage='scheduler', event='start' or 'scheduled_run_started'")

if __name__ == "__main__":
    main()

