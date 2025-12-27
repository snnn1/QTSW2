#!/usr/bin/env python3
"""
Manage large JSONL event log files
Options:
- Split large files by date or size
- Archive old files
- Compress files
- Clean up old events
"""
import json
import gzip
import shutil
from pathlib import Path
from datetime import datetime, timedelta
from typing import List, Tuple
import argparse

QTSW2_ROOT = Path(__file__).parent.parent
EVENT_LOGS_DIR = QTSW2_ROOT / "automation" / "logs" / "events"
ARCHIVE_DIR = EVENT_LOGS_DIR / "archive"
COMPRESSED_DIR = EVENT_LOGS_DIR / "compressed"

# Size thresholds
LARGE_FILE_MB = 50  # Files larger than this are considered "large"
SPLIT_SIZE_MB = 100  # Split files larger than this
ARCHIVE_DAYS = 7  # Archive files older than this

def get_file_info(file_path: Path) -> dict:
    """Get information about a JSONL file"""
    size_mb = file_path.stat().st_size / (1024 * 1024)
    line_count = 0
    first_event = None
    last_event = None
    
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            for i, line in enumerate(f):
                if line.strip():
                    line_count += 1
                    try:
                        event = json.loads(line)
                        if i == 0:
                            first_event = event
                        last_event = event
                    except json.JSONDecodeError:
                        pass
    except Exception as e:
        print(f"  ⚠️  Error reading {file_path.name}: {e}")
    
    return {
        'path': file_path,
        'size_mb': size_mb,
        'lines': line_count,
        'first_event': first_event,
        'last_event': last_event,
        'modified': datetime.fromtimestamp(file_path.stat().st_mtime)
    }

def list_large_files(threshold_mb: float = LARGE_FILE_MB) -> List[dict]:
    """List all JSONL files larger than threshold"""
    large_files = []
    
    if not EVENT_LOGS_DIR.exists():
        print(f"Event logs directory not found: {EVENT_LOGS_DIR}")
        return large_files
    
    print(f"\n📊 Scanning for large JSONL files (>{threshold_mb}MB)...")
    print(f"   Directory: {EVENT_LOGS_DIR}")
    
    jsonl_files = list(EVENT_LOGS_DIR.glob("pipeline_*.jsonl"))
    
    for file_path in jsonl_files:
        size_mb = file_path.stat().st_size / (1024 * 1024)
        if size_mb > threshold_mb:
            info = get_file_info(file_path)
            large_files.append(info)
    
    large_files.sort(key=lambda x: x['size_mb'], reverse=True)
    return large_files

def compress_file(file_path: Path, keep_original: bool = True) -> Path:
    """Compress a JSONL file with gzip"""
    compressed_path = COMPRESSED_DIR / f"{file_path.name}.gz"
    COMPRESSED_DIR.mkdir(parents=True, exist_ok=True)
    
    print(f"  📦 Compressing {file_path.name}...")
    
    with open(file_path, 'rb') as f_in:
        with gzip.open(compressed_path, 'wb') as f_out:
            shutil.copyfileobj(f_in, f_out)
    
    original_size = file_path.stat().st_size / (1024 * 1024)
    compressed_size = compressed_path.stat().st_size / (1024 * 1024)
    ratio = (1 - compressed_size / original_size) * 100
    
    print(f"     Original: {original_size:.2f}MB → Compressed: {compressed_size:.2f}MB ({ratio:.1f}% reduction)")
    
    if not keep_original:
        file_path.unlink()
        print(f"     ✅ Original file removed")
    
    return compressed_path

def archive_file(file_path: Path, keep_original: bool = False) -> Path:
    """Move file to archive directory"""
    ARCHIVE_DIR.mkdir(parents=True, exist_ok=True)
    
    archive_path = ARCHIVE_DIR / file_path.name
    print(f"  📁 Archiving {file_path.name}...")
    
    if archive_path.exists():
        # Add timestamp to avoid conflicts
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        archive_path = ARCHIVE_DIR / f"{file_path.stem}_{timestamp}{file_path.suffix}"
    
    shutil.move(str(file_path), str(archive_path))
    print(f"     ✅ Moved to {archive_path.relative_to(QTSW2_ROOT)}")
    
    return archive_path

def split_file_by_date(file_path: Path, days_per_file: int = 1) -> List[Path]:
    """Split a JSONL file by date (one file per day)"""
    print(f"  ✂️  Splitting {file_path.name} by date...")
    
    files_by_date = {}
    line_count = 0
    
    # Read and group events by date
    with open(file_path, 'r', encoding='utf-8') as f:
        for line in f:
            if not line.strip():
                continue
            
            try:
                event = json.loads(line)
                timestamp_str = event.get('timestamp', '')
                
                if timestamp_str:
                    # Parse timestamp
                    if timestamp_str.endswith('Z'):
                        timestamp_str = timestamp_str[:-1] + '+00:00'
                    try:
                        event_time = datetime.fromisoformat(timestamp_str.replace('Z', '+00:00'))
                    except:
                        # Fallback: use file modification time
                        event_time = datetime.fromtimestamp(file_path.stat().st_mtime)
                else:
                    event_time = datetime.fromtimestamp(file_path.stat().st_mtime)
                
                # Group by date
                date_key = event_time.date().isoformat()
                
                if date_key not in files_by_date:
                    run_id = event.get('run_id', 'unknown')
                    new_file = EVENT_LOGS_DIR / f"pipeline_{run_id}_{date_key}.jsonl"
                    files_by_date[date_key] = {
                        'file': new_file,
                        'events': []
                    }
                
                files_by_date[date_key]['events'].append(line)
                line_count += 1
                
            except json.JSONDecodeError:
                continue
    
    # Write split files
    created_files = []
    for date_key, data in files_by_date.items():
        with open(data['file'], 'w', encoding='utf-8') as f:
            f.writelines(data['events'])
        size_mb = data['file'].stat().st_size / (1024 * 1024)
        print(f"     ✅ Created {data['file'].name} ({size_mb:.2f}MB, {len(data['events'])} events)")
        created_files.append(data['file'])
    
    print(f"     📊 Total: {line_count} events split into {len(created_files)} files")
    
    return created_files

def split_file_by_size(file_path: Path, max_size_mb: float = 50) -> List[Path]:
    """Split a JSONL file into smaller chunks"""
    print(f"  ✂️  Splitting {file_path.name} by size (max {max_size_mb}MB per file)...")
    
    max_size_bytes = max_size_mb * 1024 * 1024
    chunk_num = 0
    current_size = 0
    current_file = None
    current_writer = None
    created_files = []
    run_id = file_path.stem.replace('pipeline_', '')
    
    with open(file_path, 'r', encoding='utf-8') as f:
        for line in f:
            if not line.strip():
                continue
            
            line_size = len(line.encode('utf-8'))
            
            # Start new file if needed
            if current_file is None or current_size + line_size > max_size_bytes:
                if current_writer:
                    current_writer.close()
                
                chunk_num += 1
                current_file = EVENT_LOGS_DIR / f"pipeline_{run_id}_chunk{chunk_num:03d}.jsonl"
                current_writer = open(current_file, 'w', encoding='utf-8')
                current_size = 0
                created_files.append(current_file)
            
            current_writer.write(line)
            current_size += line_size
    
    if current_writer:
        current_writer.close()
    
    # Report
    for f in created_files:
        size_mb = f.stat().st_size / (1024 * 1024)
        print(f"     ✅ Created {f.name} ({size_mb:.2f}MB)")
    
    print(f"     📊 Split into {len(created_files)} files")
    
    return created_files

def main():
    parser = argparse.ArgumentParser(description='Manage large JSONL event log files')
    parser.add_argument('--list', action='store_true', help='List large files')
    parser.add_argument('--compress', type=str, help='Compress a specific file (filename)')
    parser.add_argument('--archive', type=str, help='Archive a specific file (filename)')
    parser.add_argument('--split-date', type=str, help='Split a file by date (filename)')
    parser.add_argument('--split-size', type=str, help='Split a file by size (filename)')
    parser.add_argument('--archive-old', type=int, default=ARCHIVE_DAYS, 
                       help=f'Archive files older than N days (default: {ARCHIVE_DAYS})')
    parser.add_argument('--threshold', type=float, default=LARGE_FILE_MB,
                       help=f'Size threshold in MB for "large" files (default: {LARGE_FILE_MB})')
    
    args = parser.parse_args()
    
    print("=" * 60)
    print("JSONL File Manager")
    print("=" * 60)
    
    if args.list:
        large_files = list_large_files(args.threshold)
        
        if not large_files:
            print(f"\n✅ No files larger than {args.threshold}MB found")
            return
        
        print(f"\n📋 Found {len(large_files)} large file(s):\n")
        for info in large_files:
            first_ts = info['first_event'].get('timestamp', 'unknown') if info['first_event'] else 'unknown'
            last_ts = info['last_event'].get('timestamp', 'unknown') if info['last_event'] else 'unknown'
            print(f"  📄 {info['path'].name}")
            print(f"     Size: {info['size_mb']:.2f}MB")
            print(f"     Lines: {info['lines']:,}")
            print(f"     Modified: {info['modified'].strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"     First event: {first_ts[:19] if first_ts != 'unknown' else 'unknown'}")
            print(f"     Last event: {last_ts[:19] if last_ts != 'unknown' else 'unknown'}")
            print()
    
    elif args.compress:
        file_path = EVENT_LOGS_DIR / args.compress
        if not file_path.exists():
            print(f"❌ File not found: {file_path}")
            return
        compress_file(file_path, keep_original=True)
        print("\n✅ Compression complete")
    
    elif args.archive:
        file_path = EVENT_LOGS_DIR / args.archive
        if not file_path.exists():
            print(f"❌ File not found: {file_path}")
            return
        archive_file(file_path, keep_original=False)
        print("\n✅ Archiving complete")
    
    elif args.split_date:
        file_path = EVENT_LOGS_DIR / args.split_date
        if not file_path.exists():
            print(f"❌ File not found: {file_path}")
            return
        created = split_file_by_date(file_path)
        print(f"\n✅ Split complete - {len(created)} files created")
        print("   Original file still exists - delete manually if desired")
    
    elif args.split_size:
        file_path = EVENT_LOGS_DIR / args.split_size
        if not file_path.exists():
            print(f"❌ File not found: {file_path}")
            return
        created = split_file_by_size(file_path)
        print(f"\n✅ Split complete - {len(created)} files created")
        print("   Original file still exists - delete manually if desired")
    
    elif args.archive_old:
        print(f"\n📁 Archiving files older than {args.archive_old} days...")
        cutoff_date = datetime.now() - timedelta(days=args.archive_old)
        
        jsonl_files = list(EVENT_LOGS_DIR.glob("pipeline_*.jsonl"))
        archived_count = 0
        
        for file_path in jsonl_files:
            file_date = datetime.fromtimestamp(file_path.stat().st_mtime)
            if file_date < cutoff_date:
                archive_file(file_path, keep_original=False)
                archived_count += 1
        
        print(f"\n✅ Archived {archived_count} file(s)")
    
    else:
        parser.print_help()
        print("\n💡 Quick start:")
        print("   python tools/manage_jsonl_files.py --list")
        print("   python tools/manage_jsonl_files.py --split-date pipeline_006f9310.jsonl")
        print("   python tools/manage_jsonl_files.py --archive-old 7")

if __name__ == "__main__":
    main()






















