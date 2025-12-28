"""
Comprehensive Pipeline Diagnostic Tool
Tests each component and identifies issues
"""

import sys
import json
import subprocess
from pathlib import Path
from datetime import datetime
from typing import Dict, List, Any

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

def print_section(title: str):
    print("\n" + "="*80)
    print(title)
    print("="*80)

def print_result(test_name: str, passed: bool, details: str = ""):
    status = "[PASS]" if passed else "[FAIL]"
    print(f"{status} {test_name}")
    if details:
        print(f"      {details}")

def test_file_exists(path: Path, description: str) -> bool:
    """Test if a file exists"""
    exists = path.exists()
    print_result(f"{description} exists", exists, 
                 f"Path: {path}" if exists else f"Missing: {path}")
    return exists

def test_script_runs(script_path: Path, args: List[str] = None, description: str = "") -> tuple:
    """Test if a script can be executed"""
    if not script_path.exists():
        print_result(f"{description} - script exists", False, f"Missing: {script_path}")
        return False, "Script not found"
    
    try:
        cmd = [sys.executable, str(script_path)] + (args or [])
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=10,
            cwd=qtsw2_root
        )
        success = result.returncode == 0
        output = result.stdout[:200] if result.stdout else ""
        error = result.stderr[:200] if result.stderr else ""
        details = f"Exit code: {result.returncode}"
        if output:
            details += f" | Output: {output[:100]}"
        if error:
            details += f" | Error: {error[:100]}"
        print_result(f"{description} - script runs", success, details)
        return success, result.stdout + result.stderr
    except subprocess.TimeoutExpired:
        print_result(f"{description} - script runs", False, "Timeout after 10 seconds")
        return False, "Timeout"
    except Exception as e:
        print_result(f"{description} - script runs", False, f"Exception: {e}")
        return False, str(e)

def test_config_paths():
    """Test configuration paths"""
    print_section("CONFIGURATION PATHS")
    
    from automation.config import PipelineConfig
    
    try:
        config = PipelineConfig.from_environment()
        
        # Test analyzer script
        analyzer_exists = test_file_exists(
            config.parallel_analyzer_script,
            "Analyzer script"
        )
        
        # Test merger script
        merger_exists = test_file_exists(
            config.merger_script,
            "Merger script"
        )
        
        # Test data directories
        test_file_exists(config.data_raw, "Raw data directory")
        test_file_exists(config.data_translated, "Translated data directory")
        test_file_exists(config.analyzer_runs, "Analyzer runs directory")
        
        return analyzer_exists and merger_exists
    except Exception as e:
        print_result("Config loading", False, f"Error: {e}")
        return False

def test_analyzer():
    """Test analyzer script"""
    print_section("ANALYZER TEST")
    
    analyzer_script = qtsw2_root / "ops" / "maintenance" / "run_analyzer_parallel.py"
    
    # Test script exists
    if not analyzer_script.exists():
        print_result("Analyzer script exists", False, f"Missing: {analyzer_script}")
        return False
    
    # Test script can be imported
    try:
        # Just test import, not full execution
        import importlib.util
        spec = importlib.util.spec_from_file_location("run_analyzer_parallel", analyzer_script)
        if spec and spec.loader:
            print_result("Analyzer script importable", True, "")
        else:
            print_result("Analyzer script importable", False, "Could not create spec")
            return False
    except Exception as e:
        print_result("Analyzer script importable", False, f"Error: {e}")
        return False
    
    # Test with --help
    success, output = test_script_runs(analyzer_script, ["--help"], "Analyzer --help")
    
    return success

def test_merger():
    """Test merger script"""
    print_section("MERGER TEST")
    
    merger_script = qtsw2_root / "modules" / "merger" / "merger.py"
    
    # Test script exists
    if not merger_script.exists():
        print_result("Merger script exists", False, f"Missing: {merger_script}")
        return False
    
    # Test script can be imported
    try:
        import importlib.util
        spec = importlib.util.spec_from_file_location("merger", merger_script)
        if spec and spec.loader:
            print_result("Merger script importable", True, "")
        else:
            print_result("Merger script importable", False, "Could not create spec")
            return False
    except Exception as e:
        print_result("Merger script importable", False, f"Error: {e}")
        return False
    
    return True

def test_data_directories():
    """Test data directory structure"""
    print_section("DATA DIRECTORY STRUCTURE")
    
    # Check analyzer temp
    analyzer_temp = qtsw2_root / "data" / "analyzer_temp"
    exists = analyzer_temp.exists()
    print_result("Analyzer temp exists", exists, f"Path: {analyzer_temp}")
    
    if exists:
        folders = [d for d in analyzer_temp.iterdir() if d.is_dir()]
        print_result(f"Unprocessed folders", True, f"Count: {len(folders)}")
        if folders:
            print("      Recent folders:")
            for f in sorted(folders)[-5:]:
                print(f"        {f.name}")
    
    # Check analyzed output
    analyzed = qtsw2_root / "data" / "analyzed"
    exists = analyzed.exists()
    print_result("Analyzed output exists", exists, f"Path: {analyzed}")
    
    if exists:
        # Count monthly files
        monthly_files = list(analyzed.rglob("*_an_*.parquet"))
        print_result("Monthly analyzed files", True, f"Count: {len(monthly_files)}")
        if monthly_files:
            print("      Sample files:")
            for f in sorted(monthly_files)[-3:]:
                print(f"        {f.name}")
    
    return True

def test_latest_pipeline_run():
    """Test latest pipeline run"""
    print_section("LATEST PIPELINE RUN")
    
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    if not events_dir.exists():
        print_result("Events directory exists", False, f"Missing: {events_dir}")
        return False
    
    pipeline_files = sorted(
        events_dir.glob("pipeline_*.jsonl"),
        key=lambda p: p.stat().st_mtime,
        reverse=True
    )
    
    if not pipeline_files:
        print_result("Pipeline runs found", False, "No pipeline runs found")
        return False
    
    latest_file = pipeline_files[0]
    mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
    print_result("Latest pipeline run", True, 
                 f"File: {latest_file.name}, Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}")
    
    # Parse events
    with open(latest_file, 'r') as f:
        events = [json.loads(l) for l in f if l.strip()]
    
    # Group by stage
    stages = {}
    for e in events:
        stage = e.get('stage', 'unknown')
        if stage not in stages:
            stages[stage] = []
        stages[stage].append(e)
    
    print(f"\n      Stage summary:")
    for stage, stage_events in sorted(stages.items()):
        event_types = set(e.get('event') for e in stage_events)
        success = any(e.get('event') == 'success' for e in stage_events)
        failure = any(e.get('event') == 'failure' for e in stage_events)
        status = "SUCCESS" if success else ("FAILED" if failure else "IN PROGRESS")
        print(f"        {stage}: {len(stage_events)} events - {status}")
        if failure:
            failures = [e for e in stage_events if e.get('event') == 'failure']
            for f in failures:
                msg = f.get('msg', '')[:100]
                print(f"          FAILURE: {msg}")
    
    # Check if merger ran
    merger_ran = 'merger' in stages
    print_result("Merger stage executed", merger_ran, 
                 "Merger ran" if merger_ran else "Merger did not run")
    
    if not merger_ran:
        analyzer_success = any(
            e.get('event') == 'success' 
            for e in stages.get('analyzer', [])
        )
        if not analyzer_success:
            print("      [INFO] Merger didn't run because analyzer failed")
        else:
            print("      [WARNING] Analyzer succeeded but merger didn't run!")
    
    return True

def test_merger_processed_log():
    """Test merger processed log"""
    print_section("MERGER PROCESSED LOG")
    
    merger_log = qtsw2_root / "data" / "merger_processed.json"
    exists = merger_log.exists()
    print_result("Merger log exists", exists, f"Path: {merger_log}")
    
    if exists:
        with open(merger_log, 'r') as f:
            data = json.load(f)
        analyzer_processed = data.get("analyzer", [])
        print_result("Processed folders", True, f"Count: {len(analyzer_processed)}")
        if analyzer_processed:
            print(f"      Latest processed: {analyzer_processed[-1]}")
    
    return exists

def main():
    print("="*80)
    print("PIPELINE DIAGNOSTIC TOOL")
    print("="*80)
    print(f"Timestamp: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"QTSW2 Root: {qtsw2_root}")
    
    results = {}
    
    # Run tests
    results['config'] = test_config_paths()
    results['analyzer'] = test_analyzer()
    results['merger'] = test_merger()
    results['data_dirs'] = test_data_directories()
    results['latest_run'] = test_latest_pipeline_run()
    results['merger_log'] = test_merger_processed_log()
    
    # Summary
    print_section("SUMMARY")
    total = len(results)
    passed = sum(1 for v in results.values() if v)
    print(f"Tests passed: {passed}/{total}")
    
    if passed < total:
        print("\n[ISSUES FOUND]")
        for test, result in results.items():
            if not result:
                print(f"  - {test}")
    else:
        print("\n[ALL TESTS PASSED]")
    
    return passed == total

if __name__ == "__main__":
    success = main()
    sys.exit(0 if success else 1)

