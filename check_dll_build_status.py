#!/usr/bin/env python3
"""
Check if Robot.Core.dll needs to be rebuilt based on source file timestamps.
"""
from pathlib import Path
from datetime import datetime

def main():
    dll_path = Path("RobotCore_For_NinjaTrader/bin/Release/net48/Robot.Core.dll")
    
    # Source files we modified
    source_files = [
        Path("RobotCore_For_NinjaTrader/HealthMonitor.cs"),
        Path("RobotCore_For_NinjaTrader/RobotEngine.cs"),
        Path("RobotCore_For_NinjaTrader/RobotEventTypes.cs"),
        Path("RobotCore_For_NinjaTrader/Execution/RiskGate.cs"),
    ]
    
    print("="*80)
    print("DLL BUILD STATUS CHECK")
    print("="*80)
    
    if not dll_path.exists():
        print(f"\n[ERROR] DLL not found: {dll_path}")
        print("DLL needs to be built!")
        return
    
    dll_mtime = datetime.fromtimestamp(dll_path.stat().st_mtime)
    print(f"\nDLL: {dll_path}")
    print(f"  Modified: {dll_mtime}")
    
    print(f"\nSource Files:")
    needs_rebuild = False
    for src in source_files:
        if src.exists():
            src_mtime = datetime.fromtimestamp(src.stat().st_mtime)
            is_newer = src_mtime > dll_mtime
            status = "[NEEDS REBUILD]" if is_newer else "[OK]"
            print(f"  {src.name:30} Modified: {src_mtime} {status}")
            if is_newer:
                needs_rebuild = True
        else:
            print(f"  {src.name:30} [NOT FOUND]")
    
    print("\n" + "="*80)
    if needs_rebuild:
        print("[WARN] DLL NEEDS TO BE REBUILT")
        print("\nSource files are newer than DLL.")
        print("The running NinjaTrader instance may be using an old DLL.")
        print("\nTo rebuild:")
        print("  1. Open Robot.Core.csproj in Visual Studio")
        print("  2. Build -> Rebuild Solution (or Build Solution)")
        print("  3. Restart NinjaTrader to load the new DLL")
    else:
        print("[OK] DLL is up to date")
        print("\nDLL is newer than all source files.")
        print("However, if NinjaTrader is currently running, it may have loaded an old DLL.")
        print("Restart NinjaTrader to ensure it loads the latest DLL.")

if __name__ == "__main__":
    main()
