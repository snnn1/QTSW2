#!/usr/bin/env python3
"""
Safely delete the massive JSONL file and prepare for clean new files
"""
import shutil
from pathlib import Path
from datetime import datetime

EVENT_LOGS_DIR = Path("automation/logs/events")
ARCHIVE_DIR = EVENT_LOGS_DIR / "archive"

def main():
    print("=" * 60)
    print("JSONL File Cleanup and Reset")
    print("=" * 60)
    print()
    
    # Find all JSONL files
    jsonl_files = list(EVENT_LOGS_DIR.glob("pipeline_*.jsonl"))
    
    if not jsonl_files:
        print("✅ No JSONL files found - already clean!")
        return
    
    # Sort by size
    jsonl_files.sort(key=lambda f: f.stat().st_size, reverse=True)
    
    print(f"Found {len(jsonl_files)} JSONL file(s):")
    print()
    
    total_size_mb = 0
    for i, file_path in enumerate(jsonl_files, 1):
        size_mb = file_path.stat().st_size / (1024 * 1024)
        total_size_mb += size_mb
        size_str = f"{size_mb:.2f} MB" if size_mb > 1 else f"{size_mb * 1024:.2f} KB"
        print(f"  {i}. {file_path.name} - {size_str}")
    
    print()
    print(f"Total size: {total_size_mb:.2f} MB")
    print()
    
    # Find the massive one
    largest = jsonl_files[0]
    largest_size_mb = largest.stat().st_size / (1024 * 1024)
    
    if largest_size_mb > 50:
        print("⚠️  Found MASSIVE file (>50MB):")
        print(f"   {largest.name} - {largest_size_mb:.2f} MB")
        print()
        print("Options:")
        print("  1. DELETE it (recommended - start fresh)")
        print("  2. ARCHIVE it (move to archive folder)")
        print("  3. CANCEL (keep everything)")
        print()
        
        choice = input("Your choice (1/2/3): ").strip()
        
        if choice == "1":
            # Delete the massive file
            print()
            print(f"🗑️  Deleting {largest.name}...")
            largest.unlink()
            print(f"✅ Deleted {largest_size_mb:.2f} MB")
            print()
            
            # Ask about other files
            if len(jsonl_files) > 1:
                print(f"There are {len(jsonl_files) - 1} other JSONL file(s).")
                choice2 = input("Delete all other JSONL files too? (yes/no): ").strip().lower()
                
                if choice2 in ['yes', 'y']:
                    deleted_count = 0
                    deleted_size = 0
                    for file_path in jsonl_files[1:]:
                        size_mb = file_path.stat().st_size / (1024 * 1024)
                        file_path.unlink()
                        deleted_count += 1
                        deleted_size += size_mb
                    print(f"✅ Deleted {deleted_count} additional file(s) ({deleted_size:.2f} MB)")
        
        elif choice == "2":
            # Archive it
            ARCHIVE_DIR.mkdir(parents=True, exist_ok=True)
            archive_path = ARCHIVE_DIR / largest.name
            print()
            print(f"📁 Archiving {largest.name}...")
            shutil.move(str(largest), str(archive_path))
            print(f"✅ Archived to: {archive_path.relative_to(Path.cwd())}")
        
        else:
            print("Cancelled - no files deleted.")
            return
    
    else:
        # All files are reasonably sized
        print("All files are reasonably sized (<50MB).")
        choice = input("Delete all JSONL files anyway to start fresh? (yes/no): ").strip().lower()
        
        if choice in ['yes', 'y']:
            deleted_count = 0
            deleted_size = 0
            for file_path in jsonl_files:
                size_mb = file_path.stat().st_size / (1024 * 1024)
                file_path.unlink()
                deleted_count += 1
                deleted_size += size_mb
            print(f"✅ Deleted {deleted_count} file(s) ({deleted_size:.2f} MB total)")
        else:
            print("Cancelled - no files deleted.")
            return
    
    print()
    print("=" * 60)
    print("✅ Cleanup Complete!")
    print("=" * 60)
    print()
    print("Next steps:")
    print("  1. Restart the backend - it should start much faster now")
    print("  2. New pipeline runs will create fresh, clean JSONL files")
    print("  3. Files will be properly sized and manageable")
    print()
    print("💡 Tip: Consider setting up automatic archiving to prevent")
    print("   files from getting too large in the future:")
    print("   python tools/manage_jsonl_files.py --archive-old 7")
    print()

if __name__ == "__main__":
    main()




