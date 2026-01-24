"""
Schema Normalizers for P&L Calculation

Handles field name normalization from raw journal entries and events to canonical internal format.
All downstream code assumes canonical snake_case field names.
"""
import logging
from typing import Dict, Optional, Any

logger = logging.getLogger(__name__)


def normalize_journal_entry(raw: Dict[str, Any]) -> Dict[str, Any]:
    """
    Normalize ExecutionJournalEntry JSON to canonical format.
    
    Raw journal uses PascalCase (IntentId, TradingDate, FillPrice, etc.)
    Canonical format uses snake_case (intent_id, trading_date, fill_price, etc.)
    
    Returns canonical dict with fields:
    - intent_id, trading_date, stream, instrument, direction
    - entry_price, entry_qty
    - stop_price, target_price (optional)
    - costs_dollars (total: SlippageDollars + Commission + Fees)
    """
    canonical = {}
    
    # Intent metadata
    canonical["intent_id"] = raw.get("IntentId") or raw.get("intent_id") or ""
    canonical["trading_date"] = raw.get("TradingDate") or raw.get("trading_date") or ""
    canonical["stream"] = raw.get("Stream") or raw.get("stream") or ""
    canonical["instrument"] = raw.get("Instrument") or raw.get("instrument") or ""
    canonical["direction"] = raw.get("Direction") or raw.get("direction")
    
    # Entry fill data
    # FillPrice is the actual fill price (same as ActualFillPrice)
    fill_price = raw.get("FillPrice") or raw.get("fill_price") or raw.get("ActualFillPrice")
    fill_qty = raw.get("FillQuantity") or raw.get("fill_quantity")
    
    # EntryPrice is the intended entry price (may differ from fill due to slippage)
    entry_price_intended = raw.get("EntryPrice") or raw.get("entry_price")
    
    # Use FillPrice as entry_price (actual fill), EntryPrice as intended_entry_price
    canonical["entry_price"] = float(fill_price) if fill_price is not None else None
    canonical["intended_entry_price"] = float(entry_price_intended) if entry_price_intended is not None else None
    canonical["entry_qty"] = int(fill_qty) if fill_qty is not None else None
    
    # Protective order prices (optional)
    canonical["stop_price"] = float(raw.get("StopPrice") or raw.get("stop_price")) if raw.get("StopPrice") or raw.get("stop_price") else None
    canonical["target_price"] = float(raw.get("TargetPrice") or raw.get("target_price")) if raw.get("TargetPrice") or raw.get("target_price") else None
    
    # Costs
    slippage_dollars = raw.get("SlippageDollars") or raw.get("slippage_dollars") or 0
    commission = raw.get("Commission") or raw.get("commission") or 0
    fees = raw.get("Fees") or raw.get("fees") or 0
    
    # Use TotalCost if available, otherwise sum components
    total_cost = raw.get("TotalCost") or raw.get("total_cost")
    if total_cost is not None:
        canonical["costs_dollars"] = float(total_cost)
    else:
        canonical["costs_dollars"] = float(slippage_dollars or 0) + float(commission or 0) + float(fees or 0)
    
    # Timestamps
    canonical["entry_filled_at"] = raw.get("EntryFilledAt") or raw.get("entry_filled_at")
    canonical["entry_submitted_at"] = raw.get("EntrySubmittedAt") or raw.get("entry_submitted_at")
    
    return canonical


def normalize_execution_filled(raw_event: Dict[str, Any]) -> Dict[str, Any]:
    """
    Normalize EXECUTION_FILLED event to canonical format.
    
    Event structure: { "event_type": "EXECUTION_FILLED", "data": { ... } }
    
    Returns canonical dict with fields:
    - intent_id, fill_price, fill_qty, order_type, timestamp_utc, stream, instrument
    """
    data = raw_event.get("data", {})
    
    canonical = {}
    canonical["intent_id"] = data.get("intent_id") or raw_event.get("intent_id") or ""
    
    # Fill data
    fill_price = data.get("fill_price") or data.get("FillPrice")
    fill_qty = data.get("fill_quantity") or data.get("fill_qty") or data.get("FillQuantity")
    
    canonical["fill_price"] = float(fill_price) if fill_price is not None else None
    canonical["fill_qty"] = int(fill_qty) if fill_qty is not None else None
    
    # Order type/role (STOP, TARGET, ENTRY, etc.)
    canonical["order_type"] = data.get("order_type") or data.get("OrderType") or data.get("order_role") or ""
    
    # Timestamp
    canonical["timestamp_utc"] = raw_event.get("timestamp_utc") or raw_event.get("ts_utc") or data.get("timestamp_utc")
    
    # Stream and instrument (may be at top level or in data)
    canonical["stream"] = raw_event.get("stream") or data.get("stream") or data.get("stream_id") or ""
    canonical["instrument"] = raw_event.get("instrument") or data.get("instrument") or ""
    
    return canonical


def normalize_intent_exit_fill(raw_event: Dict[str, Any]) -> Dict[str, Any]:
    """
    Normalize INTENT_EXIT_FILL event to canonical format.
    
    Event structure: { "event_type": "INTENT_EXIT_FILL", "data": { ... } }
    
    Returns canonical dict with fields:
    - intent_id, exit_filled_qty_cum (cumulative), entry_filled_qty, remaining_exposure, timestamp_utc
    """
    data = raw_event.get("data", {})
    
    canonical = {}
    canonical["intent_id"] = data.get("intent_id") or raw_event.get("intent_id") or ""
    
    # Exit fill quantities (cumulative)
    exit_filled_qty = data.get("exit_filled_qty") or data.get("exit_qty") or 0
    entry_filled_qty = data.get("entry_filled_qty") or 0
    remaining_exposure = data.get("remaining_exposure") or data.get("remaining") or 0
    
    canonical["exit_filled_qty_cum"] = int(exit_filled_qty) if exit_filled_qty else 0
    canonical["entry_filled_qty"] = int(entry_filled_qty) if entry_filled_qty else 0
    canonical["remaining_exposure"] = int(remaining_exposure) if remaining_exposure else 0
    
    # Timestamp
    canonical["timestamp_utc"] = raw_event.get("timestamp_utc") or raw_event.get("ts_utc") or data.get("timestamp_utc")
    
    # Stream and instrument (may be at top level or in data)
    canonical["stream"] = raw_event.get("stream") or data.get("stream") or data.get("stream_id") or ""
    canonical["instrument"] = raw_event.get("instrument") or data.get("instrument") or ""
    
    return canonical
