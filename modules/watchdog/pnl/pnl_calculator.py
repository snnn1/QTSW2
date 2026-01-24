"""
P&L Calculator

Stateless calculator for computing realized P&L from ledger rows.
Uses canonical instrument multiplier mapping (single source of truth).
"""
import logging
from typing import Dict, Optional, Any, List

logger = logging.getLogger(__name__)

# Single source of truth for contract multipliers
INSTRUMENT_MULTIPLIERS = {
    "ES": 50,
    "NQ": 20,
    "RTY": 50,
    "YM": 5,
    "GC": 100,
    "NG": 10000,
}


def get_instrument_from_stream(stream: str) -> Optional[str]:
    """
    Derive instrument from stream naming convention.
    
    Streams are typically named like "NQ1", "ES2", "RTY1", etc.
    Extract the instrument prefix (first 2-3 characters before the number).
    """
    if not stream:
        return None
    
    # Common patterns: NQ1, ES2, RTY1, YM1, GC2, NG1
    # Extract prefix (non-numeric characters at start)
    prefix = ""
    for char in stream:
        if char.isdigit():
            break
        prefix += char
    
    # Normalize common variations
    prefix = prefix.upper()
    if prefix in INSTRUMENT_MULTIPLIERS:
        return prefix
    
    # Try common aliases
    aliases = {
        "E": "ES",  # E-mini S&P 500
        "M": "ES",  # Alternative
    }
    if prefix in aliases:
        return aliases[prefix]
    
    return None


def compute_intent_realized_pnl(ledger_row: Dict[str, Any]) -> Dict[str, Any]:
    """
    Calculate realized P&L for a single intent ledger row.
    
    Only calculates realized P&L if exit_qty > 0 and avg_exit_price exists.
    
    Cost allocation rule:
    - If PARTIAL: allocate costs proportionally: costs_allocated = total_costs * (exit_qty / entry_qty)
    - If CLOSED: allocate full costs
    - If OPEN: costs not "realized" yet (keep costs in row but do not subtract into realized totals)
    
    Args:
        ledger_row: Canonical ledger row with fields:
            - entry_price, entry_qty, exit_qty, avg_exit_price
            - total_costs, direction, instrument (or stream for derivation)
            - status (OPEN/PARTIAL/CLOSED)
    
    Returns:
        Updated ledger_row with calculated fields:
        - gross_pnl, realized_pnl, costs_allocated
        - status (OPEN/PARTIAL/CLOSED)
        - pnl_confidence (HIGH/MEDIUM/LOW)
    """
    entry_price = ledger_row.get("entry_price")
    entry_qty = ledger_row.get("entry_qty")
    exit_qty = ledger_row.get("exit_qty", 0)
    avg_exit_price = ledger_row.get("avg_exit_price")
    total_costs = ledger_row.get("total_costs", 0)
    direction = ledger_row.get("direction")
    
    # Get instrument multiplier
    instrument = ledger_row.get("instrument")
    if not instrument:
        # Try to derive from stream
        stream = ledger_row.get("stream", "")
        instrument = get_instrument_from_stream(stream)
    
    if not instrument:
        logger.warning(f"Cannot determine instrument for stream {ledger_row.get('stream')}, using multiplier 1")
        multiplier = 1
    else:
        multiplier = INSTRUMENT_MULTIPLIERS.get(instrument, 1)
        if multiplier == 1 and instrument:
            logger.warning(f"Unknown instrument {instrument}, using multiplier 1")
    
    # Determine status if not already set
    if not ledger_row.get("status"):
        if exit_qty == 0 or avg_exit_price is None:
            status = "OPEN"
        elif exit_qty >= entry_qty:
            status = "CLOSED"
        else:
            status = "PARTIAL"
        ledger_row["status"] = status
    
    status = ledger_row["status"]
    
    # Only calculate realized P&L if exits exist
    if status == "OPEN" or avg_exit_price is None or exit_qty == 0:
        ledger_row["gross_pnl"] = None
        ledger_row["realized_pnl"] = None
        ledger_row["costs_allocated"] = 0.0
        # Keep pnl_confidence from ledger builder if set, otherwise LOW for OPEN
        if "pnl_confidence" not in ledger_row:
            ledger_row["pnl_confidence"] = "LOW"
        return ledger_row
    
    # Calculate gross P&L
    price_diff = avg_exit_price - entry_price
    if direction == "Short":
        price_diff = -price_diff
    
    # Use exit_qty (may be partial)
    gross_pnl = price_diff * exit_qty * multiplier
    ledger_row["gross_pnl"] = gross_pnl
    
    # Allocate costs
    if status == "CLOSED":
        costs_allocated = total_costs
    elif status == "PARTIAL":
        # Proportional allocation
        if entry_qty and entry_qty > 0:
            costs_allocated = total_costs * (exit_qty / entry_qty)
        else:
            costs_allocated = 0.0
    else:
        costs_allocated = 0.0
    
    ledger_row["costs_allocated"] = costs_allocated
    
    # Calculate realized P&L
    realized_pnl = gross_pnl - costs_allocated
    ledger_row["realized_pnl"] = realized_pnl
    
    # Set pnl_confidence if not already set (ledger builder may have set it)
    if "pnl_confidence" not in ledger_row:
        ledger_row["pnl_confidence"] = "HIGH"
    
    return ledger_row


def aggregate_stream_pnl(ledger_rows: List[Dict[str, Any]], stream: str) -> Dict[str, Any]:
    """
    Aggregate P&L for all intents in a stream.
    
    Returns:
    {
        "stream": str,
        "realized_pnl": float,  # Sum of closed/partial intents only
        "open_positions": int,
        "total_costs_realized": float,  # Only allocated costs for CLOSED/PARTIAL
        "intent_count": int,
        "closed_count": int,
        "partial_count": int,
        "open_count": int,
        "pnl_confidence": str  # "HIGH" if all intents HIGH, MEDIUM if any MEDIUM, LOW otherwise
    }
    """
    closed_pnl = sum(
        r.get("realized_pnl", 0)
        for r in ledger_rows
        if r.get("status") in ("CLOSED", "PARTIAL") and r.get("realized_pnl") is not None
    )
    
    open_count = sum(1 for r in ledger_rows if r.get("status") == "OPEN")
    closed_count = sum(1 for r in ledger_rows if r.get("status") == "CLOSED")
    partial_count = sum(1 for r in ledger_rows if r.get("status") == "PARTIAL")
    
    # Total costs realized: only allocated costs for CLOSED/PARTIAL
    total_costs_realized = sum(
        r.get("costs_allocated", 0)
        for r in ledger_rows
        if r.get("status") in ("CLOSED", "PARTIAL")
    )
    
    # Determine confidence: HIGH if all intents HIGH, MEDIUM if any MEDIUM, LOW otherwise
    confidences = [r.get("pnl_confidence", "LOW") for r in ledger_rows]
    if all(c == "HIGH" for c in confidences):
        pnl_confidence = "HIGH"
    elif any(c == "MEDIUM" for c in confidences):
        pnl_confidence = "MEDIUM"
    else:
        pnl_confidence = "LOW"
    
    return {
        "stream": stream,
        "realized_pnl": closed_pnl,
        "open_positions": open_count,
        "total_costs_realized": total_costs_realized,
        "intent_count": len(ledger_rows),
        "closed_count": closed_count,
        "partial_count": partial_count,
        "open_count": open_count,
        "pnl_confidence": pnl_confidence
    }
