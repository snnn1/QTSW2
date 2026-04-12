#!/usr/bin/env python3
"""Check CL2 execution journals for position issues"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta

today = datetime.now(timezone.utc)
yesterday = today - timedelta(days=1)

# Check execution journals
journal_dir = Path("data/execution_journals")
if journal_dir.exists():
    print("=" * 100)
    print("CL2 EXECUTION JOURNAL CHECK")
    print("=" * 100)
    print()
    
    cl2_journals = []
    for journal_file in journal_dir.glob("*CL2*.json"):
        try:
            with open(journal_file, 'r', encoding='utf-8') as f:
                journal = json.load(f)
                ts_str = journal.get('entry_filled_at') or journal.get('entry_submitted_at', '')
                if ts_str:
                    try:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= yesterday:
                            journal['_file'] = journal_file.name
                            cl2_journals.append(journal)
                    except:
                        pass
        except:
            pass
    
    print(f"Found {len(cl2_journals)} CL2 execution journals from last 24 hours")
    print()
    
    for journal in sorted(cl2_journals, key=lambda x: x.get('entry_filled_at', '') or x.get('entry_submitted_at', ''))[-10:]:
        intent_id = journal.get('intent_id', 'N/A')[:30]
        instrument = journal.get('instrument', 'N/A')
        direction = journal.get('direction', 'N/A')
        entry_filled = journal.get('entry_filled', False)
        entry_filled_qty = journal.get('entry_filled_quantity_total', 0)
        exit_filled_qty = journal.get('exit_filled_quantity_total', 0)
        fill_price = journal.get('fill_price') or journal.get('actual_fill_price')
        
        print(f"Intent: {intent_id}...")
        print(f"  Instrument: {instrument}")
        print(f"  Direction: {direction}")
        print(f"  Entry Filled: {entry_filled}")
        print(f"  Entry Filled Qty: {entry_filled_qty}")
        print(f"  Exit Filled Qty: {exit_filled_qty}")
        print(f"  Expected Position: {entry_filled_qty - exit_filled_qty}")
        print(f"  Fill Price: {fill_price}")
        print()
