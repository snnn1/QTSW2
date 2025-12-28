"""Test analyzer manually to see what's failing"""
import sys
import subprocess
from pathlib import Path

qtsw2_root = Path(__file__).parent.parent
analyzer_script = qtsw2_root / "ops" / "maintenance" / "run_analyzer_parallel.py"

print("="*80)
print("MANUAL ANALYZER TEST")
print("="*80)

# Check if we have translated files
translated_dir = qtsw2_root / "data" / "translated"
print(f"\n[TRANSLATED FILES]")
if translated_dir.exists():
    # Find latest date folder
    date_folders = [d for d in translated_dir.rglob("*") if d.is_dir() and d.name.isdigit()]
    if date_folders:
        print(f"  Found date folders")
        # Get latest month
        month_folders = [d for d in translated_dir.rglob("*/2025/12") if d.is_dir()]
        if month_folders:
            print(f"  Found December 2025 folders")
            # Count files
            parquet_files = list(month_folders[0].parent.rglob("*.parquet"))
            print(f"  Parquet files in 2025: {len(parquet_files)}")
            if parquet_files:
                print(f"  Sample files:")
                for f in sorted(parquet_files)[:5]:
                    print(f"    {f.name}")
        else:
            print(f"  No December 2025 folders found")
    else:
        print(f"  No date folders found")
else:
    print(f"  Translated directory doesn't exist")

# Try running analyzer with minimal args to see error
print(f"\n[TESTING ANALYZER]")
print(f"  Script: {analyzer_script}")

# Check what instruments we have
instruments = ["ES", "NQ", "YM", "CL", "NG", "GC"]
print(f"  Testing with instruments: {instruments}")

# Try to run with --help first
print(f"\n[RUNNING: --help]")
try:
    result = subprocess.run(
        [sys.executable, str(analyzer_script), "--help"],
        capture_output=True,
        text=True,
        timeout=10,
        cwd=qtsw2_root
    )
    print(f"  Exit code: {result.returncode}")
    if result.stdout:
        print(f"  Output:\n{result.stdout[:500]}")
    if result.stderr:
        print(f"  Error:\n{result.stderr[:500]}")
except Exception as e:
    print(f"  Exception: {e}")

# Try running with actual args (dry run if possible)
print(f"\n[TESTING WITH ACTUAL ARGS]")
# Check if there's a --dry-run or similar flag
try:
    # Try with a test folder that might not exist to see error handling
    result = subprocess.run(
        [sys.executable, str(analyzer_script), 
         "--folder", str(qtsw2_root / "data" / "translated" / "ES" / "1m" / "2025" / "12"),
         "--instruments", "ES"],
        capture_output=True,
        text=True,
        timeout=30,
        cwd=qtsw2_root
    )
    print(f"  Exit code: {result.returncode}")
    if result.stdout:
        print(f"  Output:\n{result.stdout[:1000]}")
    if result.stderr:
        print(f"  Error:\n{result.stderr[:1000]}")
except subprocess.TimeoutExpired:
    print(f"  Timeout after 30 seconds")
except Exception as e:
    print(f"  Exception: {e}")


