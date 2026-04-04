"""
Watchdog API Router

API endpoints for Live Execution Watchdog + Execution Journal UI.
"""
import json
import logging
from pathlib import Path
from typing import Dict, List, Optional
from datetime import datetime, timezone, timedelta
from fastapi import APIRouter, HTTPException, Query
from pydantic import BaseModel

# Calculate project root (watchdog/backend/routers/watchdog.py -> QTSW2 root)
QTSW2_ROOT = Path(__file__).parent.parent.parent.parent

# Import watchdog aggregator
import sys
sys.path.insert(0, str(QTSW2_ROOT))
from modules.watchdog.aggregator import WatchdogAggregator, _read_last_lines
from modules.watchdog.incident_recorder import get_recent_incidents, get_incident_by_id, get_active_incidents
from modules.watchdog.slot_lifecycle_builder import build_slot_lifecycle
from modules.watchdog.incident_correlator import CASCADE_UPSTREAM
from modules.watchdog.reliability_metrics import get_reliability_metrics
from modules.watchdog.metrics_history import (
    aggregate_incidents_by_week,
    aggregate_incidents_by_month,
    get_metrics_history,
)
from modules.watchdog.config import (
    FRONTEND_FEED_FILE,
    EXECUTION_JOURNALS_DIR,
    EXECUTION_SUMMARIES_DIR,
    ROBOT_JOURNAL_DIR,
    FRONTEND_FEED_FILE,
)
from modules.watchdog.websocket_tracker import get_tracker
from modules.watchdog.market_calendar import get_market_state

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


@router.get("/market-state")
async def market_state():
    """CME-style market session state (Chicago clock + modules/config/cme_holidays_*.json)."""
    return get_market_state()


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


@router.get("/operator-snapshot")
async def get_operator_snapshot(
    n_events: int = Query(500, ge=100, le=2000, description="Number of recent events to derive from"),
):
    """Get deterministic per-instrument operator snapshot (Phase 1). Read-only derivation."""
    try:
        aggregator = get_aggregator()
        snapshot = aggregator.get_operator_snapshot(n_events=n_events)
        return {"snapshot": snapshot}
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting operator snapshot: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting operator snapshot: {str(e)}")


@router.get("/alerts")
async def get_alerts(
    active_only: bool = Query(False, description="Return only active alerts"),
    since_hours: Optional[float] = Query(None, description="Filter to alerts since N hours ago"),
    limit: int = Query(200, ge=1, le=500, description="Max records to return"),
):
    """Get active alerts and recent alert history from Phase 1 ledger."""
    try:
        aggregator = get_aggregator()
        result = aggregator.get_alerts(active_only=active_only, since_hours=since_hours, limit=limit)
        return result
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting alerts: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting alerts: {str(e)}")


@router.get("/events")
async def get_events(
    run_id: Optional[str] = Query(None, description="Current engine run_id (optional, will use current if not provided)"),
    since_seq: int = Query(0, description="Last processed event_seq")
):
    """Get events since last cursor position (incremental tailing)."""
    try:
        aggregator = get_aggregator()
        
        # Always get run_id from BOTTOM of feed (most recent event)
        # Never use client's run_id - it may be stale, causing us to filter out newest events
        current_run_id = aggregator.get_current_run_id()
        if not current_run_id:
            return {
                "run_id": None,
                "events": [],
                "next_seq": 0
            }
        
        # Always query by current_run_id - events at bottom of file are from current run
        events = aggregator.get_events_since(current_run_id, since_seq)
        
        # Debug: Check if ENGINE_TICK_CALLSITE events are in the response
        tick_callsite_events = [e for e in events if e.get("event_type") == "ENGINE_TICK_CALLSITE"]
        if tick_callsite_events:
            logger.debug(f"API returning {len(tick_callsite_events)} ENGINE_TICK_CALLSITE event(s) to frontend")
        
        # Get next_seq (highest event_seq in response, or since_seq if no events)
        next_seq = since_seq
        if events:
            next_seq = max(e.get("event_seq", 0) for e in events)

        # Phase 4: Add event_id for REST/WS dedupe consistency
        events_with_id = [
            {**e, "event_id": e.get("event_id") or f"{e.get('run_id', '')}:{e.get('event_seq', 0)}"}
            for e in events
        ]

        # Always return current_run_id so frontend can detect stale run and reset
        return {
            "run_id": current_run_id,
            "events": events_with_id,
            "next_seq": next_seq
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting events: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting events: {str(e)}")


@router.get("/ingestion-stats")
async def get_ingestion_stats():
    """Get ingestion telemetry (tail read duration, loop duration, parse rate)."""
    try:
        aggregator = get_aggregator()
        return aggregator.get_ingestion_stats()
    except Exception as e:
        logger.error(f"Error getting ingestion stats: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=f"Error getting ingestion stats: {str(e)}")


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


@router.get("/open-journals")
async def get_open_journals(
    include_previous_days: int = Query(0, ge=0, description="Include journals from previous N days (0 = all)")
):
    """
    Get all open execution journals (EntryFilled && !TradeCompleted).
    Operational visibility for carry-over positions from previous days.
    """
    entries = []
    if not EXECUTION_JOURNALS_DIR.exists():
        return {"entries": entries, "count": 0}
    cutoff_date = None
    if include_previous_days > 0:
        cutoff_date = (datetime.now(timezone.utc) - timedelta(days=include_previous_days)).date()
    for journal_file in EXECUTION_JOURNALS_DIR.glob("*.json"):
        try:
            with open(journal_file, 'r') as f:
                entry = json.load(f)
            if not entry.get("EntryFilled") or entry.get("TradeCompleted"):
                continue
            if entry.get("EntryFilledQuantityTotal", 0) <= 0 and entry.get("FillQuantity", 0) <= 0:
                continue
            if cutoff_date:
                td_str = entry.get("TradingDate") or journal_file.stem.split("_")[0]
                try:
                    entry_date = datetime.strptime(td_str, "%Y-%m-%d").date()
                    if entry_date < cutoff_date:
                        continue
                except (ValueError, IndexError):
                    pass
            entry = _convert_entry_timestamps_to_chicago(entry)
            entries.append(entry)
        except Exception as e:
            logger.debug(f"Skip journal {journal_file.name}: {e}")
    return {"entries": entries, "count": len(entries)}


@router.get("/incidents")
async def get_incidents(
    limit: int = Query(50, ge=1, le=200, description="Max incidents to return"),
):
    """Get recent incidents from incidents.jsonl (Phase 6)."""
    try:
        incidents = get_recent_incidents(limit=limit)
        return {"incidents": incidents, "count": len(incidents)}
    except Exception as e:
        logger.error(f"Error getting incidents: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/incidents/active")
async def get_active_incidents_endpoint():
    """
    Get currently active incidents (ongoing, not yet resolved).
    Returns type, started time (Chicago), duration_sec, instruments.
    """
    try:
        import pytz
        chicago_tz = pytz.timezone("America/Chicago")
        now = datetime.now(timezone.utc)
        raw = get_active_incidents()
        active = []
        for incident_type, info in raw.items():
            start_ts = info.get("start_ts")
            if start_ts is None:
                continue
            if hasattr(start_ts, "total_seconds"):
                duration_sec = int((now - start_ts).total_seconds())
            else:
                try:
                    start_dt = datetime.fromisoformat(str(start_ts).replace("Z", "+00:00"))
                    if start_dt.tzinfo is None:
                        start_dt = start_dt.replace(tzinfo=timezone.utc)
                    duration_sec = int((now - start_dt).total_seconds())
                except Exception:
                    duration_sec = 0
            started_chicago = start_ts.astimezone(chicago_tz).strftime("%H:%M:%S") if hasattr(start_ts, "astimezone") else str(start_ts)
            active.append({
                "type": incident_type,
                "incident_id": info.get("incident_id"),
                "started": started_chicago,
                "started_iso": start_ts.isoformat() if hasattr(start_ts, "isoformat") else str(start_ts),
                "duration_sec": duration_sec,
                "instruments": sorted(info.get("instruments", [])),
            })
        return {"active": active, "count": len(active)}
    except Exception as e:
        logger.error(f"Error getting active incidents: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/slot-lifecycle")
async def get_slot_lifecycle():
    """
    Get slot lifecycle state (forced flatten, reentry, slot expiry) per stream.
    Computed in-memory from event feed. No persistence.
    """
    try:
        aggregator = get_aggregator()
        events = aggregator.get_events_for_slot_lifecycle(500)
        slots = build_slot_lifecycle(events)
        return slots
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting slot lifecycle: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/metrics")
async def get_metrics(
    window_hours: float = Query(24, ge=0.25, le=168, description="Time window in hours"),
):
    """Get reliability metrics from incidents (Phase 6)."""
    try:
        metrics = get_reliability_metrics(window_hours=window_hours)
        return metrics
    except Exception as e:
        logger.error(f"Error getting metrics: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/metrics/history")
async def get_metrics_history_endpoint(
    granularity: str = Query("week", description="week or month"),
    limit: int = Query(52, ge=1, le=104, description="Max records"),
):
    """Phase 8: Long-term reliability trends (by week or month)."""
    try:
        if granularity == "month":
            data = aggregate_incidents_by_month()
        else:
            data = aggregate_incidents_by_week()
        history = get_metrics_history(limit=limit)
        return {
            "by_period": data[-limit:] if data else [],
            "stored_history": history,
        }
    except Exception as e:
        logger.error(f"Error getting metrics history: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/instrument-health")
async def get_instrument_health():
    """Get per-instrument data status (OK / DATA STALLED / etc.) from aggregator status (Phase 6)."""
    try:
        aggregator = get_aggregator()
        status = aggregator.get_watchdog_status()
        data_stall = status.get("data_stall_detected") or {}
        instruments: List[Dict] = []
        for inst_key, info in data_stall.items():
            stall_detected = info.get("stall_detected", False)
            instruments.append({
                "instrument": inst_key,
                "status": "DATA_STALLED" if stall_detected else "OK",
                "last_bar_chicago": info.get("last_bar_chicago"),
                "elapsed_seconds": info.get("elapsed_seconds"),
            })
        return {"instruments": instruments, "count": len(instruments)}
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting instrument health: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/incidents/cascade")
async def get_incident_cascade():
    """Phase 7: Event correlation cascade map (CONNECTION_LOST -> DATA_STALL -> ENGINE_STALLED)."""
    return {"cascade": {k: list(v) for k, v in CASCADE_UPSTREAM.items()}}


@router.get("/incidents/{incident_id}/events")
async def get_incident_events(incident_id: str):
    """
    Phase 7: Load events from frontend_feed in window [incident_start - 60s, incident_end + 60s].
    For post-mortem debugging.
    """
    try:
        incident = get_incident_by_id(incident_id)
        if not incident:
            raise HTTPException(status_code=404, detail=f"Incident {incident_id} not found")
        start_str = incident.get("start_ts")
        end_str = incident.get("end_ts")
        if not start_str or not end_str:
            raise HTTPException(status_code=400, detail="Incident missing start_ts or end_ts")
        from datetime import timedelta
        start_dt = datetime.fromisoformat(start_str.replace("Z", "+00:00"))
        end_dt = datetime.fromisoformat(end_str.replace("Z", "+00:00"))
        if start_dt.tzinfo is None:
            start_dt = start_dt.replace(tzinfo=timezone.utc)
        if end_dt.tzinfo is None:
            end_dt = end_dt.replace(tzinfo=timezone.utc)
        window_start = start_dt - timedelta(seconds=60)
        window_end = end_dt + timedelta(seconds=60)

        from modules.watchdog.replay_helpers import load_incident_events
        events = load_incident_events(window_start, window_end)
        return {
            "incident_id": incident_id,
            "incident": incident,
            "events": events,
            "count": len(events),
            "window_start": window_start.isoformat(),
            "window_end": window_end.isoformat(),
        }
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting incident events: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


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
                # Slot journals use PascalCase (Stream, Committed, LastState, LastUpdateUtc)
                stream = journal.get("Stream") or journal.get("stream")
                trading_date_val = journal.get("TradingDate") or journal.get("trading_date")
                committed = journal.get("Committed", journal.get("committed", False))
                commit_reason = journal.get("CommitReason") or journal.get("commit_reason")
                last_state = journal.get("LastState") or journal.get("last_state", "")
                last_update_utc = journal.get("LastUpdateUtc") or journal.get("last_update_utc", "")

                stream_state = {
                    "stream": stream,
                    "trading_date": trading_date_val,
                    "committed": committed,
                    "commit_reason": commit_reason,
                    "state": last_state,
                    "last_update_chicago": _convert_utc_to_chicago(last_update_utc)
                }
                streams.append(stream_state)
        
        except Exception as e:
            logger.warning(f"Error reading stream journal {journal_file}: {e}")
    
    return {
        "trading_date": trading_date,
        "streams": streams
    }


@router.get("/journal/daily")
async def get_daily_journal(
    trading_date: str = Query(..., description="Trading date (YYYY-MM-DD)")
):
    """
    Unified daily journal: streams, trades, total PnL, and summary.
    Combines execution journals, slot journals, ledger builder, and execution summary.
    """
    try:
        aggregator = get_aggregator()
        return aggregator.get_daily_journal(trading_date)
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Error getting daily journal: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


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
        
        # Read recent events (tail-only, no full file load)
        recent_lines = _read_last_lines(FRONTEND_FEED_FILE, 5000)
        
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

@router.get("/fill-metrics")
async def get_fill_metrics(
    date: Optional[str] = Query(None, description="Trading date (YYYY-MM-DD). Default: today (Chicago)"),
    stream: Optional[str] = Query(None, description="Optional stream filter")
):
    """
    Get fill metrics for execution logging hygiene.
    
    Returns: fill_coverage_rate, unmapped_rate, null_trading_date_rate, total_fills, etc.
    Targets: coverage=100%, unmapped=0, null_td=0.
    """
    try:
        import pytz
        if not date:
            chicago_tz = pytz.timezone("America/Chicago")
            date = datetime.now(chicago_tz).strftime("%Y-%m-%d")
        from modules.watchdog.pnl.fill_metrics import compute_fill_metrics
        metrics = compute_fill_metrics(date, stream)
        metrics["fill_health_ok"] = (
            metrics.get("fill_coverage_rate", 1.0) >= 1.0
            and metrics.get("unmapped_rate", 0) <= 0
            and metrics.get("null_trading_date_rate", 0) <= 0
        )
        return metrics
    except Exception as e:
        logger.error(f"Error computing fill metrics: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail=str(e))


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
