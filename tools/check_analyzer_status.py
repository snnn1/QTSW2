"""
Check Analyzer Status - Compare translated vs analyzed data dates
"""

import sys
from pathlib import Path
from datetime import datetime
from collections import defaultdict

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def extract_date_from_filename(filename: str) -> str:
    """Extract date from filename like ES_1m_2024-12-24.parquet"""
    try:
        parts = filename.split('_')
        if len(parts) >= 3:
            date_str = parts[-1].replace('.parquet', '')
            # Validate it's a date
            datetime.strptime(date_str, '%Y-%m-%d')
            return date_str
    except:
        pass
    return None

def get_latest_dates(directory: Path, pattern: str = "*.parquet") -> dict:
    """Get latest dates per instrument from directory"""
    files = list(directory.rglob(pattern))
    instrument_dates = defaultdict(list)
    
    for file in files:
        date = extract_date_from_filename(file.name)
        if date:
            # Try to extract instrument from path
            parts = file.parts
            # Look for instrument folder (usually after 'translated' or 'analyzed')
            for i, part in enumerate(parts):
                if part.upper() in ['ES', 'NQ', 'YM', 'CL', 'NG', 'GC'] and len(part) <= 4:
                    instrument = part.upper()
                    # Check if it's a session (ES1, ES2, etc.)
                    if i + 1 < len(parts) and parts[i+1].isdigit():
                        instrument = f"{part.upper()}{parts[i+1]}"
                    instrument_dates[instrument].append(date)
                    break
    
    # Get latest date per instrument
    latest = {}
    for instrument, dates in instrument_dates.items():
        if dates:
            latest[instrument] = max(dates)
    
    return latest, len(files)

def main():
    print("="*80)
    print("ANALYZER STATUS CHECK")
    print("="*80)
    
    # Check translated data
    translated_dir = qtsw2_root / "data" / "translated"
    if translated_dir.exists():
        translated_latest, translated_count = get_latest_dates(translated_dir)
        print(f"\n[TRANSLATED DATA]")
        print(f"  Total files: {translated_count}")
        if translated_latest:
            print(f"  Instruments: {len(translated_latest)}")
            print(f"  Latest dates per instrument:")
            for inst, date in sorted(translated_latest.items()):
                print(f"    {inst}: {date}")
            overall_latest = max(translated_latest.values())
            print(f"  Overall latest: {overall_latest}")
        else:
            print("  No valid files found")
    else:
        print(f"\n[TRANSLATED DATA]")
        print(f"  Directory not found: {translated_dir}")
        translated_latest = {}
    
    # Check analyzed data
    analyzed_dir = qtsw2_root / "data" / "analyzed"
    if analyzed_dir.exists():
        analyzed_latest, analyzed_count = get_latest_dates(analyzed_dir)
        print(f"\n[ANALYZED DATA]")
        print(f"  Total files: {analyzed_count}")
        if analyzed_latest:
            print(f"  Instruments: {len(analyzed_latest)}")
            print(f"  Latest dates per instrument:")
            for inst, date in sorted(analyzed_latest.items()):
                print(f"    {inst}: {date}")
            overall_latest = max(analyzed_latest.values())
            print(f"  Overall latest: {overall_latest}")
        else:
            print("  No valid files found")
    else:
        print(f"\n[ANALYZED DATA]")
        print(f"  Directory not found: {analyzed_dir}")
        analyzed_latest = {}
    
    # Compare
    print(f"\n[COMPARISON]")
    if translated_latest and analyzed_latest:
        missing = []
        outdated = []
        up_to_date = []
        
        for inst, trans_date in translated_latest.items():
            if inst not in analyzed_latest:
                missing.append((inst, trans_date))
            else:
                ana_date = analyzed_latest[inst]
                if trans_date > ana_date:
                    outdated.append((inst, trans_date, ana_date))
                else:
                    up_to_date.append((inst, trans_date, ana_date))
        
        if missing:
            print(f"  Missing instruments in analyzed data: {len(missing)}")
            for inst, date in missing[:5]:
                print(f"    {inst}: latest translated {date}, not analyzed")
            if len(missing) > 5:
                print(f"    ... and {len(missing) - 5} more")
        
        if outdated:
            print(f"  Outdated instruments: {len(outdated)}")
            for inst, trans_date, ana_date in outdated[:5]:
                print(f"    {inst}: translated {trans_date}, analyzed {ana_date} (gap: {(datetime.strptime(trans_date, '%Y-%m-%d') - datetime.strptime(ana_date, '%Y-%m-%d')).days} days)")
            if len(outdated) > 5:
                print(f"    ... and {len(outdated) - 5} more")
        
        if up_to_date:
            print(f"  Up-to-date instruments: {len(up_to_date)}")
        
        if not missing and not outdated:
            print("  ✓ All instruments are analyzed and up-to-date!")
        else:
            print(f"\n  ⚠ Analyzer needs to process {len(missing) + len(outdated)} instrument(s)")
    else:
        print("  Cannot compare - missing data")
    
    # Check recent pipeline runs
    print(f"\n[RECENT PIPELINE RUNS]")
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    if events_dir.exists():
        pipeline_files = sorted(events_dir.glob("pipeline_*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
        print(f"  Found {len(pipeline_files)} pipeline run log(s)")
        if pipeline_files:
            latest_file = pipeline_files[0]
            mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
            print(f"  Most recent: {latest_file.name}")
            print(f"  Last modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
            
            # Try to read last few lines to see status
            try:
                with open(latest_file, 'r', encoding='utf-8') as f:
                    lines = f.readlines()
                    if lines:
                        import json
                        last_event = json.loads(lines[-1])
                        print(f"  Last event: {last_event.get('stage')}/{last_event.get('event')} - {last_event.get('msg', '')[:60]}")
            except:
                pass

if __name__ == "__main__":
    main()








