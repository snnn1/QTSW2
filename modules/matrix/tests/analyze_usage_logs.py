"""
Usage Log Analysis Script

Analyzes logs/matrix_feature_usage.jsonl to identify:
- Unused subsystems
- No-impact subsystems (run but never change output)
- Invalid-data-only subsystems
"""

import json
from pathlib import Path
from collections import defaultdict
from typing import Dict, List, Set

LOG_FILE = Path("logs/matrix_feature_usage.jsonl")


def analyze_usage_logs():
    """Analyze usage logs and categorize subsystems."""
    
    if not LOG_FILE.exists():
        print(f"Usage log file not found: {LOG_FILE}")
        print("Run matrix operations first to generate usage logs.")
        return
    
    # Read all events
    events = []
    with open(LOG_FILE, 'r') as f:
        for line in f:
            if line.strip():
                try:
                    events.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
    
    if not events:
        print("No events found in usage log.")
        return
    
    # Categorize subsystems
    subsystem_stats = defaultdict(lambda: {
        'invocations': 0,
        'repair_ran': 0,
        'default_fill_ran': 0,
        'rows_changed': 0,
        'modes': set()
    })
    
    for event in events:
        subsystem = event.get('subsystem', 'unknown')
        metrics = event.get('metrics', {})
        mode = event.get('mode', 'unknown')
        
        stats = subsystem_stats[subsystem]
        stats['invocations'] += 1
        stats['modes'].add(mode)
        
        if metrics.get('repair_ran', False):
            stats['repair_ran'] += 1
        if metrics.get('default_fill_ran', False):
            stats['default_fill_ran'] += 1
        
        # Track if rows changed
        rows_in = metrics.get('rows_in', 0)
        rows_out = metrics.get('rows_out', 0)
        if rows_in != rows_out:
            stats['rows_changed'] += 1
    
    # Categorize subsystems
    unused = []
    no_impact = []
    invalid_data_only = []
    
    for subsystem, stats in subsystem_stats.items():
        if stats['invocations'] == 0:
            unused.append(subsystem)
        elif stats['repair_ran'] == 0 and stats['default_fill_ran'] == 0 and stats['rows_changed'] == 0:
            no_impact.append(subsystem)
        elif stats['repair_ran'] > 0 or stats['default_fill_ran'] > 0:
            invalid_data_only.append(subsystem)
    
    # Print results
    print("=" * 80)
    print("USAGE LOG ANALYSIS")
    print("=" * 80)
    print(f"\nTotal events: {len(events)}")
    print(f"Total subsystems: {len(subsystem_stats)}")
    
    print("\n--- Unused Subsystems ---")
    if unused:
        for s in unused:
            print(f"  - {s}")
    else:
        print("  (none)")
    
    print("\n--- No-Impact Subsystems ---")
    if no_impact:
        for s in no_impact:
            stats = subsystem_stats[s]
            print(f"  - {s} (invoked {stats['invocations']} times, no output changes)")
    else:
        print("  (none)")
    
    print("\n--- Invalid-Data-Only Subsystems ---")
    if invalid_data_only:
        for s in invalid_data_only:
            stats = subsystem_stats[s]
            print(f"  - {s} (repair_ran: {stats['repair_ran']}, default_fill_ran: {stats['default_fill_ran']})")
    else:
        print("  (none)")
    
    print("\n--- All Subsystem Stats ---")
    for subsystem, stats in sorted(subsystem_stats.items()):
        print(f"\n{subsystem}:")
        print(f"  Invocations: {stats['invocations']}")
        print(f"  Modes: {', '.join(sorted(stats['modes']))}")
        print(f"  Repair ran: {stats['repair_ran']}")
        print(f"  Default fill ran: {stats['default_fill_ran']}")
        print(f"  Rows changed: {stats['rows_changed']}")
    
    return {
        'unused': unused,
        'no_impact': no_impact,
        'invalid_data_only': invalid_data_only,
        'stats': dict(subsystem_stats)
    }


if __name__ == "__main__":
    analyze_usage_logs()
