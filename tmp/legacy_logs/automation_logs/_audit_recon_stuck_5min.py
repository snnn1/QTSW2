# Parse newest 5 min robot_ENGINE.jsonl for STUCK / IEA_UNAVAILABLE / SECONDARY_SKIPPED
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


end = parse_ts(lines[-1]["ts_utc"])
ws = end - timedelta(minutes=5)
recent = [o for o in lines if (t := parse_ts(o.get("ts_utc"))) and t >= ws]

robot_rids = {
    "04a81b0c2a584246a7fff2bc229a9c6f",
    "0aa33716f12b4b2cb0968a553408a824",
    "a6a5ffb9b9a64b719847cd0c26dbbcef",
}


def payload_blob(o):
    d = o.get("data") or {}
    return d.get("payload") or json.dumps(d, sort_keys=True, default=str)


def norm_pl(s):
    return re.sub(r"\s+", " ", (s or "")[:4000])


def inst_from_obj(o):
    d = o.get("data") or {}
    for k in ("instrument", "execution_instrument", "ExecutionInstrument"):
        v = d.get(k)
        if v:
            return str(v).strip()
    pl = payload_blob(o)
    m = re.search(r"instrument[\"'\s:=]+([^,\"'}\]]+)", pl, re.I)
    if m:
        return m.group(1).strip()
    return "_unknown"


events = ["RECONCILIATION_STUCK", "RECONCILIATION_IEA_UNAVAILABLE", "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED"]
for evname in events:
    evs = [o for o in recent if o.get("event") == evname]
    print("=" * 60)
    print(evname, "count", len(evs))
    inst_c = Counter(inst_from_obj(o) for o in evs)
    print("top_instruments")
    for i, c in inst_c.most_common(15):
        print(c, i)
    # payload fingerprint: normalized full data json
    fp = Counter()
    for o in evs:
        blob = json.dumps(o.get("data") or {}, sort_keys=True, default=str)
        fp[blob] += 1
    print("unique_payloads", len(fp))
    print("top_repeated_payloads")
    for blob, c in fp.most_common(8):
        short = norm_pl(blob)[:220]
        print(c, md5(blob.encode()).hexdigest()[:12], short)
    # per (instrument, fingerprint) for STUCK and IEA_UNAV
    if evname != "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED":
        pair = Counter()
        for o in evs:
            pair[(inst_from_obj(o), md5(json.dumps(o.get("data") or {}, sort_keys=True, default=str).encode()).hexdigest()[:16])] += 1
        print("top_instrument_fingerprint_tuples")
        for (ins, h), c in pair.most_common(10):
            print(c, ins, h)
    print()

# secondary: duplicate payloads across run_ids
sec = [o for o in recent if o.get("event") == "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED"]
by_blob_rid = defaultdict(set)
for o in sec:
    blob = json.dumps(o.get("data") or {}, sort_keys=True, default=str)
    by_blob_rid[blob].add(o.get("run_id"))
triple = sum(1 for s in by_blob_rid.values() if robot_rids.issubset(s))
lines_all_three = sum(
    c
    for b, rids in ((b, s) for b, s in by_blob_rid.items() if robot_rids.issubset(s))
    for c in [sum(1 for o in sec if json.dumps(o.get("data") or {}, sort_keys=True, default=str) == b)]
)
print("SECONDARY unique_payloads", len(by_blob_rid))
print("SECONDARY payloads present on all 3 robot run_ids", triple)
print("SECONDARY lines with triplicate payload", lines_all_three)

print("WINDOW_END", end.isoformat(), "TOTAL_LINES", len(recent))
