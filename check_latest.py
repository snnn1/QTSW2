import json

# Feed tail
with open(r"c:\Users\jakej\QTSW2\logs\robot\frontend_feed.jsonl") as f:
    lines = f.readlines()[-5:]
print("=== Last 5 events in frontend_feed.jsonl ===")
for line in lines:
    e = json.loads(line)
    ts = (e.get("timestamp_chicago") or "")[:25]
    rid = (e.get("run_id") or "")[:8]
    print(f"  {e.get('event_type')}  {ts}  run_id={rid}...")

# API
print("\n=== API /events (last 3 by timestamp) ===")
import urllib.request
r = urllib.request.urlopen("http://localhost:8002/api/watchdog/events?since_seq=0", timeout=5)
d = json.loads(r.read().decode())
evts = sorted(d.get("events", []), key=lambda x: x.get("timestamp_chicago", ""))
print("run_id returned:", d.get("run_id"))
if evts:
    for e in evts[-3:]:
        ts = (e.get("timestamp_chicago") or "")[:25]
        print(f"  {e.get('event_type')}  {ts}")
