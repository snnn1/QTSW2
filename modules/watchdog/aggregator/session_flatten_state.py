"""
Session + forced-flatten visibility state + watchdog-only alerts.

INVARIANTS:
- Derived only from robot engine events in the ingest feed (no NT logs, no local session math).
- Rollup key: (trading_date, session_class, instrument) — multiple streams may collapse to one row.
- No NO_POSITION / no inference of "flat" from absent events.
- FORCED_FLATTEN_FAILED ingested only when session_close_forced_flatten is true (session-close path).
- SESSION_CLOSE_RESOLVED treated as legacy alias for session resolution (harness / older JSONL).
- FORCED_FLATTEN_EXPOSURE_REMAINING / MANUAL_FLATTEN_REQUIRED after broker confirm → EXPOSURE_REMAINS (session-close tagged only).
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Dict, List, Optional, Tuple

import pytz

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")

_TERMINAL_STATUSES = frozenset({"CONFIRMED", "FAILED", "TIMEOUT", "EXPOSURE_REMAINS"})

# Event types that update session/flatten rollup state (replay may skip pre-filter when ingesting frontend_feed).
SESSION_FLATTEN_INGEST_EVENT_TYPES = frozenset(
    {
        "SESSION_CLOSE_SOURCE_SELECTED",
        "SESSION_RESOLVED",
        "SESSION_CLOSE_RESOLVED",
        "FLATTEN_TRIGGER_SET",
        "FORCED_FLATTEN_TRIGGERED",
        "FORCED_FLATTEN_REQUEST_SUBMITTED",
        "FORCED_FLATTEN_FAILED",
        "FLATTEN_BROKER_FLAT_CONFIRMED",
        "FORCED_FLATTEN_BROKER_TIMEOUT",
        "FORCED_FLATTEN_EXPOSURE_REMAINING",
        "MANUAL_FLATTEN_REQUIRED",
    }
)


def _as_dict(val: Any) -> Dict[str, Any]:
    return val if isinstance(val, dict) else {}


def _parse_cs_style_payload_blob(payload: Any) -> Dict[str, Any]:
    """Extract key fields from Robot/C# ToString-style blobs in data.payload (engine JSONL)."""
    if not isinstance(payload, str) or "=" not in payload:
        return {}
    out: Dict[str, Any] = {}
    patterns: Tuple[Tuple[str, str], ...] = (
        ("reason", r"\breason\s*=\s*(\w+)"),
        ("session_class", r"\bsession_class\s*=\s*(\w+)"),
        ("source", r"\bsource\s*=\s*([\w_]+)"),
        ("trading_date", r"\btrading_date\s*=\s*(\d{4}-\d{2}-\d{2})"),
        ("trading_day", r"\btrading_day\s*=\s*(\d{4}-\d{2}-\d{2})"),
        (
            "resolved_session_close_utc",
            r"\bresolved_session_close_utc\s*=\s*([^,}]+)",
        ),
        ("flatten_trigger_utc", r"\bflatten_trigger_utc\s*=\s*([^,}]+)"),
        ("buffer_seconds", r"\bbuffer_seconds\s*=\s*(\d+)"),
        ("used_fallback", r"\bused_fallback\s*=\s*(True|False)"),
        (
            "session_close_forced_flatten",
            r"\bsession_close_forced_flatten\s*=\s*(True|False)",
        ),
    )
    for key, pat in patterns:
        m = re.search(pat, payload)
        if not m:
            continue
        raw = m.group(1).strip()
        if key == "buffer_seconds":
            try:
                out[key] = int(raw)
            except ValueError:
                pass
        elif key in ("used_fallback", "session_close_forced_flatten"):
            out[key] = raw == "True"
        else:
            out[key] = raw
    return out


def _merged_payload(event: Dict[str, Any]) -> Dict[str, Any]:
    data = _as_dict(event.get("data"))
    out = {k: v for k, v in data.items() if v is not None}
    for k in (
        "trading_date",
        "session_class",
        "session",
        "instrument",
        "source",
        "has_session",
        "session_close_utc",
        "session_close_chicago",
        "flatten_trigger_utc",
        "flatten_trigger_chicago",
        "buffer_seconds",
        "reason",
        "session_close_forced_flatten",
    ):
        if (k not in out or out.get(k) in ("", None)) and event.get(k) not in (None, ""):
            out[k] = event[k]
    blob_keys = (
        "reason",
        "session_class",
        "source",
        "trading_date",
        "trading_day",
        "resolved_session_close_utc",
        "flatten_trigger_utc",
        "buffer_seconds",
        "used_fallback",
        "session_close_forced_flatten",
    )
    for k, v in _parse_cs_style_payload_blob(data.get("payload")).items():
        if k in blob_keys and (k not in out or out.get(k) in ("", None)):
            out[k] = v
    td_alt = out.get("trading_day")
    if td_alt and (not out.get("trading_date") or str(out.get("trading_date")).strip() in ("",)):
        out["trading_date"] = td_alt
    if "session_class" not in out and out.get("session"):
        out["session_class"] = out["session"]
    return out


def _fmt_hhmm_chicago(iso_str: str) -> str:
    if not iso_str or not str(iso_str).strip():
        return ""
    try:
        s = str(iso_str).strip().replace("Z", "+00:00")
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = CHICAGO_TZ.localize(dt)
        else:
            dt = dt.astimezone(CHICAGO_TZ)
        return dt.strftime("%H:%M")
    except Exception:
        return ""


def _parse_utc(iso_str: str) -> Optional[datetime]:
    if not iso_str or not str(iso_str).strip():
        return None
    try:
        s = str(iso_str).strip().replace("Z", "+00:00")
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        else:
            dt = dt.astimezone(timezone.utc)
        return dt
    except Exception:
        return None


def _dedupe_critical(trading_date: str, session_class: str, instrument_key: str) -> str:
    return f"SESSION_FLATTEN_NOT_CONFIRMED_CRITICAL:{trading_date}:{session_class}:{instrument_key}"


def _dedupe_at_risk(trading_date: str, session_class: str, instrument_key: str) -> str:
    return f"SESSION_FLATTEN_AT_RISK_WARNING:{trading_date}:{session_class}:{instrument_key}"


@dataclass
class SessionFlattenRow:
    trading_date: str = ""
    session_class: str = ""
    instrument: str = ""
    has_session: Optional[bool] = None
    session_close_utc: str = ""
    session_close_chicago_iso: str = ""
    flatten_trigger_utc: str = ""
    flatten_trigger_chicago_iso: str = ""
    source: str = ""
    buffer_seconds: Optional[int] = None
    flatten_status: str = "NOT_TRIGGERED"
    flatten_required: bool = False
    alert_emitted: bool = False
    at_risk_alert_emitted: bool = False

    def instrument_key(self) -> str:
        return self.instrument.strip() or "__engine__"

    def to_api_dict(self) -> Dict[str, Any]:
        sc_ct = _fmt_hhmm_chicago(self.session_close_chicago_iso) or _fmt_hhmm_chicago(
            self.session_close_utc
        )
        ft_ct = _fmt_hhmm_chicago(self.flatten_trigger_chicago_iso) or _fmt_hhmm_chicago(
            self.flatten_trigger_utc
        )
        return {
            "trading_date": self.trading_date,
            "session_class": self.session_class,
            "instrument": self.instrument,
            "has_session": self.has_session,
            "session_close_chicago": sc_ct,
            "flatten_trigger_chicago": ft_ct,
            "source": self.source,
            "flatten_status": self.flatten_status,
        }


class SessionFlattenStateTracker:
    """Incremental state from engine session/flatten visibility events."""

    def __init__(self) -> None:
        self._rows: Dict[Tuple[str, str, str], SessionFlattenRow] = {}
        self._pending_resolves: List[str] = []

    def _key(self, event: Dict[str, Any]) -> Optional[Tuple[str, str, str]]:
        p = _merged_payload(event)
        td = str(p.get("trading_date") or "").strip()
        sc = str(p.get("session_class") or p.get("session") or "").strip()
        inst = str(p.get("instrument") or "").strip()
        if not td or not sc:
            return None
        return (td, sc, inst or "__engine__")

    def _get_or_create(self, key: Tuple[str, str, str]) -> SessionFlattenRow:
        if key not in self._rows:
            self._rows[key] = SessionFlattenRow(
                trading_date=key[0],
                session_class=key[1],
                instrument=key[2] if key[2] != "__engine__" else "",
            )
        return self._rows[key]

    @staticmethod
    def _set_triggered_if_allowed(row: SessionFlattenRow) -> None:
        row.flatten_required = True
        if row.flatten_status not in _TERMINAL_STATUSES:
            row.flatten_status = "TRIGGERED"

    def ingest(self, event: Dict[str, Any]) -> None:
        et = str(event.get("event_type") or "").strip()
        if et not in SESSION_FLATTEN_INGEST_EVENT_TYPES:
            return

        key = self._key(event)
        if key is None:
            logger.debug("session_flatten_state: missing trading_date/session for %s", et)
            return
        row = self._get_or_create(key)
        p = _merged_payload(event)

        if et == "SESSION_CLOSE_SOURCE_SELECTED":
            src = str(p.get("source") or "").strip()
            if src:
                row.source = src
            hs = p.get("has_session")
            if isinstance(hs, bool):
                row.has_session = hs
        elif et in ("SESSION_RESOLVED", "SESSION_CLOSE_RESOLVED"):
            row.session_close_utc = str(p.get("session_close_utc") or "").strip() or str(
                p.get("resolved_session_close_utc") or ""
            ).strip()
            row.session_close_chicago_iso = str(p.get("session_close_chicago") or "").strip()
            ft_early = str(p.get("flatten_trigger_utc") or "").strip()
            if ft_early:
                row.flatten_trigger_utc = ft_early
                ft_ch = str(p.get("flatten_trigger_chicago") or "").strip()
                if ft_ch:
                    row.flatten_trigger_chicago_iso = ft_ch
                bs = p.get("buffer_seconds")
                if bs is not None and row.buffer_seconds is None:
                    try:
                        row.buffer_seconds = int(bs)
                    except (TypeError, ValueError):
                        pass
            hs = p.get("has_session")
            if isinstance(hs, bool):
                row.has_session = hs
            src_rs = str(p.get("source") or "").strip()
            if src_rs and not row.source:
                row.source = src_rs
        elif et == "FLATTEN_TRIGGER_SET":
            row.flatten_trigger_utc = str(p.get("flatten_trigger_utc") or "").strip()
            row.flatten_trigger_chicago_iso = str(p.get("flatten_trigger_chicago") or "").strip()
            bs = p.get("buffer_seconds")
            if bs is not None:
                try:
                    row.buffer_seconds = int(bs)
                except (TypeError, ValueError):
                    pass
        elif et == "FORCED_FLATTEN_TRIGGERED":
            if str(p.get("reason") or "").strip().upper() != "SESSION_CLOSE":
                return
            self._set_triggered_if_allowed(row)
            rsc = str(p.get("resolved_session_close_utc") or "").strip()
            if rsc and not row.session_close_utc:
                row.session_close_utc = rsc
        elif et == "FORCED_FLATTEN_REQUEST_SUBMITTED":
            row.flatten_required = True
            if row.flatten_status not in _TERMINAL_STATUSES:
                row.flatten_status = "TRIGGERED"
        elif et == "FORCED_FLATTEN_FAILED":
            if p.get("session_close_forced_flatten") is not True:
                return
            row.flatten_required = True
            row.flatten_status = "FAILED"
        elif et == "FLATTEN_BROKER_FLAT_CONFIRMED":
            if p.get("session_close_forced_flatten") is True:
                row.flatten_status = "CONFIRMED"
                ik = row.instrument_key()
                self._pending_resolves.extend(
                    [
                        _dedupe_critical(row.trading_date, row.session_class, ik),
                        _dedupe_at_risk(row.trading_date, row.session_class, ik),
                    ]
                )
        elif et == "FORCED_FLATTEN_BROKER_TIMEOUT":
            if p.get("session_close_forced_flatten") is True:
                row.flatten_required = True
                row.flatten_status = "TIMEOUT"
        elif et in ("FORCED_FLATTEN_EXPOSURE_REMAINING", "MANUAL_FLATTEN_REQUIRED"):
            if p.get("session_close_forced_flatten") is not True:
                return
            row.flatten_required = True
            row.flatten_status = "EXPOSURE_REMAINS"

    def tick_alerts(
        self,
        now_utc: datetime,
        *,
        resolve_alert: Optional[Callable[[str], None]] = None,
        append_ledger_alert: Optional[Callable[[str, str, Dict[str, Any], str], bool]] = None,
        enqueue_delivery: Optional[Callable[[str, str, Dict[str, Any], str], None]] = None,
        is_alert_active: Optional[Callable[[str], bool]] = None,
    ) -> None:
        if resolve_alert:
            for dk in self._pending_resolves:
                try:
                    resolve_alert(dk)
                except Exception as e:
                    logger.debug("session_flatten resolve failed: %s", e)
            self._pending_resolves.clear()

        if not append_ledger_alert:
            return

        for row in self._rows.values():
            if row.has_session is not True:
                continue
            close_dt = _parse_utc(row.session_close_utc)
            if close_dt is None:
                continue
            ik = row.instrument_key()

            if (
                row.flatten_required
                and row.flatten_status != "CONFIRMED"
                and not row.at_risk_alert_emitted
                and now_utc >= close_dt - timedelta(seconds=60)
            ):
                dedupe = _dedupe_at_risk(row.trading_date, row.session_class, ik)
                if is_alert_active and is_alert_active(dedupe):
                    row.at_risk_alert_emitted = True
                else:
                    ctx = {
                        "trading_date": row.trading_date,
                        "session_class": row.session_class,
                        "instrument": row.instrument,
                        "flatten_status": row.flatten_status,
                        "session_close_utc": row.session_close_utc,
                    }
                    if append_ledger_alert(
                        "SESSION_FLATTEN_AT_RISK_WARNING",
                        "warning",
                        ctx,
                        dedupe,
                    ):
                        row.at_risk_alert_emitted = True
                        if enqueue_delivery:
                            enqueue_delivery(
                                "SESSION_FLATTEN_AT_RISK_WARNING",
                                "warning",
                                ctx,
                                dedupe,
                            )

            if (
                row.flatten_required
                and row.flatten_status != "CONFIRMED"
                and now_utc >= close_dt
                and not row.alert_emitted
            ):
                dedupe = _dedupe_critical(row.trading_date, row.session_class, ik)
                if is_alert_active and is_alert_active(dedupe):
                    row.alert_emitted = True
                else:
                    ctx = {
                        "trading_date": row.trading_date,
                        "session_class": row.session_class,
                        "instrument": row.instrument,
                        "flatten_status": row.flatten_status,
                        "session_close_utc": row.session_close_utc,
                    }
                    if append_ledger_alert(
                        "SESSION_FLATTEN_NOT_CONFIRMED_CRITICAL",
                        "critical",
                        ctx,
                        dedupe,
                    ):
                        row.alert_emitted = True
                        if enqueue_delivery:
                            enqueue_delivery(
                                "SESSION_FLATTEN_NOT_CONFIRMED_CRITICAL",
                                "critical",
                                ctx,
                                dedupe,
                            )

    def list_rows_sorted(self, now_utc: Optional[datetime] = None) -> List[Dict[str, Any]]:
        _ = now_utc  # API compatibility; no inferred status
        rows = list(self._rows.values())
        rows.sort(key=lambda r: (-_date_key(r.trading_date), r.session_class))
        return [r.to_api_dict() for r in rows]


def _date_key(td: str) -> int:
    try:
        parts = td.split("-")
        if len(parts) == 3:
            return int(parts[0]) * 10000 + int(parts[1]) * 100 + int(parts[2])
    except Exception:
        pass
    return 0
