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
    canonical["exit_filled_at"] = raw.get("ExitFilledAtUtc") or raw.get("CompletedAtUtc") or raw.get("exit_filled_at") or raw.get("completed_at_utc")

    # Trade completion (authoritative P&L source when TradeCompleted)
    canonical["trade_completed"] = raw.get("TradeCompleted", False) or raw.get("trade_completed", False)
    canonical["realized_pnl_gross"] = raw.get("RealizedPnLGross") or raw.get("realized_pnl_gross")
    canonical["realized_pnl_net"] = raw.get("RealizedPnLNet") or raw.get("realized_pnl_net")
    canonical["contract_multiplier"] = raw.get("ContractMultiplier") or raw.get("contract_multiplier")
    canonical["completion_reason"] = raw.get("CompletionReason") or raw.get("completion_reason")
    canonical["exit_order_type"] = raw.get("ExitOrderType") or raw.get("exit_order_type")
    canonical["exit_avg_fill_price"] = raw.get("ExitAvgFillPrice") or raw.get("exit_avg_fill_price")
    canonical["exit_filled_quantity_total"] = raw.get("ExitFilledQuantityTotal") or raw.get("exit_filled_quantity_total")
    canonical["be_modified"] = raw.get("BEModified", False) or raw.get("be_modified", False)

    return canonical


def normalize_execution_filled(raw_event: Dict[str, Any]) -> Dict[str, Any]:
    """
    Normalize EXECUTION_FILLED event to canonical format.
    
    Event structure: { "event_type": "EXECUTION_FILLED", "data": { ... } }
    
    Returns canonical dict with fields:
    - intent_id, fill_price, fill_qty, order_type, timestamp_utc, stream, instrument
    - order_id (broker_order_id), trading_date, filled_total, remaining_qty
    - synthetic (True if converted from EXECUTION_EXIT_FILL for backfill)
    """
    data = raw_event.get("data", {})
    if not isinstance(data, dict):
        data = {}
    
    canonical = {}
    canonical["intent_id"] = data.get("intent_id") or raw_event.get("intent_id") or ""
    
    # Fill data
    fill_price = data.get("fill_price") or data.get("FillPrice")
    fill_qty = data.get("fill_quantity") or data.get("fill_qty") or data.get("FillQuantity")
    
    canonical["fill_price"] = float(fill_price) if fill_price is not None else None
    canonical["fill_qty"] = int(fill_qty) if fill_qty is not None else None
    
    # Order type/role (STOP, TARGET, ENTRY, FLATTEN, etc.)
    # EXECUTION_EXIT_FILL backfill: exit_order_type -> order_type
    # Fallback: infer from position_effect when order_type missing (OPEN -> ENTRY)
    # Normalize ENTRY_STOP, ENTRY_LIMIT, etc. -> ENTRY for ledger consistency
    order_type = (
        data.get("order_type") or data.get("OrderType") or data.get("order_role") or
        data.get("exit_order_type") or ""
    )
    if not order_type and (data.get("position_effect") or "").upper() == "OPEN":
        order_type = "ENTRY"
    if order_type and str(order_type).upper().startswith("ENTRY"):
        order_type = "ENTRY"
    canonical["order_type"] = order_type
    
    # Order IDs (internal vs broker) - lenient for backfill
    canonical["order_id"] = data.get("order_id") or data.get("broker_order_id") or ""
    canonical["broker_order_id"] = data.get("broker_order_id") or data.get("order_id") or ""
    
    # Canonical fill: execution_sequence, fill_group_id, position_effect, mapped
    canonical["execution_sequence"] = data.get("execution_sequence")
    canonical["fill_group_id"] = data.get("fill_group_id") or ""
    canonical["position_effect"] = data.get("position_effect") or ""
    canonical["mapped"] = data.get("mapped", True)
    canonical["unmapped_reason"] = data.get("unmapped_reason") or ""
    
    # Timestamp
    canonical["timestamp_utc"] = raw_event.get("timestamp_utc") or raw_event.get("ts_utc") or data.get("timestamp_utc")
    
    # Stream and instrument (may be at top level or in data)
    canonical["stream"] = raw_event.get("stream") or data.get("stream") or data.get("stream_id") or ""
    canonical["instrument"] = raw_event.get("instrument") or data.get("instrument") or ""
    
    # UNIFY FILL EVENTS: Required for PnL determinism
    canonical["trading_date"] = raw_event.get("trading_date") or data.get("trading_date") or ""
    canonical["filled_total"] = data.get("filled_total")
    canonical["remaining_qty"] = data.get("remaining_qty")
    
    # P1: Mandatory fields for canonical fill
    canonical["execution_instrument_key"] = data.get("execution_instrument_key") or raw_event.get("execution_instrument_key") or ""
    canonical["side"] = data.get("side") or raw_event.get("side") or ""
    canonical["account"] = data.get("account") or raw_event.get("account") or ""
    canonical["stream_key"] = data.get("stream_key") or data.get("stream") or canonical.get("stream", "")
    canonical["session_class"] = data.get("session_class") or raw_event.get("session_class") or ""
    
    # Backfill marker
    canonical["synthetic"] = raw_event.get("synthetic", False)
    
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
