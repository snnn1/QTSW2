#!/usr/bin/env python3
"""
Extract all event types from robot codebase for assessment.
"""
import re
from pathlib import Path
from collections import defaultdict

def extract_event_types_from_file(file_path: Path) -> set:
    """Extract event type strings from a C# file."""
    event_types = set()
    try:
        content = file_path.read_text(encoding='utf-8')
        
        # Pattern 1: eventType: "EVENT_NAME"
        pattern1 = r'eventType:\s*["\']([A-Z_][A-Z0-9_]*?)["\']'
        matches = re.findall(pattern1, content, re.IGNORECASE)
        event_types.update(matches)
        
        # Pattern 2: "EVENT_NAME" in eventType assignments
        pattern2 = r'["\']([A-Z_][A-Z0-9_]{3,})["\']'
        # Look for common patterns around event types
        lines = content.split('\n')
        for i, line in enumerate(lines):
            if 'eventType' in line.lower() or 'event_type' in line.lower() or '@event' in line:
                matches = re.findall(pattern2, line)
                event_types.update(matches)
        
        # Pattern 3: Event type constants (EVENT_NAME = "...")
        pattern3 = r'([A-Z_][A-Z0-9_]+)\s*=\s*["\']'
        matches = re.findall(pattern3, content)
        event_types.update(matches)
        
        # Pattern 4: Direct string literals that look like event names
        # Look for lines with event-related keywords
        for line in lines:
            if any(keyword in line.lower() for keyword in ['event', 'logevent', 'write(', 'robotevents']):
                # Find quoted strings that are ALL_CAPS_WITH_UNDERSCORES
                matches = re.findall(r'["\']([A-Z][A-Z0-9_]{5,})["\']', line)
                for match in matches:
                    # Filter out things that look like event names
                    if '_' in match and match.isupper():
                        event_types.add(match)
        
    except Exception as e:
        print(f"Error reading {file_path}: {e}")
    
    return event_types

def main():
    project_root = Path(__file__).parent.parent
    robot_dir = project_root / "modules" / "robot"
    ninja_dir = project_root / "RobotCore_For_NinjaTrader"
    
    all_events = defaultdict(set)
    
    # Scan C# files
    for cs_file in list(robot_dir.rglob("*.cs")) + list(ninja_dir.rglob("*.cs")):
        if "bin" in str(cs_file) or "obj" in str(cs_file):
            continue
        events = extract_event_types_from_file(cs_file)
        if events:
            all_events[cs_file.relative_to(project_root)].update(events)
    
    # Print summary
    unique_events = set()
    for file_events in all_events.values():
        unique_events.update(file_events)
    
    print(f"Found {len(unique_events)} unique event types across {len(all_events)} files")
    print("\nAll event types (sorted):")
    for event in sorted(unique_events):
        print(f"  {event}")
    
    print("\n\nEvent types by file:")
    for file_path, events in sorted(all_events.items()):
        if events:
            print(f"\n{file_path}:")
            for event in sorted(events):
                print(f"  {event}")

if __name__ == "__main__":
    main()
