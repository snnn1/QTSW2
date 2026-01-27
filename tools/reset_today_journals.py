#!/usr/bin/env python3
"""Reset today's journal files to allow streams to rehydrate"""
import json
from pathlib import Path
from datetime import datetime, timezone
import sys

def get_today_trading_date():
    """Get today's trading date in YYYY-MM-DD format"""
    return datetime.now(timezone.utc).strftime("%Y-%m-%d")

def reset_today_journals(project_root=None, trading_date=None, dry_run=False):
    """
    Delete all journal files for today (or specified trading date) to allow streams to reset.
    
    Args:
        project_root: Path to project root (defaults to current directory)
        trading_date: Trading date in YYYY-MM-DD format (defaults to today)
        dry_run: If True, only show what would be deleted without actually deleting
    """
    if project_root is None:
        project_root = Path.cwd()
    else:
        project_root = Path(project_root)
    
    if trading_date is None:
        trading_date = get_today_trading_date()
    
    journal_dir = project_root / "logs" / "robot" / "journal"
    
    if not journal_dir.exists():
        print(f"Journal directory does not exist: {journal_dir}")
        return
    
    # Find all journal files for the specified trading date
    pattern = f"{trading_date}_*.json"
    journal_files = list(journal_dir.glob(pattern))
    
    if not journal_files:
        print(f"No journal files found for trading date: {trading_date}")
        print(f"Pattern: {pattern}")
        print(f"Directory: {journal_dir}")
        return
    
    print("="*80)
    print(f"RESET JOURNALS FOR TRADING DATE: {trading_date}")
    print("="*80)
    print(f"Journal directory: {journal_dir}")
    print(f"Found {len(journal_files)} journal file(s):")
    print()
    
    for journal_file in sorted(journal_files):
        # Try to read and display journal contents before deletion
        try:
            with open(journal_file, 'r') as f:
                journal_data = json.load(f)
            
            stream = journal_data.get('Stream', 'UNKNOWN')
            committed = journal_data.get('Committed', False)
            commit_reason = journal_data.get('CommitReason', None)
            last_state = journal_data.get('LastState', 'UNKNOWN')
            
            print(f"  {journal_file.name}")
            print(f"    Stream: {stream}")
            print(f"    Committed: {committed}")
            print(f"    Commit Reason: {commit_reason}")
            print(f"    Last State: {last_state}")
            print()
        except Exception as e:
            print(f"  {journal_file.name} (could not read: {e})")
            print()
    
    if dry_run:
        print("DRY RUN MODE - No files will be deleted")
        print("Run without --dry-run to actually delete these files")
    else:
        print("DELETING JOURNAL FILES...")
        deleted_count = 0
        for journal_file in journal_files:
            try:
                journal_file.unlink()
                deleted_count += 1
                print(f"  Deleted: {journal_file.name}")
            except Exception as e:
                print(f"  ERROR deleting {journal_file.name}: {e}")
        
        print()
        print(f"Successfully deleted {deleted_count} of {len(journal_files)} journal file(s)")
        print()
        print("Streams will be reset on next restart and can rehydrate normally.")
    
    print("="*80)

if __name__ == "__main__":
    import argparse
    
    parser = argparse.ArgumentParser(
        description="Reset today's journal files to allow streams to rehydrate",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Reset today's journals (dry run)
  python tools/reset_today_journals.py --dry-run
  
  # Reset today's journals (actually delete)
  python tools/reset_today_journals.py
  
  # Reset journals for a specific date
  python tools/reset_today_journals.py --trading-date 2026-01-26
  
  # Reset journals with custom project root
  python tools/reset_today_journals.py --project-root /path/to/project
        """
    )
    
    parser.add_argument(
        "--project-root",
        type=str,
        help="Path to project root (defaults to current directory)"
    )
    
    parser.add_argument(
        "--trading-date",
        type=str,
        help="Trading date in YYYY-MM-DD format (defaults to today)"
    )
    
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Show what would be deleted without actually deleting"
    )
    
    args = parser.parse_args()
    
    reset_today_journals(
        project_root=args.project_root,
        trading_date=args.trading_date,
        dry_run=args.dry_run
    )
