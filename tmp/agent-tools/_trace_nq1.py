import json
from pathlib import Path

rid = "1eee4463d7c3443daccbd995cccc3f84"
root = Path(r"c:\Users\jakej\QTSW2\logs\robot")
want_substrings = (
    "STREAM_CREATED",
    "STREAM_INITIALIZED",
    "STREAM_STATE_TRANSITION",
    "PRE_HYDRATION_WAITING_FOR_BARSREQUEST",
    "PRE_HYDRATION_COMPLETE_SET",
    "ARMED_WAITING_FOR_BARS",
    "RANGE_WINDOW_STARTED",
    "RANGE_BUILDING_START",
    "RANGE_LOCK_BLOCKED_BARSREQUEST_PENDING",
    "RANGE_LOCK_FAILED",
    "RANGE_COMPUTE_NO_BARS_DIAGNOSTIC",
    "RANGE_LOCKED",
    "SLOT_EXPIRED",
    "BAR_DATE_MISMATCH",
    "BAR_PARTIAL_REJECTED",
    "BAR_RECEIVED_BEFORE_DATE_LOCKED",
    "RANGE_BUILD_START",
    "PRE_HYDRATION_COMPLETE_SIM",
    "PRE_HYDRATION_TO_ARMED",
    "SLOT_STATUS_CHANGED",
)

rows = []
for fp in sorted(root.glob("*.jsonl")):
    for line in fp.open(encoding="utf-8"):
        if rid not in line:
            continue
        try:
            o = json.loads(line)
        except json.JSONDecodeError:
            continue
        ev = o.get("event") or ""
        blob = json.dumps(o, ensure_ascii=False)
        if o.get("stream") != "NQ1":
            if "NQ1" not in blob and "MNQ" not in blob:
                continue
            # engine-level bar events: keep only if MNQ/NQ
            if ev.startswith("BAR_") and "MNQ" not in blob and "NQ" not in blob:
                continue
        if not (
            ev.startswith("NO_TRADE")
            or any(x in ev for x in want_substrings)
            or ev
            in (
                "PRE_HYDRATION_TIMEOUT_NO_BARS",
                "PRE_HYDRATION_COMPLETE",
                "PRE_HYDRATION_TO_ARMED_TRANSITION",
            )
        ):
            continue
        rows.append((o.get("ts_utc") or "", fp.name, ev, o.get("stream"), o))

rows.sort(key=lambda x: x[0])

for ts, fn, ev, st, o in rows:
    d = o.get("data") or {}
    if isinstance(d, dict):
        payload = d.get("payload")
        if isinstance(payload, str):
            reason = payload[:220]
        else:
            parts = []
            for k in ("previous_state", "new_state", "message", "committed", "commit_reason"):
                if k in d:
                    parts.append(f"{k}={d[k]}")
            reason = "; ".join(parts) if parts else str(d)[:220]
    else:
        reason = str(d)[:220]
    print(f"{ts}\t{ev}\t{st}\t{reason}")
