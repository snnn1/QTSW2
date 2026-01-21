import json
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Optional


def _parse_dt(s: Optional[str]) -> Optional[datetime]:
    if not s:
        return None
    # ISO8601 with offset: datetime.fromisoformat works.
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except Exception:
        return None


def _safe_get(d: Any, *keys: str) -> Any:
    cur = d
    for k in keys:
        if not isinstance(cur, dict):
            return None
        cur = cur.get(k)
    return cur


@dataclass
class StreamRow:
    stream: str
    instrument: str
    session: str
    slot_time: str
    enabled: bool


def load_timetable(timetable_path: Path) -> tuple[str, str, list[StreamRow]]:
    tt = json.loads(timetable_path.read_text(encoding="utf-8"))
    trading_date = str(tt.get("trading_date") or "")
    as_of = str(tt.get("as_of") or "")
    rows: list[StreamRow] = []
    for s in tt.get("streams") or []:
        rows.append(
            StreamRow(
                stream=str(s.get("stream") or ""),
                instrument=str(s.get("instrument") or ""),
                session=str(s.get("session") or ""),
                slot_time=str(s.get("slot_time") or ""),
                enabled=bool(s.get("enabled")),
            )
        )
    return trading_date, as_of, rows


def scan_latest_engine_tick(engine_log: Path) -> dict[str, Any]:
    last: dict[str, Any] = {}
    for line in engine_log.open(encoding="utf-8"):
        try:
            e = json.loads(line)
        except Exception:
            continue
        if e.get("event") == "ENGINE_TICK_HEARTBEAT":
            last = e
    return last


def scan_stream_events(log_dir: Path, enabled_stream_ids: set[str]) -> dict[str, dict[str, Any]]:
    """
    Returns per-stream:
      - latest_event_ts_utc
      - latest_event
      - latest_state (from data.state)
      - latest_range_diag (payload subset if present)
      - latest_armed_diag (payload subset if present)
      - latest_transition (event name + reason if present)
    """
    info: dict[str, dict[str, Any]] = {sid: {} for sid in enabled_stream_ids}

    def update_latest(sid: str, ts_utc: str, event: str, state: Any, payload: Any, file_name: str) -> None:
        prev_ts = info[sid].get("latest_event_ts_utc")
        if prev_ts is None or ts_utc > prev_ts:
            info[sid]["latest_event_ts_utc"] = ts_utc
            info[sid]["latest_event"] = event
            info[sid]["latest_state"] = state
            info[sid]["latest_file"] = file_name

    def update_named(sid: str, ts_utc: str, name: str, payload: dict[str, Any]) -> None:
        prev_ts = info[sid].get(f"{name}_ts_utc")
        if prev_ts is None or ts_utc > prev_ts:
            info[sid][f"{name}_ts_utc"] = ts_utc
            info[sid][name] = payload

    jsonl_files = list(log_dir.glob("robot_*.jsonl"))
    # Only read main current files (skip archives) unless there are no main files.
    if not jsonl_files:
        jsonl_files = list((log_dir / "archive").glob("robot_*.jsonl"))

    for fp in jsonl_files:
        try:
            for line in fp.open(encoding="utf-8"):
                try:
                    e = json.loads(line)
                except Exception:
                    continue
                event = str(e.get("event") or "")
                ts_utc = str(e.get("ts_utc") or "")
                data = e.get("data") or {}
                payload = data.get("payload")
                if not isinstance(payload, dict):
                    continue
                sid = payload.get("stream_id") or payload.get("stream")
                if sid not in enabled_stream_ids:
                    continue

                state = data.get("state")
                update_latest(sid, ts_utc, event, state, payload, fp.name)

                if event == "PRE_HYDRATION_RANGE_START_DIAGNOSTIC":
                    update_named(
                        sid,
                        ts_utc,
                        "latest_range_diag",
                        {
                            "now_chicago": payload.get("now_chicago"),
                            "range_start_chicago_raw": payload.get("range_start_chicago_raw"),
                            "minutes_until_range_start": payload.get("minutes_until_range_start"),
                            "minutes_past_range_start": payload.get("minutes_past_range_start"),
                            "is_before_range_start": payload.get("is_before_range_start"),
                        },
                    )
                elif event == "ARMED_STATE_DIAGNOSTIC":
                    update_named(
                        sid,
                        ts_utc,
                        "latest_armed_diag",
                        {
                            "utc_now": payload.get("utc_now"),
                            "range_start_chicago": payload.get("range_start_chicago"),
                            "slot_time_chicago": payload.get("slot_time_chicago"),
                            "time_until_range_start_minutes": payload.get("time_until_range_start_minutes"),
                            "can_transition": payload.get("can_transition"),
                        },
                    )
                elif event == "STREAM_STATE_TRANSITION":
                    update_named(
                        sid,
                        ts_utc,
                        "latest_transition",
                        {
                            "from": payload.get("from_state"),
                            "to": payload.get("to_state"),
                            "reason": payload.get("reason"),
                        },
                    )
        except Exception:
            continue

    return info


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    timetable_path = root / "data" / "timetable" / "timetable_current.json"
    log_dir = root / "logs" / "robot"
    engine_log = log_dir / "robot_ENGINE.jsonl"

    trading_date, as_of, rows = load_timetable(timetable_path)
    enabled_rows = [r for r in rows if r.enabled]
    enabled_stream_ids = {r.stream for r in enabled_rows}

    tick = scan_latest_engine_tick(engine_log)
    tick_utc = _safe_get(tick, "data", "payload", "utc_now") or tick.get("ts_utc")

    print("================================================================================")
    print("TIMETABLE vs CURRENT TIME INVESTIGATION")
    print("================================================================================")
    print(f"Timetable trading_date: {trading_date}")
    print(f"Timetable as_of:        {as_of}")
    print(f"Enabled streams ({len(enabled_rows)}): " + ", ".join(sorted(enabled_stream_ids)))
    print("")
    print(f"Latest ENGINE_TICK_HEARTBEAT utc_now: {tick_utc}")
    print("")

    stream_info = scan_stream_events(log_dir, enabled_stream_ids)

    print("Per enabled stream: latest state + range timing diagnostics")
    for r in sorted(enabled_rows, key=lambda x: x.stream):
        si = stream_info.get(r.stream) or {}
        latest_state = si.get("latest_state")
        latest_event = si.get("latest_event")
        latest_ts = si.get("latest_event_ts_utc")
        range_diag = si.get("latest_range_diag")
        armed_diag = si.get("latest_armed_diag")
        transition = si.get("latest_transition")

        print(f"- {r.stream} ({r.instrument} {r.session} {r.slot_time})")
        print(f"    latest: ts_utc={latest_ts} state={latest_state} event={latest_event}")
        if transition:
            print(f"    last transition: {transition}")
        if range_diag:
            print(f"    range diag: {range_diag}")
        if armed_diag:
            print(f"    armed diag: {armed_diag}")
    print("")

    # High-signal conclusion helper: any stream stuck in PRE_HYDRATION while past range start?
    stuck = []
    for sid, si in stream_info.items():
        if si.get("latest_state") != "PRE_HYDRATION":
            continue
        rd = si.get("latest_range_diag")
        if isinstance(rd, dict) and rd.get("is_before_range_start") is False:
            stuck.append((sid, rd))
    if stuck:
        print("!! FOUND PRE_HYDRATION past range start (should have switched):")
        for sid, rd in sorted(stuck, key=lambda x: x[0]):
            print(f"  - {sid}: {rd}")
    else:
        print("No enabled stream is clearly 'past range start' while still in PRE_HYDRATION (based on diagnostics).")

    print("================================================================================")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

