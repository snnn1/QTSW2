"""
Dashboard snapshot endpoints - Save dashboard state as git commit
"""
import json
import logging
import subprocess
from pathlib import Path
from datetime import datetime, timezone
from fastapi import APIRouter, HTTPException
from typing import Dict, Any, Optional

router = APIRouter(prefix="/api/dashboard", tags=["dashboard"])
logger = logging.getLogger(__name__)

# Get project root
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent
SNAPSHOTS_DIR = QTSW2_ROOT / "snapshots"
SNAPSHOTS_DIR.mkdir(parents=True, exist_ok=True)


def get_orchestrator():
    """Get orchestrator instance"""
    try:
        from ..main import orchestrator_instance
    except ImportError:
        from main import orchestrator_instance
    return orchestrator_instance


async def get_scheduler_status() -> Dict[str, Any]:
    """Get scheduler status"""
    try:
        from ..routers.schedule import get_scheduler_status as _get_scheduler_status
    except ImportError:
        from routers.schedule import get_scheduler_status as _get_scheduler_status
    
    try:
        return await _get_scheduler_status()
    except Exception as e:
        logger.warning(f"Failed to get scheduler status: {e}")
        return {}


def get_file_counts() -> Dict[str, Any]:
    """Get file counts"""
    try:
        data_raw = QTSW2_ROOT / "data" / "raw"
        data_translated = QTSW2_ROOT / "data" / "translated"
        analyzer_runs = QTSW2_ROOT / "data" / "analyzed"
        
        raw_count = len(list(data_raw.glob("*.csv"))) if data_raw.exists() else 0
        translated_count = len(list(data_translated.rglob("*.parquet"))) if data_translated.exists() else 0
        analyzed_count = len(list(analyzer_runs.rglob("*.parquet"))) if analyzer_runs.exists() else 0
        
        return {
            "raw_files": raw_count,
            "translated_files": translated_count,
            "analyzed_files": analyzed_count
        }
    except Exception as e:
        logger.warning(f"Failed to get file counts: {e}")
        return {}


async def collect_dashboard_state() -> Dict[str, Any]:
    """Collect all dashboard state"""
    orchestrator = get_orchestrator()
    state = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "pipeline": {},
        "scheduler": {},
        "schedule": {},
        "metrics": {},
        "events": [],
    }
    
    # Get pipeline snapshot (includes status, events, lock info, next scheduled run)
    if orchestrator:
        try:
            snapshot = await orchestrator.get_snapshot()
            state["pipeline"] = {
                "status": snapshot.get("status"),
                "lock_info": snapshot.get("lock_info"),
                "next_scheduled_run": snapshot.get("next_scheduled_run"),
                "event_source": snapshot.get("event_source"),
            }
            # Include recent events (limit to last 100 for snapshot)
            state["events"] = snapshot.get("recent_events", [])[-100:]
            state["run_events"] = snapshot.get("run_events", [])[-100:]
        except Exception as e:
            logger.warning(f"Failed to get pipeline snapshot: {e}")
            state["pipeline"]["error"] = str(e)
    
    # Get scheduler status
    try:
        scheduler_status = await get_scheduler_status()
        state["scheduler"] = scheduler_status
    except Exception as e:
        logger.warning(f"Failed to get scheduler status: {e}")
        state["scheduler"] = {"error": str(e)}
    
    # Get schedule config
    try:
        from ..config import load_schedule_config
        schedule_config = load_schedule_config()
        state["schedule"] = schedule_config.dict() if hasattr(schedule_config, 'dict') else schedule_config
    except Exception as e:
        logger.warning(f"Failed to load schedule config: {e}")
        state["schedule"]["error"] = str(e)
    
    # Get file counts/metrics
    state["metrics"] = get_file_counts()
    
    return state


def run_git_command(cmd: list, cwd: Optional[Path] = None) -> tuple[str, str, int]:
    """Run a git command and return stdout, stderr, returncode"""
    try:
        result = subprocess.run(
            ["git"] + cmd,
            cwd=cwd or QTSW2_ROOT,
            capture_output=True,
            text=True,
            timeout=30
        )
        return result.stdout, result.stderr, result.returncode
    except subprocess.TimeoutExpired:
        return "", "Git command timed out", 1
    except FileNotFoundError:
        return "", "Git not found. Is git installed?", 1
    except Exception as e:
        return "", f"Git command failed: {e}", 1


@router.post("/snapshot/commit")
async def create_snapshot_commit():
    """
    Create a git commit with current dashboard state.
    
    This endpoint:
    1. Collects all dashboard state (pipeline, scheduler, events, metrics)
    2. Saves it to a JSON file in snapshots/ directory
    3. Commits it to git with a descriptive message
    4. Returns the commit hash
    """
    try:
        # Check if we're in a git repository
        stdout, stderr, returncode = run_git_command(["rev-parse", "--git-dir"])
        if returncode != 0:
            raise HTTPException(
                status_code=400,
                detail="Not a git repository. Please initialize git first."
            )
        
        # Collect dashboard state
        logger.info("Collecting dashboard state...")
        state = await collect_dashboard_state()
        
        # Create snapshot filename with timestamp
        timestamp = datetime.now(timezone.utc)
        snapshot_filename = f"snapshot_{timestamp.strftime('%Y%m%d_%H%M%S')}.json"
        snapshot_path = SNAPSHOTS_DIR / snapshot_filename
        
        # Save state to file
        logger.info(f"Saving snapshot to {snapshot_path}...")
        with open(snapshot_path, 'w', encoding='utf-8') as f:
            json.dump(state, f, indent=2, default=str)
        
        # Stage the snapshot file
        logger.info("Staging snapshot file...")
        stdout, stderr, returncode = run_git_command(["add", str(snapshot_path.relative_to(QTSW2_ROOT))])
        if returncode != 0:
            raise HTTPException(
                status_code=500,
                detail=f"Failed to stage snapshot file: {stderr}"
            )
        
        # Create commit message
        pipeline_state = state.get("pipeline", {}).get("status", {})
        pipeline_state_value = pipeline_state.get("state", "unknown") if isinstance(pipeline_state, dict) else "unknown"
        run_id = pipeline_state.get("run_id", "")[:8] if isinstance(pipeline_state, dict) else ""
        
        commit_message = (
            f"Dashboard snapshot: {timestamp.strftime('%Y-%m-%d %H:%M:%S UTC')}\n\n"
            f"Pipeline state: {pipeline_state_value}"
        )
        if run_id:
            commit_message += f" (run: {run_id})"
        
        commit_message += (
            f"\nScheduler: {state.get('scheduler', {}).get('enabled', 'unknown')}\n"
            f"Events: {len(state.get('events', []))} recent events\n"
            f"Metrics: {state.get('metrics', {}).get('raw_files', 0)} raw files"
        )
        
        # Create commit
        logger.info("Creating git commit...")
        stdout, stderr, returncode = run_git_command([
            "commit",
            "-m", commit_message
        ])
        
        if returncode != 0:
            # If commit fails, unstage the file
            run_git_command(["reset", "HEAD", str(snapshot_path.relative_to(QTSW2_ROOT))])
            raise HTTPException(
                status_code=500,
                detail=f"Failed to create git commit: {stderr or stdout}"
            )
        
        # Get commit hash
        stdout, stderr, returncode = run_git_command(["rev-parse", "HEAD"])
        commit_hash = stdout.strip() if returncode == 0 else "unknown"
        
        logger.info(f"Snapshot committed successfully: {commit_hash[:8]}")
        
        return {
            "success": True,
            "commit_hash": commit_hash,
            "snapshot_file": str(snapshot_path.relative_to(QTSW2_ROOT)),
            "timestamp": timestamp.isoformat(),
            "message": "Dashboard state saved and committed to git"
        }
    
    except HTTPException:
        raise
    except Exception as e:
        logger.exception("Failed to create snapshot commit")
        raise HTTPException(
            status_code=500,
            detail=f"Failed to create snapshot commit: {str(e)}"
        )


@router.get("/snapshot/list")
async def list_snapshots():
    """List all snapshot files"""
    try:
        snapshots = []
        for snapshot_file in sorted(SNAPSHOTS_DIR.glob("snapshot_*.json"), reverse=True):
            try:
                stat = snapshot_file.stat()
                snapshots.append({
                    "filename": snapshot_file.name,
                    "path": str(snapshot_file.relative_to(QTSW2_ROOT)),
                    "size": stat.st_size,
                    "modified": datetime.fromtimestamp(stat.st_mtime).isoformat(),
                })
            except Exception as e:
                logger.warning(f"Failed to read snapshot {snapshot_file}: {e}")
        
        return {
            "snapshots": snapshots,
            "count": len(snapshots)
        }
    except Exception as e:
        logger.exception("Failed to list snapshots")
        raise HTTPException(
            status_code=500,
            detail=f"Failed to list snapshots: {str(e)}"
        )

