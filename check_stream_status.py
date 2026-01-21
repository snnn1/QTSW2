#!/usr/bin/env python3
"""Check stream initialization status"""
import json
from pathlib import Path

# Check timetable
timetable_path = Path("data/timetable/timetable_current.json")
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    streams = timetable.get('streams', [])
    enabled = [s for s in streams if s.get('enabled', False)]
    
    print("="*80)
    print("TIMETABLE STATUS")
    print("="*80)
    print(f"Total streams: {len(streams)}")
    print(f"Enabled streams: {len(enabled)}")
    print(f"Trading date: {timetable.get('trading_date', 'N/A')}")
    print(f"As of: {timetable.get('as_of', 'N/A')}")
    
    if enabled:
        print(f"\nEnabled streams:")
        for s in enabled[:10]:
            print(f"  - {s.get('instrument', 'N/A')} {s.get('session', 'N/A')} {s.get('slot_time', 'N/A')}")
    else:
        print("\n[WARN] No enabled streams in timetable!")
else:
    print("[ERROR] Timetable file not found!")

# Check for stream journal files
journal_dir = Path("logs/robot/journal")
if journal_dir.exists():
    journal_files = list(journal_dir.glob("*.json"))
    print(f"\n" + "="*80)
    print("STREAM JOURNALS")
    print("="*80)
    print(f"Journal files found: {len(journal_files)}")
    
    # Check recent journals
    recent_journals = sorted(journal_files, key=lambda p: p.stat().st_mtime, reverse=True)[:10]
    print(f"\nRecent journal files:")
    for jf in recent_journals:
        try:
            journal_data = json.loads(jf.read_text())
            trading_date = journal_data.get('trading_date', 'N/A')
            stream = journal_data.get('stream', 'N/A')
            committed = journal_data.get('committed', False)
            state = journal_data.get('state', 'N/A')
            print(f"  - {jf.name} | Stream: {stream} | Date: {trading_date} | State: {state} | Committed: {committed}")
        except:
            print(f"  - {jf.name} | (error reading)")

print("\n" + "="*80)
