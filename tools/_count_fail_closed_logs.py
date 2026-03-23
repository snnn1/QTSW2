import os
import re

ROOT = os.path.join(os.path.dirname(__file__), "..", "logs")

# Prefer top-level JSON "event" strings to avoid double-counting health echoes
# (e.g. CRITICAL_EVENT_REPORTED embeds event_type:DISCONNECT_FAIL_CLOSED_ENTERED).
patterns = {
    "event_RECONCILIATION_MISMATCH_FAIL_CLOSED": '"event":"RECONCILIATION_MISMATCH_FAIL_CLOSED"',
    "event_DISCONNECT_FAIL_CLOSED_ENTERED": '"event":"DISCONNECT_FAIL_CLOSED_ENTERED"',
    "FAIL_CLOSED_FLATTEN_or_flatten_note": re.compile(r"FAIL_CLOSED_FLATTEN|fail-closed flatten"),
    "EXECUTION_BLOCKED_DISCONNECT": '"reason":"DISCONNECT_FAIL_CLOSED"',
    "escalation_state_FAIL_CLOSED_in_JSON": '"escalation_state":"FAIL_CLOSED"',
    # Broad substring (includes CRITICAL_EVENT_REPORTED wrappers — do not use as primary)
    "substring_DISCONNECT_FAIL_CLOSED_ENTERED_anywhere": "DISCONNECT_FAIL_CLOSED_ENTERED",
}

counts = {k: 0 for k in patterns}
by_zone = {
    "robot_jsonl": {k: 0 for k in patterns},
    "health_jsonl": {k: 0 for k in patterns},
    "other_jsonl": {k: 0 for k in patterns},
}
files = 0
for dirpath, _, filenames in os.walk(ROOT):
    for fn in filenames:
        if not fn.endswith(".jsonl"):
            continue
        files += 1
        path = os.path.join(dirpath, fn)
        rel = os.path.relpath(path, ROOT).replace("\\", "/")
        if rel.startswith("robot/"):
            zone = "robot_jsonl"
        elif rel.startswith("health/"):
            zone = "health_jsonl"
        else:
            zone = "other_jsonl"
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError:
            continue
        for k, pat in patterns.items():
            if isinstance(pat, re.Pattern):
                n = len(pat.findall(text))
            else:
                n = text.count(pat)
            counts[k] += n
            by_zone[zone][k] += n

print("jsonl_files_scanned", files)
for k, v in sorted(counts.items()):
    print(f"{k}: {v}")
print("--- by zone (robot / health / other) ---")
for zone in ("robot_jsonl", "health_jsonl", "other_jsonl"):
    print(zone + ":")
    for k, v in sorted(by_zone[zone].items()):
        if v:
            print(f"  {k}: {v}")
