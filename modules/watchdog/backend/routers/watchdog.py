"""
Watchdog API Router

API endpoints for Live Execution Watchdog + Execution Journal UI.
"""
import json
import logging
from pathlib import Path
from typing import Dict, List, Optional
from datetime import datetime
from fastapi import APIRouter, HTTPException, Query
from pydantic import BaseModel

# Calculate project root (watchdog/backend/routers/watchdog.py -> QTSW2 root)
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent

# Import watchdog aggregator
import sys
sys.path.insert(0, str(QTSW2_ROOT))
from modules.watchdog.aggregator import WatchdogAggregator
from modules.watchdog.config import (
    EXECUTION_JOURNALS_DIR,
    EXECUTION_SUMMARIES_DIR,
    ROBOT_JOURNAL_DIR,
    FRONTEND_FEED_FILE,
)
from modules.watchdog.websocket_tracker import get_tracker

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/api/watchdog", tags=["watchdog"])

# Global aggregator instance (will be set by main.py)
aggregator_instance: Optional[WatchdogAggregator] = None


def get_aggregator() -> WatchdogAggregator:
    """Get aggregator instance."""
    if aggregator_instance is None:
        error_msg = "Watchdog aggregator not initialized - check startup logs"
        print(f"ERROR: {error_msg}")
        logger.error(error_msg)
        raise HTTPException(status_code=503, detail=error_msg)
    return aggregator_instance


@router.get("/debug")
async def debug_watchdog():
    """Debug endpoint to check aggregator state."""
    return {
        "aggregator_instance": aggregator_instance is not None,
        "aggregator_type": str(type(aggregator_instance)) if aggregator_instance else None,
        "has_state_manager": hasattr(aggregator_instance, '_state_manager') if aggregator_instance else False,
        "has_event_feed": hasattr(aggregator_instance, '_event_feed') if aggregator_instance else False,
    }


@router.get("/status")
async def get_watchdog_status():
    """Get current WatchdogStatus (pre-computed by backend)."""
    try:
        aggregator = get_aggregator()
        status = aggregator.get_watchdog_status()
        return status
    except HTTPException:
        raise
    except Exception as e:
        import traceback
        error_trace = traceback.format_exc()
        logger.error(f"Error getting watchdog status: {e}\n{error_trace}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting watchdog status: {str(e)}")


@router.get("/events")
async def get_events(
    run_id: Optional[str] = Query(None, description="Current engine run_id (optional, will use current if not provided)"),
    since_seq: int = Query(0, description="Last processed event_seq")
):
    """Get events since last cursor position (incremental tailing)."""
    try:
        aggregator = get_aggregator()
        
        # If run_id not provided, get current run_id
        if not run_id:
            run_id = aggregator.get_current_run_id()
            if not run_id:
                return {
                    "run_id": None,
                    "events": [],
                    "next_seq": 0
                }
        
        events = aggregator.get_events_since(run_id, since_seq)
        
        # Debug: Check if ENGINE_TICK_CALLSITE events are in the response
        tick_callsite_events = [e for e in events if e.get("event_type") == "ENGINE_TICK_CALLSITE"]
        if tick_callsite_events:
            logger.debug(f"API returning {len(tick_callsite_events)} ENGINE_TICK_CALLSITE event(s) to frontend")
        
        # Get next_seq (highest event_seq in response, or since_seq if no events)
        next_seq = since_seq
        if events:
            next_seq = max(e.get("event_seq", 0) for e in events)
        
        return {
            "run_id": run_id,
            "events": events,
            "next_seq": next_seq
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting events: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting events: {str(e)}")


@router.get("/risk-gates")
async def get_risk_gates():
    """Get current RiskGateStatus (pre-computed by backend)."""
    try:
        aggregator = get_aggregator()
        return aggregator.get_risk_gate_status()
    except Exception as e:
        logger.error(f"Error getting risk gates: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting risk gates: {str(e)}")


@router.get("/unprotected-positions")
async def get_unprotected_positions():
    """Get current unprotected positions (pre-computed by backend)."""
    try:
        aggregator = get_aggregator()
        return aggregator.get_unprotected_positions()
    except Exception as e:
        logger.error(f"Error getting unprotected positions: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting unprotected positions: {str(e)}")


@router.get("/stream-states")
async def get_stream_states():
    """Get current stream states."""
    try:
        aggregator = get_aggregator()
        return aggregator.get_stream_states()
    except Exception as e:
        logger.error(f"Error getting stream states: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting stream states: {str(e)}")


@router.get("/active-intents")
async def get_active_intents():
    """Get current active intents."""
    try:
        aggregator = get_aggregator()
        return aggregator.get_active_intents()
    except Exception as e:
        logger.error(f"Error getting active intents: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting active intents: {str(e)}")


@router.get("/ws-health")
async def get_ws_health():
    """Get WebSocket health status and connection metrics."""
    try:
        tracker = get_tracker()
        snapshot = await tracker.get_snapshot()
        return snapshot
    except Exception as e:
        logger.error(f"Error getting WebSocket health: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting WebSocket health: {str(e)}")


# Historical journal endpoints

@router.get("/journal/execution")
async def get_execution_journal(
    trading_date: str = Query(..., description="Trading date (YYYY-MM-DD)"),
    stream: Optional[str] = Query(None, description="Filter by stream"),
    intent_id: Optional[str] = Query(None, description="Filter by intent_id")
):
    """Get ExecutionJournalEntry for historical review."""
    entries = []
    
    if not EXECUTION_JOURNALS_DIR.exists():
        return {"entries": []}
    
    if intent_id:
        # Single intent file
        journal_file = EXECUTION_JOURNALS_DIR / f"{trading_date}_{stream}_{intent_id}.json"
        if journal_file.exists():
            try:
                with open(journal_file, 'r') as f:
                    entry = json.load(f)
                    # Convert UTC timestamps to Chicago
                    entry = _convert_entry_timestamps_to_chicago(entry)
                    entries.append(entry)
            except Exception as e:
                logger.error(f"Error reading journal file {journal_file}: {e}")
    else:
        # All intents for trading_date (and optionally stream)
        pattern = f"{trading_date}_*.json" if not stream else f"{trading_date}_{stream}_*.json"
        for journal_file in EXECUTION_JOURNALS_DIR.glob(pattern):
            try:
                with open(journal_file, 'r') as f:
                    entry = json.load(f)
                    # Convert UTC timestamps to Chicago
                    entry = _convert_entry_timestamps_to_chicago(entry)
                    entries.append(entry)
            except Exception as e:
                logger.warning(f"Error reading journal file {journal_file}: {e}")
    
    return {"entries": entries}


@router.get("/journal/streams")
async def get_stream_journal(
    trading_date: str = Query(..., description="Trading date (YYYY-MM-DD)")
):
    """Get StreamState for all streams on trading date."""
    streams = []
    
    if not ROBOT_JOURNAL_DIR.exists():
        return {
            "trading_date": trading_date,
            "streams": []
        }
    
    for journal_file in ROBOT_JOURNAL_DIR.glob(f"{trading_date}_*.json"):
        try:
            with open(journal_file, 'r') as f:
                journal = json.load(f)
                stream = journal.get("stream")
                
                # Convert UTC timestamps to Chicago
                stream_state = {
                    "stream": stream,
                    "trading_date": journal.get("trading_date"),
                    "committed": journal.get("committed", False),
                    "commit_reason": journal.get("commit_reason"),
                    "state": journal.get("last_state", ""),
                    "last_update_chicago": _convert_utc_to_chicago(journal.get("last_update_utc", ""))
                }
                streams.append(stream_state)
        
        except Exception as e:
            logger.warning(f"Error reading stream journal {journal_file}: {e}")
    
    return {
        "trading_date": trading_date,
        "streams": streams
    }


@router.get("/journal/summary")
async def get_execution_summary(
    trading_date: str = Query(..., description="Trading date (YYYY-MM-DD)")
):
    """Get ExecutionSummary for trading date."""
    summary_file = EXECUTION_SUMMARIES_DIR / f"{trading_date}.json"
    
    if not summary_file.exists():
        raise HTTPException(status_code=404, detail=f"Summary not found for {trading_date}")
    
    try:
        with open(summary_file, 'r') as f:
            summary = json.load(f)
            return summary
    except Exception as e:
        logger.error(f"Error reading summary file {summary_file}: {e}")
        raise HTTPException(status_code=500, detail=f"Error reading summary: {e}")


@router.post("/reprocess-identity")
async def reprocess_identity():
    """
    Manually trigger reprocessing of the latest identity event.
    Useful for applying new extraction logic to existing events.
    """
    if aggregator_instance is None:
        raise HTTPException(status_code=503, detail="Watchdog aggregator not initialized")
    
    try:
        import json
        from datetime import datetime, timezone
        
        if not FRONTEND_FEED_FILE.exists():
            return {"success": False, "error": "Frontend feed file not found"}
        
        # Read recent events and find latest identity event
        with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
            all_lines = f.readlines()
            recent_lines = all_lines[-5000:] if len(all_lines) > 5000 else all_lines
        
        latest_identity_event = None
        latest_timestamp = None
        
        for line in recent_lines:
            if line.strip():
                try:
                    event = json.loads(line.strip())
                    if event.get('event_type') == 'IDENTITY_INVARIANTS_STATUS':
                        ts_str = event.get('timestamp_utc', '')
                        if ts_str:
                            try:
                                ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                                if ts.tzinfo is None:
                                    ts = ts.replace(tzinfo=timezone.utc)
                                if latest_timestamp is None or ts > latest_timestamp:
                                    latest_identity_event = event
                                    latest_timestamp = ts
                            except:
                                pass
                except:
                    continue
        
        if latest_identity_event:
            # Reprocess the event
            aggregator_instance._event_processor.process_event(latest_identity_event)
            
            # Get updated status
            status = aggregator_instance._state_manager.compute_watchdog_status()
            
            return {
                "success": True,
                "event_timestamp": latest_timestamp.isoformat()[:19] if latest_timestamp else None,
                "pass_value": status.get("last_identity_invariants_pass"),
                "violations": status.get("last_identity_violations", [])
            }
        else:
            return {"success": False, "error": "No identity events found"}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Failed to reprocess identity: {str(e)}")

@router.get("/stream-pnl")
async def get_stream_pnl(
    trading_date: str = Query(..., description="Trading date (YYYY-MM-DD)"),
    stream: Optional[str] = Query(None, description="Filter by stream")
):
    """
    Get realized P&L for stream(s).
    
    Separate endpoint - does not modify get_stream_states().
    Frontend fetches P&L separately and joins by stream id.
    """
    aggregator = get_aggregator()
    return aggregator.get_stream_pnl(trading_date, stream)


def _convert_utc_to_chicago(utc_timestamp_str: str) -> Optional[str]:
    """Convert UTC timestamp string to Chicago timezone."""
    if not utc_timestamp_str:
        return None
    
    try:
        import pytz
        chicago_tz = pytz.timezone("America/Chicago")
        dt_utc = datetime.fromisoformat(utc_timestamp_str.replace('Z', '+00:00'))
        if dt_utc.tzinfo is None:
            from datetime import timezone
            dt_utc = dt_utc.replace(tzinfo=timezone.utc)
        dt_chicago = dt_utc.astimezone(chicago_tz)
        return dt_chicago.isoformat()
    except Exception as e:
        logger.warning(f"Failed to convert timestamp {utc_timestamp_str}: {e}")
        return utc_timestamp_str


def _convert_entry_timestamps_to_chicago(entry: Dict) -> Dict:
    """Convert all UTC timestamp fields in entry to Chicago."""
    timestamp_fields = [
        "entry_submitted_at",
        "entry_filled_at",
        "rejected_at",
        "be_modified_at"
    ]
    
    for field in timestamp_fields:
        if field in entry and entry[field]:
            chicago_time = _convert_utc_to_chicago(entry[field])
            # Add _chicago suffix
            entry[f"{field}_chicago"] = chicago_time
    
    return entry
