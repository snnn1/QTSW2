"""
Ledger Builder

Builds per-intent ledger rows from execution journals and events.
Handles edge cases: partial fills, forced flatten, missing exits, recovery days.
"""
import json
import logging
from pathlib import Path
from typing import Dict, List, Optional, Any, Tuple
from collections import defaultdict
from datetime import datetime

from .schema import (
    normalize_journal_entry,
    normalize_execution_filled,
    normalize_intent_exit_fill,
)
from ..config import EXECUTION_JOURNALS_DIR, FRONTEND_FEED_FILE

logger = logging.getLogger(__name__)


def get_canonical_instrument(instrument: str) -> str:
    """
    PHASE 2: Map execution instrument to canonical instrument.
    Maps micro futures (MES, MNQ, etc.) to their base instruments (ES, NQ, etc.).
    """
    try:
        from modules.analyzer.logic.instrument_logic import InstrumentManager
        mgr = InstrumentManager()
        if mgr.is_micro_future(instrument):
            return mgr.get_base_instrument(instrument)
        return instrument
    except Exception as e:
        logger.warning(f"Failed to canonicalize instrument '{instrument}': {e}, using as-is")
        return instrument


def canonicalize_stream(stream_id: str, execution_instrument: str) -> str:
    """
    PHASE 2: Map stream ID to canonical stream ID.
    Replaces execution instrument in stream ID with canonical instrument.
    e.g., "MES1" -> "ES1"
    """
    canonical_instrument = get_canonical_instrument(execution_instrument)
    if execution_instrument and execution_instrument.upper() in stream_id.upper():
        import re
        pattern = re.compile(re.escape(execution_instrument), re.IGNORECASE)
        return pattern.sub(canonical_instrument, stream_id)
    return stream_id


class LedgerBuilder:
    """Builds per-intent ledger rows for P&L calculation."""
    
    def build_ledger_rows(
        self,
        trading_date: str,
        stream: Optional[str] = None
    ) -> List[Dict[str, Any]]:
        """
        Build ledger rows for all intents in trading_date (optionally filtered by stream).
        
        Returns list of ledger rows with canonical fields:
        - trading_date, stream, instrument, intent_id, direction
        - entry_price, entry_qty, exit_qty, avg_exit_price
        - total_costs, status, pnl_confidence
        """
        # Step 1: Load journals
        journal_entries = self._load_journals(trading_date, stream)
        
        # Step 2: Load events
        exit_fills = self._load_exit_fills(trading_date, stream)
        execution_fills = self._load_execution_fills(trading_date, stream)
        
        # Step 3: Build ledger rows
        ledger_rows = []
        
        for intent_id, journal in journal_entries.items():
            row = self._build_ledger_row(
                journal, intent_id, exit_fills.get(intent_id, []), execution_fills.get(intent_id, [])
            )
            if row:
                ledger_rows.append(row)
        
        return ledger_rows
    
    def _load_journals(self, trading_date: str, stream: Optional[str] = None) -> Dict[str, Dict]:
        """Load ExecutionJournalEntry files and normalize."""
        journals = {}
        
        if not EXECUTION_JOURNALS_DIR.exists():
            logger.warning(f"Execution journals directory does not exist: {EXECUTION_JOURNALS_DIR}")
            return journals
        
        # Pattern: {trading_date}_{stream}_{intent_id}.json
        pattern = f"{trading_date}_*.json" if not stream else f"{trading_date}_{stream}_*.json"
        
        for journal_file in EXECUTION_JOURNALS_DIR.glob(pattern):
            try:
                with open(journal_file, 'r') as f:
                    raw = json.load(f)
                
                normalized = normalize_journal_entry(raw)
                intent_id = normalized.get("intent_id")
                
                if intent_id:
                    journals[intent_id] = normalized
            except Exception as e:
                logger.warning(f"Failed to load journal file {journal_file}: {e}")
        
        return journals
    
    def _load_exit_fills(self, trading_date: str, stream: Optional[str] = None) -> Dict[str, List[Dict]]:
        """
        Load INTENT_EXIT_FILL events from frontend_feed.jsonl.
        
        Returns dict: intent_id -> list of normalized exit fill events
        """
        exit_fills = defaultdict(list)
        
        if not FRONTEND_FEED_FILE.exists():
            logger.warning(f"Frontend feed file does not exist: {FRONTEND_FEED_FILE}")
            return exit_fills
        
        try:
            with open(FRONTEND_FEED_FILE, 'r') as f:
                for line in f:
                    if not line.strip():
                        continue
                    
                    try:
                        event = json.loads(line)
                        event_type = event.get("event_type") or event.get("event")
                        
                        if event_type != "INTENT_EXIT_FILL":
                            continue
                        
                        # Filter by trading_date
                        event_trading_date = event.get("trading_date")
                        if event_trading_date != trading_date:
                            continue
                        
                        # Filter by stream if specified
                        # PHASE 3: Canonicalize event stream for comparison
                        event_stream = event.get("stream")
                        event_execution_instrument = event.get("instrument") or event.get("execution_instrument")
                        if event_stream and event_execution_instrument:
                            event_canonical_stream = canonicalize_stream(event_stream, event_execution_instrument)
                        else:
                            event_canonical_stream = event_stream
                        
                        if stream and event_canonical_stream != stream:
                            continue
                        
                        normalized = normalize_intent_exit_fill(event)
                        intent_id = normalized.get("intent_id")
                        
                        if intent_id:
                            exit_fills[intent_id].append(normalized)
                    except json.JSONDecodeError:
                        continue
                    except Exception as e:
                        logger.debug(f"Error processing event line: {e}")
                        continue
        except Exception as e:
            logger.warning(f"Failed to read frontend feed file: {e}")
        
        return exit_fills
    
    def _load_execution_fills(self, trading_date: str, stream: Optional[str] = None) -> Dict[str, List[Dict]]:
        """
        Load EXECUTION_FILLED events from frontend_feed.jsonl.
        
        Returns dict: intent_id -> list of normalized execution fill events
        """
        execution_fills = defaultdict(list)
        
        if not FRONTEND_FEED_FILE.exists():
            logger.warning(f"Frontend feed file does not exist: {FRONTEND_FEED_FILE}")
            return execution_fills
        
        try:
            with open(FRONTEND_FEED_FILE, 'r') as f:
                for line in f:
                    if not line.strip():
                        continue
                    
                    try:
                        event = json.loads(line)
                        event_type = event.get("event_type") or event.get("event")
                        
                        if event_type not in ("EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL"):
                            continue
                        
                        # Filter by trading_date
                        event_trading_date = event.get("trading_date")
                        if event_trading_date != trading_date:
                            continue
                        
                        # Filter by stream if specified
                        # PHASE 3: Canonicalize event stream for comparison
                        event_stream = event.get("stream")
                        event_execution_instrument = event.get("instrument") or event.get("execution_instrument")
                        if event_stream and event_execution_instrument:
                            event_canonical_stream = canonicalize_stream(event_stream, event_execution_instrument)
                        else:
                            event_canonical_stream = event_stream
                        
                        if stream and event_canonical_stream != stream:
                            continue
                        
                        normalized = normalize_execution_filled(event)
                        intent_id = normalized.get("intent_id")
                        order_type = normalized.get("order_type", "").upper()
                        
                        # Only include exit fills (STOP, TARGET, FLATTEN)
                        # ENTRY fills are already in journal
                        if intent_id and order_type in ("STOP", "TARGET", "FLATTEN", "PROTECTIVE_STOP", "PROTECTIVE_TARGET"):
                            execution_fills[intent_id].append(normalized)
                    except json.JSONDecodeError:
                        continue
                    except Exception as e:
                        logger.debug(f"Error processing event line: {e}")
                        continue
        except Exception as e:
            logger.warning(f"Failed to read frontend feed file: {e}")
        
        return execution_fills
    
    def _build_ledger_row(
        self,
        journal: Dict[str, Any],
        intent_id: str,
        exit_fill_events: List[Dict],
        execution_fill_events: List[Dict]
    ) -> Optional[Dict[str, Any]]:
        """
        Build a single ledger row from journal entry and events.
        
        Handles:
        - Determining exit quantities from INTENT_EXIT_FILL events
        - Determining exit prices from EXECUTION_FILLED events (weighted average)
        - Edge cases: partial fills, missing exits, missing prices
        """
        # Start with journal data
        # PHASE 3: Journals contain canonical stream ID and execution instrument
        execution_instrument_from_journal = journal.get("instrument", "")  # Execution instrument from journal
        stream_from_journal = journal.get("stream", "")  # Canonical stream ID from journal
        
        # PHASE 3: Canonicalize stream ID if needed (journals should already be canonical, but defensive)
        canonical_stream_id = canonicalize_stream(stream_from_journal, execution_instrument_from_journal) if execution_instrument_from_journal else stream_from_journal
        canonical_instrument_from_journal = get_canonical_instrument(execution_instrument_from_journal) if execution_instrument_from_journal else execution_instrument_from_journal
        
        row = {
            "trading_date": journal.get("trading_date", ""),
            "stream": canonical_stream_id,  # PHASE 3: Use canonical stream ID for aggregation
            "instrument": execution_instrument_from_journal,  # Keep execution instrument for reference
            "execution_instrument": execution_instrument_from_journal,  # PHASE 3: Explicit execution identity
            "canonical_instrument": canonical_instrument_from_journal,  # PHASE 3: Explicit canonical identity
            "intent_id": intent_id,
            "direction": journal.get("direction"),
            "entry_price": journal.get("entry_price"),
            "entry_qty": journal.get("entry_qty"),
            "total_costs": journal.get("costs_dollars", 0),
            "stop_price": journal.get("stop_price"),
            "target_price": journal.get("target_price"),
        }
        
        # Validate required fields
        if not row["entry_price"] or not row["entry_qty"]:
            logger.debug(f"Skipping intent {intent_id}: missing entry data")
            return None
        
        # Step 1: Determine exit quantity
        exit_qty = self._determine_exit_qty(exit_fill_events, row["entry_qty"])
        row["exit_qty"] = exit_qty
        
        # Step 2: Determine exit price (weighted average)
        avg_exit_price, price_confidence = self._determine_exit_price(
            execution_fill_events, exit_qty, row.get("stop_price"), row.get("target_price")
        )
        row["avg_exit_price"] = avg_exit_price
        
        # Step 3: Determine status
        status = self._determine_status(exit_qty, row["entry_qty"], avg_exit_price)
        row["status"] = status
        
        # Step 4: Set confidence
        row["pnl_confidence"] = price_confidence
        
        return row
    
    def _determine_exit_qty(self, exit_fill_events: List[Dict], entry_qty: int) -> int:
        """
        Determine exit quantity from INTENT_EXIT_FILL events.
        
        Events provide cumulative exit_filled_qty.
        Use the maximum cumulative value.
        Clamp to entry_qty.
        """
        if not exit_fill_events:
            return 0
        
        # Find maximum cumulative exit filled quantity
        max_exit_qty = 0
        for event in exit_fill_events:
            exit_filled_qty_cum = event.get("exit_filled_qty_cum", 0)
            if exit_filled_qty_cum > max_exit_qty:
                max_exit_qty = exit_filled_qty_cum
        
        # Clamp to entry_qty (safety check)
        if max_exit_qty > entry_qty:
            logger.warning(
                f"Exit qty {max_exit_qty} exceeds entry qty {entry_qty}, clamping to {entry_qty}"
            )
            max_exit_qty = entry_qty
        
        return max_exit_qty
    
    def _determine_exit_price(
        self,
        execution_fill_events: List[Dict],
        exit_qty: int,
        stop_price: Optional[float],
        target_price: Optional[float]
    ) -> Tuple[Optional[float], str]:
        """
        Determine average exit price from EXECUTION_FILLED events.
        
        Returns:
            (avg_exit_price, confidence)
            - avg_exit_price: Weighted average if fills exist, None otherwise
            - confidence: "HIGH" if fills exist, "MEDIUM" if inferred from stop/target, "LOW" if missing
        """
        if not execution_fill_events:
            # No execution fills - try to infer from stop/target prices
            if exit_qty > 0:
                # If we have exits but no fill prices, infer from stop/target
                # Prefer target (profit) over stop (loss) if both exist
                inferred_price = target_price or stop_price
                if inferred_price:
                    logger.debug(f"Inferring exit price from stop/target: {inferred_price}")
                    return inferred_price, "MEDIUM"
                else:
                    logger.warning(f"Exit qty {exit_qty} but no exit price available")
                    return None, "LOW"
            else:
                return None, "HIGH"  # No exits, no price needed
        
        # Calculate weighted average from execution fills
        total_price_qty = 0.0
        total_qty = 0
        
        for event in execution_fill_events:
            fill_price = event.get("fill_price")
            fill_qty = event.get("fill_qty", 0)
            
            if fill_price is not None and fill_qty > 0:
                total_price_qty += fill_price * fill_qty
                total_qty += fill_qty
        
        if total_qty > 0:
            avg_price = total_price_qty / total_qty
            return avg_price, "HIGH"
        else:
            # Fills exist but no valid price/qty
            if exit_qty > 0:
                inferred_price = target_price or stop_price
                if inferred_price:
                    return inferred_price, "MEDIUM"
                return None, "LOW"
            return None, "HIGH"
    
    def _determine_status(
        self,
        exit_qty: int,
        entry_qty: int,
        avg_exit_price: Optional[float]
    ) -> str:
        """
        Determine intent status: CLOSED, PARTIAL, or OPEN.
        """
        if exit_qty == 0 or avg_exit_price is None:
            return "OPEN"
        elif exit_qty >= entry_qty:
            return "CLOSED"
        else:
            return "PARTIAL"
