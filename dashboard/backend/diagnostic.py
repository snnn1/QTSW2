"""
Pipeline System Diagnostic Tool
Checks system health, configuration, and recent activity
"""

import asyncio
import json
import sys
from pathlib import Path
from datetime import datetime
from typing import Dict, Any, List

# Add project root to path
qtsw2_root = Path(__file__).parent.parent.parent
if str(qtsw2_root) not in sys.path:
    sys.path.insert(0, str(qtsw2_root))

try:
    from dashboard.backend.orchestrator.service import PipelineOrchestrator
    from dashboard.backend.orchestrator.config import OrchestratorConfig
    from dashboard.backend.orchestrator.state import PipelineRunState
    from automation.config import PipelineConfig
except ImportError as e:
    print(f"❌ Import error: {e}")
    sys.exit(1)


class DiagnosticTool:
    """Diagnostic tool for pipeline system"""
    
    def __init__(self):
        self.qtsw2_root = qtsw2_root
        self.issues: List[str] = []
        self.warnings: List[str] = []
        self.info: List[str] = []
    
    def check(self, condition: bool, message: str, is_warning: bool = False):
        """Record check result"""
        if condition:
            self.info.append(f"✓ {message}")
        elif is_warning:
            self.warnings.append(f"⚠ {message}")
        else:
            self.issues.append(f"❌ {message}")
    
    def print_section(self, title: str):
        """Print section header"""
        print(f"\n{'='*60}")
        print(f"  {title}")
        print(f"{'='*60}")
    
    def check_file_system(self):
        """Check file system structure"""
        self.print_section("File System")
        
        # Check directories
        dirs_to_check = [
            ("Project Root", self.qtsw2_root),
            ("Raw Data", self.qtsw2_root / "data" / "raw"),
            ("Processed Data", self.qtsw2_root / "data" / "processed"),
            ("Event Logs", self.qtsw2_root / "automation" / "logs" / "events"),
            ("Pipeline Logs", self.qtsw2_root / "automation" / "logs"),
        ]
        
        for name, path in dirs_to_check:
            exists = path.exists()
            if exists:
                msg = f"✓ {name} directory exists: {path}"
                print(f"  {msg}")
                self.info.append(msg)
                try:
                    files = list(path.glob("*"))
                    item_msg = f"  → {len(files)} items in {name}"
                    print(f"  {item_msg}")
                    self.info.append(item_msg)
                except Exception as e:
                    warn_msg = f"  → Cannot list {name}: {e}"
                    print(f"  {warn_msg}")
                    self.warnings.append(warn_msg)
            else:
                issue_msg = f"❌ {name} directory missing: {path}"
                print(f"  {issue_msg}")
                self.issues.append(issue_msg)
    
    def check_raw_files(self):
        """Check for raw CSV files"""
        self.print_section("Raw Data Files")
        
        raw_dir = self.qtsw2_root / "data" / "raw"
        if raw_dir.exists():
            csv_files = list(raw_dir.glob("*.csv"))
            self.check(len(csv_files) > 0, f"Found {len(csv_files)} raw CSV file(s)")
            
            if csv_files:
                print(f"\n  Raw files found:")
                for f in csv_files[:10]:  # Show first 10
                    size_mb = f.stat().st_size / (1024 * 1024)
                    print(f"    • {f.name} ({size_mb:.2f} MB)")
                if len(csv_files) > 10:
                    print(f"    ... and {len(csv_files) - 10} more")
        else:
            self.issues.append("Raw data directory does not exist")
    
    def check_processed_files(self):
        """Check for processed files"""
        self.print_section("Processed Data Files")
        
        processed_dir = self.qtsw2_root / "data" / "processed"
        if processed_dir.exists():
            parquet_files = list(processed_dir.glob("*.parquet"))
            csv_files = list(processed_dir.glob("*.csv"))
            
            total = len(parquet_files) + len(csv_files)
            if total > 0:
                msg = f"✓ Found {total} processed file(s) ({len(parquet_files)} parquet, {len(csv_files)} CSV)"
                print(f"  {msg}")
                self.info.append(msg)
            else:
                warn_msg = f"⚠ No processed files found in {processed_dir}"
                print(f"  {warn_msg}")
                self.warnings.append(warn_msg)
        else:
            warn_msg = "⚠ Processed data directory does not exist"
            print(f"  {warn_msg}")
            self.warnings.append(warn_msg)
    
    def check_configuration(self):
        """Check configuration"""
        self.print_section("Configuration")
        
        try:
            config = OrchestratorConfig.from_environment(qtsw2_root=self.qtsw2_root)
            msg = "✓ Orchestrator config loaded"
            print(f"  {msg}")
            self.info.append(msg)
            print(f"    → Event logs dir: {config.event_logs_dir}")
            print(f"    → Lock dir: {config.lock_dir}")
            print(f"    → Stages configured: {len(config.stages)}")
            self.info.append(f"  → Event logs dir: {config.event_logs_dir}")
            self.info.append(f"  → Lock dir: {config.lock_dir}")
            self.info.append(f"  → Stages configured: {len(config.stages)}")
            
            for stage_name, stage_config in config.stages.items():
                stage_msg = f"    → {stage_name}: timeout={stage_config.timeout_sec}s, retries={stage_config.max_retries}"
                print(f"  {stage_msg}")
                self.info.append(f"  → {stage_name}: timeout={stage_config.timeout_sec}s, retries={stage_config.max_retries}")
        except Exception as e:
            issue_msg = f"❌ Failed to load orchestrator config: {e}"
            print(f"  {issue_msg}")
            self.issues.append(issue_msg)
        
        try:
            pipeline_config = PipelineConfig.from_environment()
            msg = "✓ Pipeline config loaded"
            print(f"  {msg}")
            self.info.append(msg)
            print(f"    → Translator script: {pipeline_config.translator_script}")
            print(f"    → Data raw: {pipeline_config.data_raw}")
            print(f"    → Data processed: {pipeline_config.data_processed}")
            self.info.append(f"  → Translator script: {pipeline_config.translator_script}")
            self.info.append(f"  → Data raw: {pipeline_config.data_raw}")
            self.info.append(f"  → Data processed: {pipeline_config.data_processed}")
        except Exception as e:
            issue_msg = f"❌ Failed to load pipeline config: {e}"
            print(f"  {issue_msg}")
            self.issues.append(issue_msg)
    
    async def check_orchestrator(self):
        """Check orchestrator status"""
        self.print_section("Orchestrator Status")
        
        try:
            config = OrchestratorConfig.from_environment(qtsw2_root=self.qtsw2_root)
            schedule_config_path = self.qtsw2_root / "automation" / "schedule_config.json"
            
            orchestrator = PipelineOrchestrator(
                config=config,
                schedule_config_path=schedule_config_path
            )
            
            # Get status
            status = await orchestrator.get_status()
            if status:
                msg = f"✓ Active pipeline run: {status.run_id[:8]}"
                print(f"  {msg}")
                self.info.append(msg)
                print(f"    → State: {status.state.value}")
                print(f"    → Stage: {status.current_stage.value if status.current_stage else 'None'}")
                print(f"    → Started: {status.started_at.isoformat() if status.started_at else 'Unknown'}")
                print(f"    → Updated: {status.updated_at.isoformat() if status.updated_at else 'Unknown'}")
                self.info.append(f"  → State: {status.state.value}")
                self.info.append(f"  → Stage: {status.current_stage.value if status.current_stage else 'None'}")
                self.info.append(f"  → Started: {status.started_at.isoformat() if status.started_at else 'Unknown'}")
                self.info.append(f"  → Updated: {status.updated_at.isoformat() if status.updated_at else 'Unknown'}")
                if status.error:
                    error_msg = f"  → Error: {status.error}"
                    print(f"    {error_msg}")
                    self.issues.append(error_msg)
            else:
                msg = "✓ No active pipeline run (system idle)"
                print(f"  {msg}")
                self.info.append(msg)
            
            # Get snapshot
            snapshot = await orchestrator.get_snapshot()
            if snapshot:
                recent_events = snapshot.get("recent_events", [])
                self.info.append(f"  → Recent events: {len(recent_events)}")
                
                # Check for errors in recent events
                error_events = [e for e in recent_events if e.get("event") in ["error", "failure"]]
                if error_events:
                    self.warnings.append(f"  → Found {len(error_events)} error event(s) in recent history")
                    for event in error_events[-5:]:  # Show last 5 errors
                        self.warnings.append(f"    • {event.get('stage', 'unknown')}: {event.get('msg', 'No message')}")
            
            await orchestrator.stop()
            
        except Exception as e:
            self.issues.append(f"Failed to check orchestrator: {e}")
            import traceback
            self.warnings.append(f"Traceback: {traceback.format_exc()}")
    
    def check_recent_events(self):
        """Check recent event logs"""
        self.print_section("Recent Events")
        
        events_dir = self.qtsw2_root / "automation" / "logs" / "events"
        if not events_dir.exists():
            self.warnings.append("Events directory does not exist")
            return
        
        event_files = list(events_dir.glob("pipeline_*.jsonl"))
        if not event_files:
            self.warnings.append("No event log files found")
            return
        
        # Get most recent event file
        latest_file = max(event_files, key=lambda f: f.stat().st_mtime)
        self.info.append(f"Latest event log: {latest_file.name}")
        
        try:
            with open(latest_file, 'r') as f:
                lines = f.readlines()
                self.info.append(f"  → Total events: {len(lines)}")
                
                # Parse last 10 events
                recent_events = []
                for line in lines[-10:]:
                    try:
                        event = json.loads(line.strip())
                        recent_events.append(event)
                    except:
                        pass
                
                if recent_events:
                    print(f"\n  Last {len(recent_events)} events:")
                    for event in recent_events:
                        stage = event.get("stage", "unknown")
                        event_type = event.get("event", "unknown")
                        msg = event.get("msg", "No message")
                        timestamp = event.get("timestamp", "Unknown")
                        print(f"    [{timestamp}] {stage}/{event_type}: {msg}")
        except Exception as e:
            self.warnings.append(f"Failed to read event log: {e}")
    
    def check_schedule_config(self):
        """Check schedule configuration"""
        self.print_section("Schedule Configuration")
        
        schedule_file = self.qtsw2_root / "automation" / "schedule_config.json"
        if schedule_file.exists():
            try:
                with open(schedule_file, 'r') as f:
                    schedule = json.load(f)
                    schedule_time = schedule.get("schedule_time", "Unknown")
                    msg = f"✓ Schedule config found: {schedule_time}"
                    print(f"  {msg}")
                    self.info.append(msg)
            except Exception as e:
                issue_msg = f"❌ Failed to read schedule config: {e}"
                print(f"  {issue_msg}")
                self.issues.append(issue_msg)
        else:
            warn_msg = "⚠ Schedule config file not found"
            print(f"  {warn_msg}")
            self.warnings.append(warn_msg)
    
    def print_summary(self):
        """Print diagnostic summary"""
        self.print_section("Summary")
        
        print(f"\n  Issues: {len(self.issues)}")
        print(f"  Warnings: {len(self.warnings)}")
        print(f"  Info: {len(self.info)}")
        
        if self.issues:
            print(f"\n  ❌ ISSUES FOUND:")
            for issue in self.issues:
                print(f"    {issue}")
        
        if self.warnings:
            print(f"\n  ⚠ WARNINGS:")
            for warning in self.warnings:
                print(f"    {warning}")
        
        if not self.issues and not self.warnings:
            print(f"\n  ✓ All checks passed!")
    
    async def run_all_checks(self):
        """Run all diagnostic checks"""
        print("\n" + "="*60)
        print("  PIPELINE SYSTEM DIAGNOSTIC")
        print("="*60)
        print(f"  Time: {datetime.now().isoformat()}")
        print(f"  Project Root: {self.qtsw2_root}")
        
        self.check_file_system()
        self.check_raw_files()
        self.check_processed_files()
        self.check_configuration()
        self.check_schedule_config()
        self.check_recent_events()
        await self.check_orchestrator()
        self.print_summary()
        
        print("\n" + "="*60 + "\n")


async def main():
    """Main entry point"""
    diagnostic = DiagnosticTool()
    await diagnostic.run_all_checks()
    
    # Exit with error code if issues found
    if diagnostic.issues:
        sys.exit(1)
    elif diagnostic.warnings:
        sys.exit(0)  # Warnings are OK
    else:
        sys.exit(0)


if __name__ == "__main__":
    asyncio.run(main())

