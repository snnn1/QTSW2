"""
Timetable Poller

Polls timetable_current.json. Session label and streams come only from a **valid** execution file:
we never mix wall-clock CME session with streams read from a broken or unlabeled timetable.
"""
import json
import logging
from pathlib import Path
from typing import Tuple, Optional, Set, Dict, Any, List
from datetime import datetime, timezone
import pytz

from .config import QTSW2_ROOT
from .run_context import WatchdogRunContext, context_prefers_replay_timetable

from modules.timetable.cme_session import get_cme_trading_date, resolve_live_execution_session_trading_date
from modules.timetable.stream_id_derived import (
    instrument_from_stream_id,
    session_from_stream_id,
)

logger = logging.getLogger(__name__)

CHICAGO_TZ = pytz.timezone("America/Chicago")


def _compute_content_hash(timetable: dict) -> str:
    """
    Canonical content hash: same logical input as RobotCore TimetableContentHasher and
    modules.timetable.timetable_content_hash.compute_content_hash_from_document.
    """
    from modules.timetable.timetable_content_hash import compute_content_hash_from_document

    return compute_content_hash_from_document(timetable)


def compute_timetable_trading_date(chicago_now: datetime) -> str:
    """
    CME session trading date from wall clock (18:00 America/Chicago rule).
    Use only for **non-timetable** helpers (metrics, cleanup hints) — not as a substitute for
    a missing session_trading_date on the execution file.
    """
    if chicago_now.tzinfo is None:
        raise ValueError("chicago_now must be timezone-aware (America/Chicago)")
    utc = chicago_now.astimezone(timezone.utc)
    return get_cme_trading_date(utc)


def _timetable_doc_is_replay(timetable: Dict[str, Any]) -> bool:
    if timetable.get("replay") is True:
        return True
    meta = timetable.get("metadata")
    return isinstance(meta, dict) and meta.get("replay") is True


def _timetable_json_source(timetable: Dict[str, Any]) -> Optional[str]:
    """Publisher lineage from the active timetable file ``source`` (e.g. master_matrix, dashboard_ui)."""
    raw = timetable.get("source")
    if raw is None:
        return None
    s = str(raw).strip()
    return s or None


def _read_json_object(path: Path) -> Optional[Dict[str, Any]]:
    if not path.is_file():
        return None
    try:
        with path.open(encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, json.JSONDecodeError):
        return None
    return data if isinstance(data, dict) else None


def _date_from_run_summary(context: WatchdogRunContext) -> Optional[str]:
    summary = _read_json_object(context.persistence_base / "summary.json")
    if not summary:
        return None
    for key in ("trading_date", "date", "session_trading_date"):
        value = str(summary.get(key) or "").strip()
        if value:
            return value[:10]
    return None


def _date_from_runtime_clock(context: WatchdogRunContext) -> Optional[str]:
    clock = _read_json_object(context.persistence_base / "state" / "runtime_clock.json")
    if not clock:
        return None
    for key in ("active_session_trading_date", "session_trading_date", "scenario_date", "trading_date", "date"):
        value = str(clock.get(key) or "").strip()
        if not value:
            continue
        candidate = value[:10]
        try:
            datetime.strptime(candidate, "%Y-%m-%d")
            return candidate
        except ValueError:
            continue
    return None


def _latest_date_from_run_journals(context: WatchdogRunContext) -> Optional[str]:
    candidates: List[str] = []
    for base in (context.slot_journals_dir, context.execution_journals_dir):
        if not base.is_dir():
            continue
        for path in base.glob("*.json"):
            prefix = path.name[:10]
            try:
                datetime.strptime(prefix, "%Y-%m-%d")
            except ValueError:
                continue
            candidates.append(prefix)
    return max(candidates) if candidates else None


def _scenario_timetable_path(context: Optional[WatchdogRunContext]) -> Optional[Path]:
    if context is None or not context.is_run_scoped:
        return None
    scenario_dir = context.persistence_base / "playback_scenario"
    manifest = _read_json_object(scenario_dir / "playback_scenario.json")
    if not manifest:
        return None
    timetables = manifest.get("timetables")
    if not isinstance(timetables, dict):
        return None
    dates = [str(d).strip() for d in manifest.get("dates", []) if str(d).strip()]
    runtime_clock_date = _date_from_runtime_clock(context)
    active_date = runtime_clock_date or _date_from_run_summary(context) or _latest_date_from_run_journals(context)
    if active_date not in timetables:
        if runtime_clock_date:
            logger.warning(
                "WATCHDOG_SCENARIO_RUNTIME_CLOCK_INVALID: active_session_trading_date=%s "
                "missing_from_manifest=True scenario_dir=%s",
                runtime_clock_date,
                scenario_dir,
            )
            return scenario_dir / "timetables" / f"timetable_{runtime_clock_date}.json"
        active_date = dates[0] if dates else next(iter(timetables.keys()), None)
    if not active_date:
        return None
    entry = timetables.get(active_date)
    if not isinstance(entry, dict):
        return None
    raw_path = str(entry.get("path") or "").strip()
    if not raw_path:
        return None
    path = Path(raw_path)
    if not path.is_absolute():
        path = scenario_dir / path
    try:
        path = path.resolve()
    except OSError:
        return None
    if path.is_file():
        return path
    if runtime_clock_date and active_date == runtime_clock_date:
        return path
    return None


def resolve_watchdog_timetable_path(context: Optional[WatchdogRunContext] = None) -> Path:
    """
    Resolve the timetable file the watchdog should observe for the active context.

    Playback/run-scoped watchdog contexts should follow the replay timetable, matching
    the robot's playback default. Live/sim-at-root contexts continue to use
    ``timetable_current.json``.
    """
    scenario_path = _scenario_timetable_path(context)
    if scenario_path is not None:
        return scenario_path

    timetable_dir = QTSW2_ROOT / "data" / "timetable"
    if context_prefers_replay_timetable(context):
        replay_current = timetable_dir / "timetable_replay_current.json"
        if replay_current.is_file():
            return replay_current
        replay_template = timetable_dir / "timetable_replay.json"
        if replay_template.is_file():
            return replay_template
    return timetable_dir / "timetable_current.json"


class TimetablePoller:
    """Polls the active watchdog timetable file and extracts enabled streams."""

    def __init__(self):
        self._timetable_path: Optional[Path] = None

    def _resolve_timetable_path(self, context: Optional[WatchdogRunContext] = None) -> Path:
        if self._timetable_path is not None:
            return self._timetable_path
        return resolve_watchdog_timetable_path(context)

    def poll(
        self,
        context: Optional[WatchdogRunContext] = None,
    ) -> Tuple[
        Optional[str],
        Optional[Set[str]],
        Optional[str],
        Optional[Dict[str, Dict]],
        Optional[str],
        Optional[List[str]],
        Optional[str],
    ]:
        """
        Poll timetable file.

        Returns:
            (trading_date, enabled_streams_set, timetable_hash, enabled_streams_metadata,
             timetable_file_source, enabled_streams_ordered, timetable_identity_hash).

        - ``timetable_hash`` is the canonical **content** hash (Robot TimetableContentHasher parity).
        - ``timetable_identity_hash`` matches Robot runtime: JSON ``timetable_hash`` when set, else content hash.

        - ``trading_date`` is the **effective** CME session day: equals file session when it
          matches canonical CME, or is **clamped** when the file is one calendar day ahead
          before 18:00 CT (parity with RobotEngine). Replay timetables use the file date.
        - If the file is missing, corrupt, no valid session, or live session cannot be
          resolved to canonical CME: seven ``None`` values.

        Logs may include ``expected_cme_session=...`` (context only) when the file is unusable.
        """
        chicago_now = datetime.now(CHICAGO_TZ)
        utc_now = datetime.now(timezone.utc)
        expected_cme = get_cme_trading_date(utc_now)
        timetable_path = self._resolve_timetable_path(context)

        if not timetable_path.exists():
            logger.warning(
                "TIMETABLE_POLL_FAIL: timetable_unavailable=True enabled_streams_unknown=True "
                "expected_cme_session=%s reason=file_missing path=%s",
                expected_cme,
                timetable_path,
            )
            return (None, None, None, None, None, None, None)

        try:
            with open(timetable_path, "rb") as f:
                file_contents = f.read()

            try:
                timetable = json.loads(file_contents.decode("utf-8"))
            except json.JSONDecodeError as e:
                logger.error(
                    "TIMETABLE_POLL_FAIL: timetable_unavailable=True enabled_streams_unknown=True "
                    "expected_cme_session=%s parse_error=%s path=%s",
                    expected_cme,
                    e,
                    timetable_path,
                )
                return (None, None, None, None, None, None, None)

            if not isinstance(timetable, dict):
                logger.error(
                    "TIMETABLE_POLL_FAIL: timetable_unavailable=True expected_cme_session=%s "
                    "reason=not_a_json_object",
                    expected_cme,
                )
                return (None, None, None, None, None, None, None)

            timetable_hash = _compute_content_hash(timetable)
            pub_raw = timetable.get("timetable_hash")
            if isinstance(pub_raw, str) and pub_raw.strip():
                timetable_identity_hash = pub_raw.strip()
            else:
                timetable_identity_hash = timetable_hash
            timetable_file_source = _timetable_json_source(timetable)

            session_str = self._authoritative_session_from_doc(timetable)
            if session_str is None:
                raw_st = timetable.get("session_trading_date")
                raw_td = timetable.get("trading_date")
                logger.warning(
                    "TIMETABLE_POLL_FAIL: timetable_unavailable=True enabled_streams_unknown=True "
                    "expected_cme_session=%s reason=missing_or_invalid_session "
                    "session_trading_date=%r trading_date=%r path=%s",
                    expected_cme,
                    raw_st,
                    raw_td,
                    timetable_path,
                )
                return (None, None, None, None, None, None, None)

            replay = _timetable_doc_is_replay(timetable)
            effective, resolve_reason = resolve_live_execution_session_trading_date(
                session_str, utc_now, is_replay_document=replay
            )
            if effective is None:
                logger.warning(
                    "TIMETABLE_POLL_FAIL: timetable_unavailable=True enabled_streams_unknown=True "
                    "expected_cme_session=%s reason=live_session_cme_mismatch "
                    "file_session_trading_date=%s resolve_reason=%s path=%s",
                    expected_cme,
                    session_str,
                    resolve_reason,
                    timetable_path,
                )
                return (None, None, None, None, None, None, None)

            trading_date = effective
            if resolve_reason == "clamped_ahead":
                logger.warning(
                    "SESSION_START_DATE_TIMETABLE_AHEAD_CLAMPED: expected_cme_session=%s "
                    "file_session_trading_date=%s effective_session=%s note=watchdog_poller",
                    expected_cme,
                    session_str,
                    effective,
                )

            enabled_streams = self._extract_enabled_streams(timetable)
            enabled_streams_metadata = self._extract_enabled_streams_metadata(timetable)
            enabled_streams_ordered = self._extract_enabled_streams_ordered(timetable)

            streams_list = timetable.get("streams", [])
            if isinstance(streams_list, list):
                timetable_enabled_row_count = sum(
                    1
                    for e in streams_list
                    if isinstance(e, dict)
                    and str(e.get("stream") or "").strip()
                    and e.get("enabled")
                )
            else:
                timetable_enabled_row_count = 0
            if len(enabled_streams) != timetable_enabled_row_count:
                logger.warning(
                    "WATCHDOG_TIMETABLE_STREAM_MISMATCH: unique_enabled_stream_ids=%s "
                    "timetable_enabled_row_count=%s path=%s",
                    len(enabled_streams),
                    timetable_enabled_row_count,
                    timetable_path,
                )

            logger.info(
                "TIMETABLE_POLL_OK: trading_date=%s (effective_cme_session), "
                "enabled_count=%s, hash=%s, file_session=%s, resolve=%s",
                trading_date,
                len(enabled_streams),
                timetable_hash[:8] if timetable_hash else "N/A",
                session_str,
                resolve_reason,
            )

            return (
                trading_date,
                enabled_streams,
                timetable_hash,
                enabled_streams_metadata,
                timetable_file_source,
                enabled_streams_ordered,
                timetable_identity_hash,
            )

        except Exception as e:
            logger.error(
                "TIMETABLE_POLL_FAIL: timetable_unavailable=True expected_cme_session=%s "
                "unexpected_error=%s path=%s",
                expected_cme,
                e,
                timetable_path,
                exc_info=True,
            )
            return (None, None, None, None, None, None, None)

    def _authoritative_session_from_doc(self, timetable: dict) -> Optional[str]:
        """Valid session YYYY-MM-DD from session_trading_date, else legacy trading_date."""
        raw = timetable.get("session_trading_date")
        if raw is not None:
            s = str(raw).strip()
            if s and self._validate_trading_date(s):
                return s
        leg = timetable.get("trading_date")
        if leg is not None:
            s = str(leg).strip()
            if s and self._validate_trading_date(s):
                return s
        return None

    def _validate_trading_date(self, trading_date: str) -> bool:
        if not isinstance(trading_date, str):
            return False
        try:
            datetime.strptime(trading_date, "%Y-%m-%d")
            return True
        except (ValueError, TypeError):
            return False

    def _extract_enabled_streams_ordered(self, timetable: dict) -> List[str]:
        """Enabled stream IDs in timetable JSON/array order (first occurrence wins if duplicated)."""
        ordered: List[str] = []
        seen: Set[str] = set()
        streams = timetable.get("streams", [])
        if not isinstance(streams, list):
            logger.warning("Timetable 'streams' is not a list: %s", type(streams))
            return ordered
        for stream_entry in streams:
            if not isinstance(stream_entry, dict):
                continue
            stream_id = stream_entry.get("stream")
            enabled = stream_entry.get("enabled", False)
            if stream_id and enabled:
                sid = str(stream_id).strip()
                if sid and sid not in seen:
                    ordered.append(sid)
                    seen.add(sid)
        return ordered

    def _extract_enabled_streams(self, timetable: dict) -> Set[str]:
        enabled_streams: Set[str] = set()
        streams = timetable.get("streams", [])
        if not isinstance(streams, list):
            logger.warning("Timetable 'streams' is not a list: %s", type(streams))
            return enabled_streams
        for stream_entry in streams:
            if not isinstance(stream_entry, dict):
                continue
            raw_id = stream_entry.get("stream")
            stream_id = str(raw_id).strip() if raw_id is not None else ""
            enabled = stream_entry.get("enabled", False)
            if stream_id and enabled:
                enabled_streams.add(stream_id)
        return enabled_streams

    def _extract_enabled_streams_metadata(self, timetable: dict) -> Dict[str, Dict]:
        metadata: Dict[str, Dict] = {}
        streams = timetable.get("streams", [])
        if not isinstance(streams, list):
            logger.warning("Timetable 'streams' is not a list: %s", type(streams))
            return metadata
        for stream_entry in streams:
            if not isinstance(stream_entry, dict):
                continue
            raw_id = stream_entry.get("stream")
            stream_id = str(raw_id).strip() if raw_id is not None else ""
            enabled = bool(stream_entry.get("enabled", False))
            if stream_id:
                inst = (stream_entry.get("instrument") or "").strip()
                sess = (stream_entry.get("session") or "").strip()
                metadata[stream_id] = {
                    "instrument": inst or instrument_from_stream_id(stream_id),
                    "session": sess or session_from_stream_id(stream_id),
                    "slot_time": stream_entry.get("slot_time", ""),
                    "decision_time": stream_entry.get("decision_time", ""),
                    "enabled": enabled,
                    "block_reason": stream_entry.get("block_reason"),
                }
        return metadata
