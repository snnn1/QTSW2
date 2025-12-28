"""
Pipeline Diagnostic Tool
Runs through the pipeline initialization and checks for errors and potential issues.
"""

import sys
import asyncio
import logging
from pathlib import Path
from datetime import datetime
from typing import List, Dict, Any

# Add project root to path
qtsw2_root = Path(__file__).parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

# Setup logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[logging.StreamHandler()]
)
logger = logging.getLogger(__name__)


class DiagnosticResult:
    """Container for diagnostic results"""
    def __init__(self):
        self.checks: List[Dict[str, Any]] = []
        self.errors: List[str] = []
        self.warnings: List[str] = []
        self.info: List[str] = []
    
    def add_check(self, name: str, status: str, message: str, details: Dict[str, Any] = None):
        """Add a diagnostic check result"""
        self.checks.append({
            "name": name,
            "status": status,  # "pass", "fail", "warning", "info"
            "message": message,
            "details": details or {}
        })
        if status == "fail":
            self.errors.append(f"{name}: {message}")
        elif status == "warning":
            self.warnings.append(f"{name}: {message}")
        else:
            self.info.append(f"{name}: {message}")
    
    def print_summary(self):
        """Print diagnostic summary"""
        print("\n" + "="*80)
        print("PIPELINE DIAGNOSTIC SUMMARY")
        print("="*80)
        
        print(f"\nTotal Checks: {len(self.checks)}")
        print(f"  [PASS] Passed: {sum(1 for c in self.checks if c['status'] == 'pass')}")
        print(f"  [WARN] Warnings: {sum(1 for c in self.checks if c['status'] == 'warning')}")
        print(f"  [FAIL] Failed: {sum(1 for c in self.checks if c['status'] == 'fail')}")
        print(f"  [INFO] Info: {sum(1 for c in self.checks if c['status'] == 'info')}")
        
        if self.errors:
            print("\n" + "="*80)
            print("ERRORS (Must Fix):")
            print("="*80)
            for i, error in enumerate(self.errors, 1):
                print(f"{i}. {error}")
        
        if self.warnings:
            print("\n" + "="*80)
            print("WARNINGS (Should Review):")
            print("="*80)
            for i, warning in enumerate(self.warnings, 1):
                print(f"{i}. {warning}")
        
        print("\n" + "="*80)
        print("DETAILED CHECK RESULTS")
        print("="*80)
        for check in self.checks:
            status_symbol = {
                "pass": "[PASS]",
                "fail": "[FAIL]",
                "warning": "[WARN]",
                "info": "[INFO]"
            }.get(check['status'], "[?]")
            print(f"\n{status_symbol} {check['name']}")
            print(f"   Status: {check['status'].upper()}")
            print(f"   Message: {check['message']}")
            if check['details']:
                print(f"   Details:")
                for key, value in check['details'].items():
                    print(f"     - {key}: {value}")


async def diagnose_pipeline():
    """Run comprehensive pipeline diagnostics"""
    result = DiagnosticResult()
    
    print("Starting pipeline diagnostic...")
    print(f"Project Root: {qtsw2_root}")
    print(f"Timestamp: {datetime.now().isoformat()}\n")
    
    # 1. Check project root exists
    if qtsw2_root.exists():
        result.add_check("Project Root", "pass", f"Project root exists: {qtsw2_root}")
    else:
        result.add_check("Project Root", "fail", f"Project root does not exist: {qtsw2_root}")
        return result
    
    # 2. Check configuration files
    config_files = {
        "pipeline.json": qtsw2_root / "configs" / "pipeline.json",
        "instruments.json": qtsw2_root / "configs" / "instruments.json",
        "schedule.json": qtsw2_root / "configs" / "schedule.json",
        "paths.json": qtsw2_root / "configs" / "paths.json"
    }
    
    for name, path in config_files.items():
        if path.exists():
            try:
                import json
                with open(path, 'r') as f:
                    data = json.load(f)
                result.add_check(f"Config: {name}", "pass", f"File exists and is valid JSON", {
                    "path": str(path),
                    "keys": list(data.keys()) if isinstance(data, dict) else "not a dict"
                })
            except Exception as e:
                result.add_check(f"Config: {name}", "fail", f"File exists but invalid: {e}", {
                    "path": str(path)
                })
        else:
            if name == "paths.json":
                result.add_check(f"Config: {name}", "warning", f"File missing (may be optional)", {
                    "path": str(path)
                })
            else:
                result.add_check(f"Config: {name}", "fail", f"File missing", {
                    "path": str(path)
                })
    
    # 3. Check required directories
    required_dirs = {
        "data/raw": qtsw2_root / "data" / "raw",
        "data/translated": qtsw2_root / "data" / "translated",
        "data/analyzed": qtsw2_root / "data" / "analyzed",
        "automation/logs": qtsw2_root / "automation" / "logs",
        "modules/orchestrator": qtsw2_root / "modules" / "orchestrator",
        "automation/pipeline/stages": qtsw2_root / "automation" / "pipeline" / "stages"
    }
    
    for name, path in required_dirs.items():
        if path.exists() and path.is_dir():
            result.add_check(f"Directory: {name}", "pass", f"Directory exists", {
                "path": str(path)
            })
        else:
            result.add_check(f"Directory: {name}", "warning", f"Directory missing (will be created if needed)", {
                "path": str(path)
            })
    
    # 4. Check Python dependencies
    required_modules = [
        "asyncio",
        "pathlib",
        "json",
        "uuid",
        "datetime",
        "logging"
    ]
    
    optional_modules = {
        "httpx": "Required for real-time event publishing to backend",
        "pandas": "Required for data processing",
        "pyarrow": "Required for Parquet file handling"
    }
    
    for module in required_modules:
        try:
            __import__(module)
            result.add_check(f"Module: {module}", "pass", f"Module available")
        except ImportError:
            result.add_check(f"Module: {module}", "fail", f"Module not available")
    
    for module, description in optional_modules.items():
        try:
            __import__(module)
            result.add_check(f"Module: {module}", "pass", f"Module available ({description})")
        except ImportError:
            result.add_check(f"Module: {module}", "warning", f"Module not available ({description})")
    
    # 5. Check orchestrator imports
    try:
        from modules.orchestrator.entrypoint import create_orchestrator
        result.add_check("Orchestrator Import", "pass", "Orchestrator entrypoint imports successfully")
    except ImportError as e:
        result.add_check("Orchestrator Import", "fail", f"Failed to import orchestrator: {e}")
        return result
    except Exception as e:
        result.add_check("Orchestrator Import", "fail", f"Unexpected error importing orchestrator: {e}")
        return result
    
    # 6. Check pipeline stage imports
    stage_modules = {
        "translator": "automation.pipeline.stages.translator",
        "analyzer": "automation.pipeline.stages.analyzer",
        "merger": "automation.pipeline.stages.merger"
    }
    
    for stage_name, module_path in stage_modules.items():
        try:
            __import__(module_path)
            result.add_check(f"Stage: {stage_name}", "pass", f"Stage module imports successfully")
        except ImportError as e:
            result.add_check(f"Stage: {stage_name}", "fail", f"Failed to import stage: {e}")
        except Exception as e:
            result.add_check(f"Stage: {stage_name}", "warning", f"Unexpected error importing stage: {e}")
    
    # 7. Check orchestrator configuration
    try:
        from modules.orchestrator.config import OrchestratorConfig
        config = OrchestratorConfig.from_environment(qtsw2_root=qtsw2_root)
        result.add_check("Orchestrator Config", "pass", "Orchestrator configuration created successfully", {
            "stages": list(config.stages.keys()),
            "event_logs_dir": str(config.event_logs_dir),
            "lock_dir": str(config.lock_dir)
        })
        
        # Check event logs directory
        if config.event_logs_dir.exists():
            result.add_check("Event Logs Dir", "pass", f"Event logs directory exists: {config.event_logs_dir}")
        else:
            result.add_check("Event Logs Dir", "info", f"Event logs directory will be created: {config.event_logs_dir}")
        
    except Exception as e:
        result.add_check("Orchestrator Config", "fail", f"Failed to create orchestrator config: {e}")
    
    # 8. Try to create orchestrator instance (without starting)
    try:
        orchestrator = create_orchestrator(qtsw2_root=qtsw2_root, logger=logger)
        result.add_check("Orchestrator Creation", "pass", "Orchestrator instance created successfully", {
            "has_event_bus": orchestrator.event_bus is not None,
            "has_state_manager": orchestrator.state_manager is not None,
            "has_runner": orchestrator.runner is not None,
            "has_scheduler": orchestrator.scheduler is not None,
            "has_watchdog": orchestrator.watchdog is not None
        })
    except Exception as e:
        result.add_check("Orchestrator Creation", "fail", f"Failed to create orchestrator: {e}")
        return result
    
    # 9. Check pipeline config
    try:
        from automation.config import PipelineConfig
        pipeline_config = PipelineConfig.from_environment()
        result.add_check("Pipeline Config", "pass", "Pipeline configuration loaded successfully", {
            "data_raw": str(pipeline_config.data_raw) if hasattr(pipeline_config, 'data_raw') else "N/A",
            "data_translated": str(pipeline_config.data_translated) if hasattr(pipeline_config, 'data_translated') else "N/A",
            "analyzer_runs": str(pipeline_config.analyzer_runs) if hasattr(pipeline_config, 'analyzer_runs') else "N/A"
        })
    except Exception as e:
        result.add_check("Pipeline Config", "fail", f"Failed to load pipeline config: {e}")
    
    # 10. Check for raw data files
    try:
        raw_data_dir = qtsw2_root / "data" / "raw"
        if raw_data_dir.exists():
            csv_files = list(raw_data_dir.rglob("*.csv"))
            result.add_check("Raw Data Files", "info" if csv_files else "warning", 
                           f"Found {len(csv_files)} raw CSV file(s)", {
                "directory": str(raw_data_dir),
                "file_count": len(csv_files)
            })
        else:
            result.add_check("Raw Data Files", "warning", "Raw data directory does not exist")
    except Exception as e:
        result.add_check("Raw Data Files", "warning", f"Error checking raw data files: {e}")
    
    # 11. Check for translated data files
    try:
        translated_data_dir = qtsw2_root / "data" / "translated"
        if translated_data_dir.exists():
            parquet_files = list(translated_data_dir.rglob("*.parquet"))
            result.add_check("Translated Data Files", "info", 
                           f"Found {len(parquet_files)} translated Parquet file(s)", {
                "directory": str(translated_data_dir),
                "file_count": len(parquet_files)
            })
        else:
            result.add_check("Translated Data Files", "info", "Translated data directory does not exist (will be created)")
    except Exception as e:
        result.add_check("Translated Data Files", "warning", f"Error checking translated data files: {e}")
    
    # 12. Check lock file status
    try:
        lock_dir = qtsw2_root / "automation" / "logs"
        lock_file = lock_dir / ".pipeline.lock"
        if lock_file.exists():
            result.add_check("Pipeline Lock", "warning", "Pipeline lock file exists (pipeline may be running or stuck)", {
                "lock_file": str(lock_file),
                "size": lock_file.stat().st_size
            })
        else:
            result.add_check("Pipeline Lock", "pass", "No pipeline lock file (pipeline is not running)")
    except Exception as e:
        result.add_check("Pipeline Lock", "warning", f"Error checking lock file: {e}")
    
    # 13. Check orchestrator state file
    try:
        state_file = qtsw2_root / "automation" / "logs" / "orchestrator_state.json"
        if state_file.exists():
            import json
            with open(state_file, 'r') as f:
                state_data = json.load(f)
            result.add_check("Orchestrator State", "info", "Orchestrator state file exists", {
                "state_file": str(state_file),
                "current_state": state_data.get("state", "unknown") if isinstance(state_data, dict) else "unknown"
            })
        else:
            result.add_check("Orchestrator State", "info", "No orchestrator state file (will be created on first run)")
    except Exception as e:
        result.add_check("Orchestrator State", "warning", f"Error checking state file: {e}")
    
    # 14. Test orchestrator initialization (without starting background tasks)
    try:
        # Create orchestrator
        test_orchestrator = create_orchestrator(qtsw2_root=qtsw2_root, logger=logger)
        
        # Check readiness (should be True if event_bus exists)
        is_ready = test_orchestrator.is_ready()
        result.add_check("Orchestrator Readiness", "pass" if is_ready else "fail", 
                        f"Orchestrator is {'ready' if is_ready else 'not ready'}")
        
        # Try to get status (should work even if not started)
        try:
            status = await test_orchestrator.get_status()
            result.add_check("Orchestrator Status Check", "pass", 
                           f"Status check successful (current state: {status.state.value if status else 'None'})")
        except Exception as e:
            result.add_check("Orchestrator Status Check", "warning", f"Status check failed: {e}")
        
    except Exception as e:
        result.add_check("Orchestrator Initialization", "fail", f"Failed to initialize orchestrator: {e}")
    
    return result


async def main():
    """Main entry point"""
    try:
        result = await diagnose_pipeline()
        result.print_summary()
        
        # Exit with error code if there are failures
        if result.errors:
            print("\n" + "="*80)
            print("DIAGNOSTIC COMPLETED WITH ERRORS")
            print("="*80)
            sys.exit(1)
        elif result.warnings:
            print("\n" + "="*80)
            print("DIAGNOSTIC COMPLETED WITH WARNINGS")
            print("="*80)
            sys.exit(0)
        else:
            print("\n" + "="*80)
            print("DIAGNOSTIC COMPLETED SUCCESSFULLY")
            print("="*80)
            sys.exit(0)
    except Exception as e:
        logger.error(f"Diagnostic failed with exception: {e}", exc_info=True)
        sys.exit(1)


if __name__ == "__main__":
    asyncio.run(main())

