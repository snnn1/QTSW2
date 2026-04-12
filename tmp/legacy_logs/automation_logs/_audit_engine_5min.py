# One-off audit: newest 5 min of logs/robot/robot_ENGINE.jsonl
import json
import re
from collections import Counter, defaultdict
from datetime import datetime, timedelta
from hashlib import md5

path = r"c:\Users\jakej\QTSW2\logs\robot\robot_ENGINE.jsonl"
lines = []
with open(path, encoding="utf-8-sig", errors="replace") as f:
    for l in f:
        l = l.strip()
        if not l:
            continue
        try:
            lines.append(json.loads(l))
        except json.JSONDecodeError:
            pass


def parse_ts(s):
    if not s:
        return None
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


newest = None
for o in reversed(lines):
    t = parse_ts(o.get("ts_utc"))
    if t:
        newest = t
        break

ws = newest - timedelta(minutes=5)
recent = [o for o in lines if (t := parse_ts(o.get("ts_utc"))) and t >= ws]

print("WINDOW_END_UTC", newest.isoformat() if newest else None)
print("TOTAL_LINES", len(recent))

# --- BAR ---
bars = [o for o in recent if o.get("event") == "BAR_RECEIVED_NO_STREAMS"]
print("BAR_RECEIVED_NO_STREAMS_TOTAL", len(bars))

inst_c = Counter()
rid_c = Counter()
for o in bars:
    rid = o.get("run_id") or "_none"
    rid_c[rid] += 1
    d = o.get("data") or {}
    pl = d.get("payload") or ""
    m = re.search(r"instrument\s*=\s*([^,}]+)", pl)
    inst = m.group(1).strip() if m else (d.get("instrument") or "_unknown")
    inst_c[inst] += 1

print("BAR_BY_RUN_ID")
for r, c in rid_c.most_common(15):
    print(c, r)
print("BAR_BY_INSTRUMENT_TOP20")
for i, c in inst_c.most_common(20):
    print(c, i)

# --- Reconciliation ---
rec = Counter(o.get("event") for o in recent if (o.get("event") or "").startswith("RECONCILIATION_"))
print("RECONCILIATION_SUBTOTAL", sum(rec.values()))
for e, c in rec.most_common(50):
    print(c, e)

iea = Counter(o.get("event") for o in recent if (o.get("event") or "").startswith("IEA_"))
print("IEA_SUBTOTAL", sum(iea.values()))
for e, c in iea.most_common(25):
    print(c, e)

gate = []
for o in recent:
    if o.get("event") == "UNREGISTERED_EVENT_TYPE":
        et = (o.get("data") or {}).get("event_type") or ""
        if "GATE" in et or "CONSISTENCY" in et:
            gate.append(et)
gc = Counter(gate)
print("GATE_UNREGISTERED_TOTAL", len(gate))
for e, c in gc.most_common():
    print(c, e)

# --- run_ids ---
rall = Counter(o.get("run_id") for o in recent)
print("DISTINCT_RUN_IDS", len(rall))
for r, c in rall.most_common():
    print(c, r)

# Duplication: same (event, data prefix) across run_ids
by_h = defaultdict(list)
for o in recent:
    rid = o.get("run_id")
    blob = json.dumps(o.get("data") or {}, sort_keys=True, default=str)[:2500]
    key = (o.get("event"), blob)
    h = md5(json.dumps(key, default=str).encode()).hexdigest()[:20]
    by_h[h].append(rid)

dup_lines = 0
multi_keys = 0
for h, rids in by_h.items():
    cr = Counter(rids)
    if_len = len([x for x in cr.values() if x > 0])
    if len(cr) > 1:
        multi_keys += 1
        dup_lines += sum(cr.values()) - max(cr.values())

print("UNIQUE_EVENT_DATA_KEYS", len(by_h))
print("KEYS_WITH_MULTI_RUN_ID", multi_keys)
print("EST_DUPLICATE_LINES_FROM_MULTI_RUN", dup_lines, "pct", round(100 * dup_lines / len(recent), 2) if recent else 0)

# RECONCILIATION_PASS_SUMMARY by instrument (best-effort)
rps = [o for o in recent if o.get("event") == "RECONCILIATION_PASS_SUMMARY"]
ins_ps = Counter()
for o in rps:
    d = o.get("data") or {}
    pl = d.get("payload") or json.dumps(d, default=str)
    m = re.search(r"instrument\s*=\s*([^,}]+)", pl, re.I)
    ins_ps[(m.group(1).strip() if m else "_unk")] += 1
print("RECONCILIATION_PASS_SUMMARY_TOTAL", len(rps))
print("PASS_SUMMARY_BY_INSTRUMENT_TOP15")
for i, c in ins_ps.most_common(15):
    print(c, i)

# SECONDARY skipped — distinct (instrument, owner, current) tuples
sec = [o for o in recent if o.get("event") == "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED"]
print("RECONCILIATION_SECONDARY_INSTANCE_SKIPPED_TOTAL", len(sec))
sec_inst = Counter()
for o in sec:
    d = o.get("data") or {}
    ins = d.get("instrument") or "_unk"
    sec_inst[ins] += 1
print("SECONDARY_SKIPPED_BY_INSTRUMENT_TOP15")
for i, c in sec_inst.most_common(15):
    print(c, i)
