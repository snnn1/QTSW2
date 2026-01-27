#!/usr/bin/env python3
"""Cleanup old log files"""
import shutil
from pathlib import Path
from datetime import datetime, timedelta, timezone

qtsw2_root = Path(__file__).parent.parent
robot_logs_dir = qtsw2_root / "logs" / "robot"
archive_dir = robot_logs_dir / "archive"

# Create archive directory if it doesn't exist
archive_dir.mkdir(parents=True, exist_ok=True)

# Rotate frontend_feed.jsonl if it's too large
frontend_feed = robot_logs_dir / "frontend_feed.jsonl"
MAX_SIZE_MB = 100
MAX_SIZE_BYTES = MAX_SIZE_MB * 1024 * 1024

if frontend_feed.exists():
    size_mb = frontend_feed.stat().st_size / (1024 * 1024)
    print(f"frontend_feed.jsonl: {size_mb:.2f} MB")
    
    if frontend_feed.stat().st_size >= MAX_SIZE_BYTES:
        timestamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
        archive_path = archive_dir / f"frontend_feed_{timestamp}.jsonl"
        
        print(f"Rotating {frontend_feed.name} -> archive/{archive_path.name}")
        shutil.move(str(frontend_feed), str(archive_path))
        print(f"[OK] Rotated ({size_mb:.2f} MB)")
    else:
        print(f"[OK] Size OK ({size_mb:.2f} MB < {MAX_SIZE_MB} MB)")

# Clean up old archive files (older than 30 days)
cutoff = datetime.now(timezone.utc) - timedelta(days=30)
deleted_count = 0
deleted_size_mb = 0

for archive_file in archive_dir.glob("*.jsonl"):
    try:
        file_time = datetime.fromtimestamp(archive_file.stat().st_mtime, tz=timezone.utc)
        if file_time < cutoff:
            size_mb = archive_file.stat().st_size / (1024 * 1024)
            archive_file.unlink()
            deleted_count += 1
            deleted_size_mb += size_mb
            print(f"Deleted old archive: {archive_file.name} ({size_mb:.2f} MB)")
    except Exception as e:
        print(f"Error deleting {archive_file.name}: {e}")

if deleted_count > 0:
    print(f"\n[OK] Cleaned up {deleted_count} old archive files ({deleted_size_mb:.2f} MB)")
else:
    print("\n[OK] No old archive files to clean up")
