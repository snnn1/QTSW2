#!/usr/bin/env python3
"""
Check sync status between modules/robot/core and RobotCore_For_NinjaTrader
"""
import os
import hashlib
from pathlib import Path
from datetime import datetime

def get_file_hash(filepath):
    """Get SHA256 hash of file"""
    try:
        with open(filepath, 'rb') as f:
            return hashlib.sha256(f.read()).hexdigest()
    except Exception as e:
        return f"ERROR: {e}"

def get_file_time(filepath):
    """Get file modification time"""
    try:
        return datetime.fromtimestamp(os.path.getmtime(filepath))
    except:
        return None

def check_sync():
    """Check sync status of key files"""
    
    # Key files to check
    key_files = [
        "RobotEngine.cs",
        "StreamStateMachine.cs",
        "RobotEventTypes.cs",
        "RobotLogger.cs",
        "RobotLoggingService.cs",
        "Execution/NinjaTraderSimAdapter.cs",
        "Execution/NinjaTraderSimAdapter.NT.cs",
        "Execution/RiskGate.cs",
        "Execution/ExecutionJournal.cs",
        "HealthMonitor.cs",
        "TimeService.cs",
    ]
    
    base_source = Path("modules/robot/core")
    base_dest = Path("RobotCore_For_NinjaTrader")
    
    print("=" * 80)
    print("SYNC STATUS CHECK: modules/robot/core <-> RobotCore_For_NinjaTrader")
    print("=" * 80)
    print()
    
    synced = []
    different = []
    missing_source = []
    missing_dest = []
    
    for rel_path in key_files:
        source_path = base_source / rel_path
        dest_path = base_dest / rel_path
        
        source_exists = source_path.exists()
        dest_exists = dest_path.exists()
        
        if not source_exists:
            missing_source.append(rel_path)
            continue
        
        if not dest_exists:
            missing_dest.append(rel_path)
            continue
        
        source_hash = get_file_hash(source_path)
        dest_hash = get_file_hash(dest_path)
        source_time = get_file_time(source_path)
        dest_time = get_file_time(dest_path)
        
        if source_hash == dest_hash:
            synced.append((rel_path, source_time, dest_time))
        else:
            different.append((rel_path, source_time, dest_time, source_hash[:16], dest_hash[:16]))
    
    # Print results
    print(f"✅ SYNCED ({len(synced)} files):")
    for rel_path, source_time, dest_time in synced:
        print(f"  {rel_path}")
        if source_time and dest_time:
            time_diff = abs((source_time - dest_time).total_seconds())
            if time_diff > 1:
                print(f"    Source: {source_time.strftime('%Y-%m-%d %H:%M:%S')}")
                print(f"    Dest:   {dest_time.strftime('%Y-%m-%d %H:%M:%S')}")
    
    print()
    
    if different:
        print(f"⚠️  DIFFERENT ({len(different)} files):")
        for rel_path, source_time, dest_time, source_hash, dest_hash in different:
            print(f"  {rel_path}")
            if source_time:
                print(f"    Source modified: {source_time.strftime('%Y-%m-%d %H:%M:%S')}")
            if dest_time:
                print(f"    Dest modified:   {dest_time.strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"    Source hash: {source_hash}...")
            print(f"    Dest hash:   {dest_hash}...")
        print()
    
    if missing_source:
        print(f"❌ MISSING IN SOURCE ({len(missing_source)} files):")
        for rel_path in missing_source:
            print(f"  {rel_path}")
        print()
    
    if missing_dest:
        print(f"❌ MISSING IN DEST ({len(missing_dest)} files):")
        for rel_path in missing_dest:
            print(f"  {rel_path}")
        print()
    
    # Summary
    print("=" * 80)
    print("SUMMARY")
    print("=" * 80)
    print(f"Total files checked: {len(key_files)}")
    print(f"✅ Synced: {len(synced)}")
    print(f"⚠️  Different: {len(different)}")
    print(f"❌ Missing in source: {len(missing_source)}")
    print(f"❌ Missing in dest: {len(missing_dest)}")
    
    if len(different) == 0 and len(missing_dest) == 0:
        print()
        print("✅ ALL KEY FILES ARE SYNCED!")
    elif len(different) > 0:
        print()
        print("⚠️  SOME FILES NEED SYNCING")
        print("   Run sync script or manually copy files to sync")

if __name__ == '__main__':
    check_sync()
