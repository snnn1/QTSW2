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
from ..config import EXECUTION_JOURNALS_DIR, ROBOT_LOGS_DIR

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


class LedgerInvariantViolation(Exception):
    """Raised when ledger invariants are violated. Payload describes the violation."""
    def __init__(self, message: str, payload: Dict[str, Any]):
        super().__init__(message)
        self.payload = payload


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
        
        # Step 2: Load EXECUTION_FILLED events (canonical source for PnL - no INTENT_EXIT_FILL dependency)
        execution_fills = self._load_execution_fills(trading_date, stream)
        
        # Step 2b: Validate invariants (fail loudly on violation)
        self._validate_fill_invariants(execution_fills, journal_entries)
        
        # Step 3: Build ledger rows
        ledger_rows = []
        
        for intent_id, journal in journal_entries.items():
            row = self._build_ledger_row(
                journal, intent_id, execution_fills.get(intent_id, [])
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
            # Use utf-8-sig to handle UTF-8 BOM markers
            with open(FRONTEND_FEED_FILE, 'r', encoding='utf-8-sig') as f:
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
                        # Silently skip malformed JSON lines
                        continue
                    except Exception as e:
                        logger.debug(f"Error processing event line: {e}")
                        continue
        except Exception as e:
            logger.warning(f"Failed to read frontend feed file: {e}")
        
        return exit_fills
    
    ENTRY_ORDER_TYPE = "ENTRY"
    EXIT_ORDER_TYPES = frozenset(("STOP", "TARGET", "FLATTEN", "PROTECTIVE_STOP", "PROTECTIVE_TARGET"))
    OPPOSITE_SIDE = {"BUY": "SELL", "SELL": "BUY"}
    DIRECTION_TO_ENTRY_SIDE = {"LONG": "BUY", "SHORT": "SELL"}

    def _validate_execution_sequence_strictly_increasing(
        self, execution_fills: Dict[str, List[Dict]]
    ) -> None:
        """
        Phase 2.3: execution_sequence must be strictly increasing per execution_instrument_key.
        Skips fills without execution_sequence (synthetic/legacy backfill).
        """
        by_key: Dict[str, List[Tuple[int, str, int, str]]] = defaultdict(list)
        for intent_id, fills in execution_fills.items():
            for i, evt in enumerate(fills):
                seq = evt.get("execution_sequence")
                if seq is None:
                    continue
                key = evt.get("execution_instrument_key") or ""
                if not key:
                    continue
                by_key[key].append((seq, intent_id, i, evt.get("order_id") or ""))
        
        for key, items in by_key.items():
            items.sort(key=lambda x: x[0])
            for j in range(1, len(items)):
                if items[j][0] <= items[j - 1][0]:
                    payload = {
                        "event": "LEDGER_INVARIANT_VIOLATION",
                        "invariant": "execution_sequence_strictly_increasing",
                        "execution_instrument_key": key,
                        "prev_seq": items[j - 1][0],
                        "curr_seq": items[j][0],
                        "intent_id": items[j][1],
                        "fill_index": items[j][2],
                    }
                    logger.error("LEDGER_INVARIANT_VIOLATION: %s", payload)
                    raise LedgerInvariantViolation(
                        f"execution_sequence must be strictly increasing per execution_instrument_key "
                        f"(key={key}, prev={items[j-1][0]}, curr={items[j][0]})",
                        payload,
                    )

    def _validate_exit_side_opposite_entry(
        self,
        execution_fills: Dict[str, List[Dict]],
        journal_entries: Dict[str, Dict],
    ) -> None:
        """
        Phase 2.3: Entry side defines direction; exit side must be opposite.
        LONG -> entry BUY, exit SELL. SHORT -> entry SELL, exit BUY.
        """
        for intent_id, fills in execution_fills.items():
            entry_fills = [e for e in fills if (e.get("order_type") or "").upper().startswith(self.ENTRY_ORDER_TYPE)]
            exit_fills = [e for e in fills if (e.get("order_type") or "").upper() in self.EXIT_ORDER_TYPES]
            if not exit_fills:
                continue
            
            # Expected entry side: from entry fills, or from journal direction
            entry_side = None
            if entry_fills:
                entry_side = (entry_fills[0].get("side") or "").upper()
            if not entry_side:
                journal = journal_entries.get(intent_id, {})
                direction = (journal.get("direction") or "").upper()
                entry_side = self.DIRECTION_TO_ENTRY_SIDE.get(direction)
            
            if not entry_side or entry_side not in self.OPPOSITE_SIDE:
                continue  # Cannot validate without entry side
            
            expected_exit_side = self.OPPOSITE_SIDE[entry_side]
            for i, evt in enumerate(exit_fills):
                exit_side = (evt.get("side") or "").upper()
                if not exit_side:
                    continue  # Lenient: skip if side missing (legacy)
                if exit_side != expected_exit_side:
                    payload = {
                        "event": "LEDGER_INVARIANT_VIOLATION",
                        "invariant": "exit_side_opposite_entry",
                        "intent_id": intent_id,
                        "entry_side": entry_side,
                        "expected_exit_side": expected_exit_side,
                        "actual_exit_side": exit_side,
                        "fill_index": i,
                        "order_id": evt.get("order_id") or "",
                    }
                    logger.error("LEDGER_INVARIANT_VIOLATION: %s", payload)
                    raise LedgerInvariantViolation(
                        f"Exit side must be opposite entry (entry={entry_side}, expected_exit={expected_exit_side}, "
                        f"actual={exit_side})",
                        payload,
                    )

    def _validate_fill_invariants(
        self,
        execution_fills: Dict[str, List[Dict]],
        journal_entries: Optional[Dict[str, Dict]] = None,
    ) -> None:
        """
        Validate EXECUTION_FILLED invariants. Raises LedgerInvariantViolation on failure.
        
        Invariants (P1 canonicalization):
        - Every EXECUTION_FILLED must have fill_price > 0 and fill_qty > 0
        - Every EXECUTION_FILLED must have trading_date non-null
        - order_type must be ENTRY or in exit set
        - execution_instrument_key and side must be non-null (when available; lenient for backfill)
        - No negative quantities
        - No missing instrument
        
        Phase 2.3 ledger invariants:
        - execution_sequence strictly increasing per execution_instrument_key
        - Exit side must be opposite of entry side (LONG->entry BUY/exit SELL, SHORT->entry SELL/exit BUY)
        - sum(exit_qty) <= sum(entry_qty) per intent (validated in _build_ledger_row via _determine_exit_qty)
        """
        journal_entries = journal_entries or {}
        
        # Phase 2.3: execution_sequence strictly increasing per execution_instrument_key
        self._validate_execution_sequence_strictly_increasing(execution_fills)
        
        # Phase 2.3: exit side opposite entry side per intent
        self._validate_exit_side_opposite_entry(execution_fills, journal_entries)
        
        for intent_id, fills in execution_fills.items():
            for i, evt in enumerate(fills):
                violations = []
                fill_price = evt.get("fill_price")
                fill_qty = evt.get("fill_qty", 0) or 0
                trading_date = evt.get("trading_date")
                order_type = (evt.get("order_type") or "").upper()
                instrument = evt.get("instrument") or ""
                order_id = evt.get("order_id") or ""
                exec_inst_key = evt.get("execution_instrument_key") or ""
                side = evt.get("side") or ""
                
                if fill_price is None:
                    violations.append("fill_price missing")
                elif not (isinstance(fill_price, (int, float)) and float(fill_price) > 0):
                    violations.append(f"fill_price must be > 0 (got {fill_price})")
                if fill_qty <= 0:
                    violations.append(f"fill_qty must be > 0 (got {fill_qty})")
                if not trading_date or (isinstance(trading_date, str) and not trading_date.strip()):
                    violations.append("trading_date null or empty")
                valid_order_types = (self.ENTRY_ORDER_TYPE,) + tuple(self.EXIT_ORDER_TYPES)
                is_valid = order_type in valid_order_types or (order_type and order_type.startswith(self.ENTRY_ORDER_TYPE))
                if order_type and not is_valid:
                    violations.append(f"order_type must be ENTRY or in {self.EXIT_ORDER_TYPES} (got {order_type})")
                if fill_qty < 0:
                    violations.append("fill_qty negative")
                if not instrument or (isinstance(instrument, str) and not instrument.strip()):
                    violations.append("instrument missing")
                # P1: execution_instrument_key and side - strict for new events, lenient for synthetic backfill
                if not evt.get("synthetic") and order_type in self.EXIT_ORDER_TYPES:
                    if not exec_inst_key or (isinstance(exec_inst_key, str) and not exec_inst_key.strip()):
                        violations.append("execution_instrument_key null or empty")
                    if not side or (isinstance(side, str) and not side.strip()):
                        violations.append("side null or empty")
                
                if violations:
                    payload = {
                        "event": "LEDGER_INVARIANT_VIOLATION",
                        "intent_id": intent_id,
                        "order_id": order_id,
                        "violations": violations,
                        "fill_index": i,
                        "synthetic": evt.get("synthetic", False),
                    }
                    logger.error("LEDGER_INVARIANT_VIOLATION: %s", payload)
                    raise LedgerInvariantViolation(
                        f"EXECUTION_FILLED invariant violation: {', '.join(violations)}",
                        payload
                    )

    def _read_fills_from_log_file(
        self,
        log_file: Path,
        trading_date: str,
        stream: Optional[str],
        execution_fills: Dict[str, List[Dict]],
        exit_fill_by_order_id: Dict[str, Dict],
    ) -> None:
        """Read EXECUTION_FILLED/PARTIAL/EXIT_FILL from a single robot log file."""
        try:
            with open(log_file, 'r', encoding='utf-8-sig') as f:
                for line in f:
                    if not line.strip():
                        continue
                    try:
                        event = json.loads(line)
                        event_type = event.get("event_type") or event.get("event")
                        if event_type not in ("EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL", "EXECUTION_EXIT_FILL"):
                            continue
                        event_trading_date = event.get("trading_date") or (event.get("data") or {}).get("trading_date")
                        if event_trading_date != trading_date:
                            continue
                        event_stream = event.get("stream")
                        event_execution_instrument = event.get("instrument") or event.get("execution_instrument")
                        if event_stream and event_execution_instrument:
                            event_canonical_stream = canonicalize_stream(event_stream, event_execution_instrument)
                        else:
                            event_canonical_stream = event_stream
                        if stream and event_canonical_stream != stream:
                            continue
                        if event_type == "EXECUTION_EXIT_FILL":
                            normalized = normalize_execution_filled(event)
                            order_id = normalized.get("order_id") or normalized.get("broker_order_id") or ""
                            if order_id:
                                exit_fill_by_order_id[order_id] = normalized
                            continue
                        normalized = normalize_execution_filled(event)
                        intent_id = normalized.get("intent_id") or ""
                        order_type = (normalized.get("order_type") or "").upper()
                        if not intent_id or normalized.get("mapped") is False:
                            continue
                        if order_type == self.ENTRY_ORDER_TYPE or order_type in self.EXIT_ORDER_TYPES:
                            execution_fills[intent_id].append(normalized)
                    except json.JSONDecodeError:
                        continue
                    except Exception as e:
                        logger.debug(f"Error processing line in {log_file}: {e}")
                        continue
        except Exception as e:
            logger.warning(f"Failed to read {log_file}: {e}")

    def _load_execution_fills(self, trading_date: str, stream: Optional[str] = None) -> Dict[str, List[Dict]]:
        """
        Load EXECUTION_FILLED events from raw robot logs (Phase 3.1).
        
        Source: robot_<instrument>.jsonl in ROBOT_LOGS_DIR (canonical; feed is UI-only).
        UNIFY FILL EVENTS: Canonical source for both entry and exit fills.
        - Includes EXECUTION_FILLED and EXECUTION_PARTIAL_FILL
        - Backfill: Converts EXECUTION_EXIT_FILL to synthetic EXECUTION_FILLED when no EXECUTION_FILLED exists for that order
        
        Returns dict: intent_id -> list of normalized execution fill events (entry + exit fills for ledger).
        Unmapped fills (mapped=false, intent_id empty) are excluded from per-intent dict.
        """
        execution_fills = defaultdict(list)
        exit_fill_by_order_id: Dict[str, Dict] = {}  # For backfill: order_id -> normalized EXECUTION_EXIT_FILL
        
        if not ROBOT_LOGS_DIR.exists():
            logger.warning(f"Robot logs directory does not exist: {ROBOT_LOGS_DIR}")
            return execution_fills
        
        log_files = sorted(ROBOT_LOGS_DIR.glob("robot_*.jsonl"))
        if not log_files:
            logger.warning(f"No robot_*.jsonl files found in {ROBOT_LOGS_DIR}")
            return execution_fills
        
        try:
            for log_file in log_files:
                self._read_fills_from_log_file(
                    log_file, trading_date, stream,
                    execution_fills, exit_fill_by_order_id,
                )
            
            # Backfill: Convert EXECUTION_EXIT_FILL to synthetic EXECUTION_FILLED when no EXECUTION_FILLED for that order
            filled_order_ids = set()
            for intent_id, fills in execution_fills.items():
                for f in fills:
                    oid = f.get("order_id") or f.get("broker_order_id")
                    if oid:
                        filled_order_ids.add(oid)
            
            for order_id, exit_fill in exit_fill_by_order_id.items():
                if order_id not in filled_order_ids:
                    exit_fill["synthetic"] = True
                    intent_id = exit_fill.get("intent_id") or ""
                    if intent_id:
                        execution_fills[intent_id].append(exit_fill)
                        if logger.isEnabledFor(logging.DEBUG):
                            logger.debug(f"Backfill: Converted EXECUTION_EXIT_FILL to synthetic EXECUTION_FILLED for order_id={order_id}, intent_id={intent_id}")
                        
        except Exception as e:
            logger.warning(f"Failed to read robot logs: {e}")
        
        return execution_fills
    
    def _build_ledger_row(
        self,
        journal: Dict[str, Any],
        intent_id: str,
        execution_fill_events: List[Dict]
    ) -> Optional[Dict[str, Any]]:
        """
        Build a single ledger row from journal entry and EXECUTION_FILLED events.
        
        Fallback rule (Phase 2): EXECUTION_FILLED for entry when present, else journal, else incomplete.
        
        Handles:
        - Determining entry from EXECUTION_FILLED (ENTRY) or journal
        - Determining exit quantities from EXECUTION_FILLED (sum fill_qty for exit order types)
        - Determining exit prices from EXECUTION_FILLED (weighted average)
        - Edge cases: partial fills, missing exits, missing prices
        """
        # Start with journal data
        # PHASE 3: Journals contain canonical stream ID and execution instrument
        execution_instrument_from_journal = journal.get("instrument", "")  # Execution instrument from journal
        stream_from_journal = journal.get("stream", "")  # Canonical stream ID from journal
        
        # PHASE 3: Canonicalize stream ID if needed (journals should already be canonical, but defensive)
        canonical_stream_id = canonicalize_stream(stream_from_journal, execution_instrument_from_journal) if execution_instrument_from_journal else stream_from_journal
        canonical_instrument_from_journal = get_canonical_instrument(execution_instrument_from_journal) if execution_instrument_from_journal else execution_instrument_from_journal
        
        # Phase 2 fallback: entry from EXECUTION_FILLED when present, else journal
        # Treat order_type starting with "ENTRY" (e.g. ENTRY_STOP) as entry
        entry_fills = [
            e for e in execution_fill_events
            if (e.get("order_type") or "").upper().startswith(self.ENTRY_ORDER_TYPE)
        ]
        if entry_fills:
            entry_qty_from_fills = sum(e.get("fill_qty", 0) or 0 for e in entry_fills)
            total_pq = sum((e.get("fill_price") or 0) * (e.get("fill_qty") or 0) for e in entry_fills)
            entry_price_from_fills = total_pq / entry_qty_from_fills if entry_qty_from_fills else None
            entry_price = entry_price_from_fills
            entry_qty = entry_qty_from_fills
            entry_source = "EXECUTION_FILLED"
        else:
            entry_price = journal.get("entry_price")
            entry_qty = journal.get("entry_qty")
            entry_source = "journal"
        
        row = {
            "trading_date": journal.get("trading_date", ""),
            "stream": canonical_stream_id,  # PHASE 3: Use canonical stream ID for aggregation
            "instrument": execution_instrument_from_journal,  # Keep execution instrument for reference
            "execution_instrument": execution_instrument_from_journal,  # PHASE 3: Explicit execution identity
            "canonical_instrument": canonical_instrument_from_journal,  # PHASE 3: Explicit canonical identity
            "intent_id": intent_id,
            "direction": journal.get("direction"),
            "entry_price": entry_price,
            "entry_qty": entry_qty,
            "entry_source": entry_source,  # Phase 2: traceability
            "total_costs": journal.get("costs_dollars", 0),
            "stop_price": journal.get("stop_price"),
            "target_price": journal.get("target_price"),
            # Journal authoritative fields (use when TradeCompleted)
            "contract_multiplier": journal.get("contract_multiplier"),
            "trade_completed": journal.get("trade_completed", False),
            "realized_pnl_gross": journal.get("realized_pnl_gross"),
            "realized_pnl_net": journal.get("realized_pnl_net"),
            "completion_reason": journal.get("completion_reason"),
            "exit_order_type": journal.get("exit_order_type"),
            "exit_avg_fill_price": journal.get("exit_avg_fill_price"),
            "exit_filled_at": journal.get("exit_filled_at"),
            "be_modified": journal.get("be_modified", False),
        }
        
        # Validate required fields; if neither EXECUTION_FILLED nor journal has entry, mark incomplete
        if not row["entry_price"] or not row["entry_qty"]:
            logger.warning(
                f"Intent {intent_id}: missing entry data (entry_source={entry_source}). "
                "Emit CRITICAL if configured; skipping ledger row."
            )
            return None
        
        # Step 1: Determine exit quantity from EXECUTION_FILLED only (no INTENT_EXIT_FILL)
        exit_qty = self._determine_exit_qty(execution_fill_events, row["entry_qty"], intent_id)
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
    
    def _determine_exit_qty(
        self,
        execution_fill_events: List[Dict],
        entry_qty: int,
        intent_id: str = "",
    ) -> int:
        """
        Determine exit quantity from EXECUTION_FILLED only.
        
        Sum fill_qty for exit order types (STOP, TARGET, FLATTEN, etc.).
        Phase 2.3: Raise LedgerInvariantViolation if sum(exit_qty) > sum(entry_qty).
        """
        if not execution_fill_events:
            return 0
        
        exit_qty_from_fills = sum(
            e.get("fill_qty", 0) or 0
            for e in execution_fill_events
            if (e.get("order_type") or "").upper() in self.EXIT_ORDER_TYPES
            and not (e.get("order_type") or "").upper().startswith(self.ENTRY_ORDER_TYPE)
        )
        if exit_qty_from_fills > entry_qty:
            payload = {
                "event": "LEDGER_INVARIANT_VIOLATION",
                "invariant": "exit_qty_le_entry_qty",
                "intent_id": intent_id,
                "exit_qty": exit_qty_from_fills,
                "entry_qty": entry_qty,
            }
            logger.error("LEDGER_INVARIANT_VIOLATION: %s", payload)
            raise LedgerInvariantViolation(
                f"sum(exit_qty) must be <= sum(entry_qty) (intent={intent_id}, exit={exit_qty_from_fills}, entry={entry_qty})",
                payload,
            )
        return exit_qty_from_fills
    
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
