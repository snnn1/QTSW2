"""
Check Today's Files - Verify raw and translated files for today
"""

import sys
from pathlib import Path
from datetime import datetime

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    today = datetime.now().strftime('%Y-%m-%d')
    print("="*80)
    print(f"TODAY'S FILES CHECK - {today}")
    print("="*80)
    
    # Check raw files
    raw_dir = qtsw2_root / "data" / "raw"
    raw_files = list(raw_dir.rglob(f'*{today}*.csv'))
    
    print(f"\n[RAW FILES FOR TODAY]")
    print(f"  Count: {len(raw_files)}")
    if raw_files:
        print(f"  Files:")
        for f in sorted(raw_files):
            mtime = datetime.fromtimestamp(f.stat().st_mtime)
            size_kb = f.stat().st_size / 1024
            print(f"    {f.name}")
            print(f"      Size: {size_kb:.2f} KB")
            print(f"      Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    else:
        print(f"  [WARNING] No raw files found for today!")
    
    # Check translated files
    translated_dir = qtsw2_root / "data" / "translated"
    translated_files = list(translated_dir.rglob(f'*{today}*.parquet'))
    
    print(f"\n[TRANSLATED FILES FOR TODAY]")
    print(f"  Count: {len(translated_files)}")
    if translated_files:
        print(f"  Files:")
        for f in sorted(translated_files):
            mtime = datetime.fromtimestamp(f.stat().st_mtime)
            size_kb = f.stat().st_size / 1024
            print(f"    {f.name}")
            print(f"      Size: {size_kb:.2f} KB")
            print(f"      Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"      Path: {f.relative_to(qtsw2_root)}")
    else:
        print(f"  [WARNING] No translated files found for today!")
    
    # Compare
    print(f"\n[COMPARISON]")
    if raw_files and translated_files:
        raw_instruments = sorted(set([f.stem.split('_')[0].upper() for f in raw_files]))
        trans_instruments = sorted(set([f.stem.split('_')[0].upper() for f in translated_files]))
        
        if raw_instruments == trans_instruments:
            print(f"  [OK] All {len(raw_instruments)} instruments have both raw and translated files")
        else:
            missing_raw = set(trans_instruments) - set(raw_instruments)
            missing_trans = set(raw_instruments) - set(trans_instruments)
            if missing_raw:
                print(f"  [WARNING] Translated but no raw: {missing_raw}")
            if missing_trans:
                print(f"  [WARNING] Raw but not translated: {missing_trans}")
        
        # Check timing
        if raw_files and translated_files:
            latest_raw = max([f.stat().st_mtime for f in raw_files])
            latest_trans = max([f.stat().st_mtime for f in translated_files])
            
            print(f"\n  Timing:")
            print(f"    Latest raw file: {datetime.fromtimestamp(latest_raw).strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"    Latest translated: {datetime.fromtimestamp(latest_trans).strftime('%Y-%m-%d %H:%M:%S')}")
            
            if latest_trans > latest_raw:
                diff = latest_trans - latest_raw
                print(f"    [OK] Translated files are newer (translated {diff/60:.1f} minutes after raw files)")
            elif latest_trans < latest_raw:
                diff = latest_raw - latest_trans
                print(f"    [WARNING] Raw files are newer (raw files {diff/60:.1f} minutes newer than translated)")
            else:
                print(f"    [INFO] Files have same timestamp")
    
    # Check if they're being analyzed
    print(f"\n[ANALYZER STATUS]")
    analyzer_temp = qtsw2_root / "data" / "analyzer_temp" / today
    if analyzer_temp.exists():
        analyzer_files = list(analyzer_temp.glob("*.parquet"))
        print(f"  Analyzer temp folder exists: {analyzer_temp}")
        print(f"  Files in analyzer temp: {len(analyzer_files)}")
        if analyzer_files:
            print(f"    Files:")
            for f in sorted(analyzer_files)[:5]:
                print(f"      {f.name}")
    else:
        print(f"  Analyzer temp folder does not exist (may have been processed and cleaned up)")
    
    # Check analyzed output
    analyzed_dir = qtsw2_root / "data" / "analyzed"
    # Check if today's data is in the December 2025 files
    print(f"\n[ANALYZED OUTPUT]")
    print(f"  Today's data should be in: ES1_an_2025_12.parquet, ES2_an_2025_12.parquet, etc.")
    print(f"  (Monthly files are updated when today's data is merged)")

if __name__ == "__main__":
    main()









