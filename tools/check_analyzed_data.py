"""
Check Analyzed Data - Show latest data in analyzed folder
"""

import sys
from pathlib import Path
from datetime import datetime
from collections import defaultdict

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def main():
    print("="*80)
    print("ANALYZED DATA CHECK")
    print("="*80)
    
    analyzed_dir = qtsw2_root / "data" / "analyzed"
    
    if not analyzed_dir.exists():
        print(f"\n[ERROR] Analyzed directory not found: {analyzed_dir}")
        return
    
    # Find all parquet files
    files = list(analyzed_dir.rglob("*.parquet"))
    print(f"\n[OVERVIEW]")
    print(f"  Total analyzed files: {len(files)}")
    
    # Group by instrument/session
    instrument_data = defaultdict(lambda: defaultdict(list))
    
    for file in files:
        # Path structure: data/analyzed/{instrument}{session}/{year}/{file}
        # Example: data/analyzed/CL1/2025/CL1_an_2025_12.parquet
        parts = file.parts
        try:
            # Find the instrument folder (e.g., CL1, ES2)
            for i, part in enumerate(parts):
                if part == "analyzed" and i + 1 < len(parts):
                    inst_folder = parts[i + 1]
                    if i + 2 < len(parts):
                        year = parts[i + 2]
                        
                        # Extract instrument and session
                        if len(inst_folder) >= 2 and inst_folder[-1].isdigit():
                            instrument = inst_folder[:-1]
                            session = inst_folder[-1]
                        else:
                            instrument = inst_folder
                            session = "?"
                        
                        # Extract month from filename
                        filename = file.stem
                        if "_an_" in filename:
                            parts_name = filename.split("_an_")
                            if len(parts_name) >= 2:
                                year_month = parts_name[1]
                                if "_" in year_month:
                                    year_part, month_part = year_month.split("_")
                                else:
                                    year_part = year
                                    month_part = year_month[-2:] if len(year_month) >= 2 else "??"
                        else:
                            year_part = year
                            month_part = "??"
                        
                        mtime = datetime.fromtimestamp(file.stat().st_mtime)
                        
                        instrument_data[instrument][session].append({
                            "file": file,
                            "year": year_part,
                            "month": month_part,
                            "filename": file.name,
                            "mtime": mtime,
                            "path": str(file.relative_to(qtsw2_root))
                        })
                        break
        except Exception as e:
            continue
    
    # Display by instrument
    print(f"\n[LATEST DATA BY INSTRUMENT]")
    print("="*80)
    
    for instrument in sorted(instrument_data.keys()):
        print(f"\n{instrument}:")
        for session in sorted(instrument_data[instrument].keys()):
            files_list = instrument_data[instrument][session]
            if not files_list:
                continue
            
            # Group by year
            by_year = defaultdict(list)
            for f in files_list:
                by_year[f["year"]].append(f)
            
            print(f"  Session {session}:")
            for year in sorted(by_year.keys(), reverse=True):
                year_files = by_year[year]
                # Sort by month
                year_files.sort(key=lambda x: (x["month"], x["mtime"]), reverse=True)
                
                months = sorted(set([f["month"] for f in year_files]))
                latest = max(year_files, key=lambda x: x["mtime"])
                
                print(f"    {year}: {len(year_files)} file(s)")
                print(f"      Months available: {', '.join(months)}")
                print(f"      Latest file: {latest['filename']}")
                print(f"      Last modified: {latest['mtime'].strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Find overall latest
    print(f"\n[OVERALL LATEST DATA]")
    print("="*80)
    
    all_latest = []
    for instrument in instrument_data.keys():
        for session in instrument_data[instrument].keys():
            files_list = instrument_data[instrument][session]
            if files_list:
                latest = max(files_list, key=lambda x: x["mtime"])
                all_latest.append((latest, instrument, session))
    
    if all_latest:
        # Sort by modification time
        all_latest.sort(key=lambda x: x[0]["mtime"], reverse=True)
        
        print(f"\nMost recently modified files:")
        for latest, instrument, session in all_latest[:10]:
            print(f"  {instrument}{session}: {latest['filename']}")
            print(f"    Modified: {latest['mtime'].strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"    Path: {latest['path']}")
        
        # Find latest year/month
        latest_year = max([f["year"] for f, _, _ in all_latest])
        latest_files_2025 = [f for f, inst, sess in all_latest if f["year"] == latest_year]
        if latest_files_2025:
            latest_month = max([f["month"] for f in latest_files_2025])
            print(f"\n  Latest data period: {latest_year}-{latest_month}")
            print(f"  Latest modification: {max([f['mtime'] for f in latest_files_2025]).strftime('%Y-%m-%d %H:%M:%S')}")

if __name__ == "__main__":
    main()









