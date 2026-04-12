#!/usr/bin/env python3
"""
Reduce JSONL file size by filtering to only important events
Removes verbose/redundant events while keeping critical information
"""
import json
import shutil
from pathlib import Path
from datetime import datetime
from collections import Counter

EVENT_LOGS_DIR = Path("automation/logs/events")

# Events to KEEP (important events)
KEEP_EVENTS = {
    'start',           # Pipeline/stage starts
    'success',         # Successful completions
    'failed',          # Failures
    'state_change',    # State changes
    'error',           # Errors
    'log',             # Important log messages
}

# Events to REMOVE (verbose/redundant)
REMOVE_EVENTS = {
    'metric',          # Frequent metrics (can be regenerated)
    'progress',        # Progress updates
    'heartbeat',       # Heartbeat messages
}

# Stages to always keep (even if event type is normally filtered)
ALWAYS_KEEP_STAGES = {
    'pipeline',        # Pipeline-level events
    'scheduler',       # Scheduler events
}

def reduce_file(input_file: Path, output_file: Path = None, keep_ratio: float = 0.1):
    """
    Reduce JSONL file by keeping only important events
    
    Args:
        input_file: Input JSONL file
        output_file: Output file (default: input_file with .reduced suffix)
        keep_ratio: Target ratio of events to keep (0.1 = keep 10%)
    """
    if output_file is None:
        output_file = input_file.parent / f"{input_file.stem}.reduced{input_file.suffix}"
    
    print("=" * 60)
    print(f"Reducing: {input_file.name}")
    print("=" * 60)
    
    input_size_mb = input_file.stat().st_size / (1024 * 1024)
    print(f"Input size: {input_size_mb:.2f} MB")
    print()
    
    # Statistics
    total_lines = 0
    kept_lines = 0
    removed_lines = 0
    event_counts = Counter()
    kept_event_counts = Counter()
    
    print("Processing file...")
    print("  Strategy: Keep important events (start, success, failed, state_change, error, log)")
    print("  Remove: Frequent metrics and progress updates")
    print()
    
    with open(input_file, 'r', encoding='utf-8') as f_in:
        with open(output_file, 'w', encoding='utf-8') as f_out:
            for line_num, line in enumerate(f_in, 1):
                if not line.strip():
                    continue
                
                total_lines += 1
                
                try:
                    event = json.loads(line)
                    stage = event.get('stage', 'unknown')
                    event_type = event.get('event', 'unknown')
                    
                    event_counts[event_type] += 1
                    
                    # Decision: Keep or remove?
                    keep = False
                    
                    # Always keep pipeline/scheduler events
                    if stage in ALWAYS_KEEP_STAGES:
                        keep = True
                    
                    # Keep important event types
                    elif event_type in KEEP_EVENTS:
                        keep = True
                    
                    # Remove verbose events
                    elif event_type in REMOVE_EVENTS:
                        keep = False
                    
                    # For other events, keep a small sample (1%)
                    else:
                        keep = (line_num % 100 == 0)  # Keep 1% of unknown events
                    
                    if keep:
                        f_out.write(line)
                        kept_lines += 1
                        kept_event_counts[event_type] += 1
                    else:
                        removed_lines += 1
                    
                    # Progress indicator
                    if line_num % 100000 == 0:
                        reduction = (1 - kept_lines / total_lines) * 100 if total_lines > 0 else 0
                        print(f"  Processed {line_num:,} lines... (kept {kept_lines:,}, {reduction:.1f}% reduction)")
                
                except json.JSONDecodeError:
                    # Skip invalid JSON
                    removed_lines += 1
                    continue
    
    output_size_mb = output_file.stat().st_size / (1024 * 1024)
    reduction_pct = (1 - output_size_mb / input_size_mb) * 100 if input_size_mb > 0 else 0
    
    print()
    print("=" * 60)
    print("Results")
    print("=" * 60)
    print(f"Total lines processed: {total_lines:,}")
    print(f"Lines kept: {kept_lines:,} ({kept_lines/total_lines*100:.2f}%)")
    print(f"Lines removed: {removed_lines:,} ({removed_lines/total_lines*100:.2f}%)")
    print()
    print(f"Input size:  {input_size_mb:.2f} MB")
    print(f"Output size: {output_size_mb:.2f} MB")
    print(f"Reduction:   {reduction_pct:.1f}%")
    print()
    
    print("Top event types in original file:")
    for event_type, count in event_counts.most_common(10):
        pct = (count / total_lines) * 100
        kept = kept_event_counts.get(event_type, 0)
        kept_pct = (kept / count * 100) if count > 0 else 0
        print(f"  {event_type:15} {count:>10,} ({pct:5.1f}%) → kept {kept:>8,} ({kept_pct:5.1f}%)")
    
    print()
    print(f"✅ Reduced file saved to: {output_file.name}")
    print()
    print("Next steps:")
    print(f"  1. Review the reduced file: {output_file.name}")
    print(f"  2. If satisfied, replace original:")
    print(f"     - Backup: shutil.move('{input_file.name}', '{input_file.name}.backup')")
    print(f"     - Replace: shutil.move('{output_file.name}', '{input_file.name}')")
    print(f"  3. Or archive the original and keep the reduced version")
    
    return output_file

def main():
    import sys
    
    # Find largest file
    jsonl_files = list(EVENT_LOGS_DIR.glob("pipeline_*.jsonl"))
    if not jsonl_files:
        print("No JSONL files found")
        return
    
    jsonl_files.sort(key=lambda f: f.stat().st_size, reverse=True)
    largest_file = jsonl_files[0]
    
    size_mb = largest_file.stat().st_size / (1024 * 1024)
    
    print("=" * 60)
    print("JSONL File Reducer")
    print("=" * 60)
    print(f"\nLargest file: {largest_file.name}")
    print(f"Size: {size_mb:.2f} MB")
    print()
    
    if size_mb < 10:
        print("File is already reasonably sized. No reduction needed.")
        return
    
    # Ask for confirmation
    print("This will create a REDUCED version of the file by:")
    print("  ✅ Keeping: start, success, failed, state_change, error, log events")
    print("  ❌ Removing: metric, progress, heartbeat events (frequent/verbose)")
    print("  ✅ Always keeping: pipeline and scheduler events")
    print()
    print("The original file will NOT be modified.")
    print("A new .reduced.jsonl file will be created.")
    print()
    
    response = input("Continue? (yes/no): ").strip().lower()
    if response not in ['yes', 'y']:
        print("Cancelled.")
        return
    
    # Reduce the file
    output_file = reduce_file(largest_file)
    
    print()
    print("=" * 60)
    print("✅ Reduction complete!")
    print("=" * 60)

if __name__ == "__main__":
    main()




