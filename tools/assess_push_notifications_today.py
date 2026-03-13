#!/usr/bin/env python3
"""Assess all push notifications from today's robot logs."""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

now = datetime.now(timezone.utc)
today_start = now.replace(hour=0, minute=0, second=0, microsecond=0)

def parse_ts(s):
    try:
        return datetime.fromisoformat(s.replace('Z', '+00:00'))
    except Exception:
        return None

# Read all ENGINE logs (current + rotated)
log_dir = Path(__file__).parent.parent / "logs" / "robot"
engine_logs = list(log_dir.glob("robot_ENGINE*.jsonl"))
events = []
for p in engine_logs:
    try:
        with open(p, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    e = json.loads(line)
                    ts = parse_ts(e.get('ts_utc', ''))
                    if ts and ts >= today_start:
                        events.append(e)
                except Exception:
                    pass
    except Exception as ex:
        print(f"  [WARN] Could not read {p.name}: {ex}")

events.sort(key=lambda x: parse_ts(x.get('ts_utc', '')) or datetime.min)

print("=" * 90)
print(f"PUSH NOTIFICATIONS ASSESSMENT - {today_start.strftime('%Y-%m-%d')} (Today)")
print("=" * 90)
print()

# Actual notifications sent
sent = [e for e in events if e.get('event') == 'PUSHOVER_NOTIFY_ENQUEUED']
print(f"[NOTIFICATIONS SENT] {len(sent)}")
if sent:
    for e in sent:
        ts = parse_ts(e.get('ts_utc', ''))
        d = e.get('data', {})
        ts_str = ts.strftime("%H:%M:%S UTC") if ts else "?"
        print(f"  {ts_str} | {d.get('title', '?')} | key={d.get('notification_key', '?')}")
else:
    print("  (none)")
print()

# Critical events reported (may or may not have triggered notification)
crit = [e for e in events if e.get('event') == 'CRITICAL_EVENT_REPORTED']
print(f"[CRITICAL EVENTS REPORTED] {len(crit)}")
if crit:
    for e in crit[:15]:
        ts = parse_ts(e.get('ts_utc', ''))
        d = e.get('data', {})
        ts_str = ts.strftime("%H:%M:%S UTC") if ts else "?"
        print(f"  {ts_str} | {d.get('event_type', '?')}")
    if len(crit) > 15:
        print(f"  ... and {len(crit) - 15} more")
else:
    print("  (none)")
print()

# Skipped / Rejected
skipped = [e for e in events if e.get('event') == 'CRITICAL_NOTIFICATION_SKIPPED']
rejected = [e for e in events if e.get('event') == 'CRITICAL_NOTIFICATION_REJECTED']
print(f"[SKIPPED] {len(skipped)}")
if skipped:
    for e in skipped[:5]:
        ts = parse_ts(e.get('ts_utc', ''))
        d = e.get('data', {})
        ts_str = ts.strftime("%H:%M:%S UTC") if ts else "?"
        print(f"  {ts_str} | {d.get('reason', '?')}")
print(f"[REJECTED] {len(rejected)}")
if rejected:
    for e in rejected[:5]:
        ts = parse_ts(e.get('ts_utc', ''))
        d = e.get('data', {})
        ts_str = ts.strftime("%H:%M:%S UTC") if ts else "?"
        print(f"  {ts_str} | {d.get('reason', '?')}")
print()

# Engine tick stalls (often false positives)
stalls = [e for e in events if 'ENGINE_TICK_STALL' in e.get('event', '')]
print(f"[ENGINE TICK STALL EVENTS] {len(stalls)}")
if stalls:
    for e in stalls:
        ts = parse_ts(e.get('ts_utc', ''))
        ev = e.get('event', '')
        ts_str = ts.strftime("%H:%M:%S UTC") if ts else "?"
        print(f"  {ts_str} | {ev}")
print()

# Connection events
conn = [
    e
    for e in events
    if e.get('event')
    in [
        'CONNECTION_LOST',
        'CONNECTION_LOST_SUSTAINED',
        'CONNECTION_RECOVERED',
        'CONNECTIVITY_INCIDENT',
    ]
]
print(f"[CONNECTION EVENTS] {len(conn)}")
if conn:
    for e in conn:
        ts = parse_ts(e.get('ts_utc', ''))
        ts_str = ts.strftime("%H:%M:%S UTC") if ts else "?"
        print(f"  {ts_str} | {e.get('event', '?')}")
print()

# Notification errors (today)
err_log = log_dir / "notification_errors.log"
if err_log.exists():
    err_today = []
    try:
        with open(err_log, 'r', encoding='utf-8-sig') as f:
            for line in f:
                if today_start.strftime("%Y-%m-%d") in line:
                    err_today.append(line.strip())
    except Exception:
        pass
    print(f"[NOTIFICATION ERRORS TODAY] {len(err_today)}")
    if err_today:
        for line in err_today[:10]:
            print(f"  {line[:120]}...")
    else:
        print("  (none)")
    print()

# Summary
print("=" * 90)
print("SUMMARY")
print("=" * 90)
print(f"  Notifications sent: {len(sent)}")
print(f"  Critical events: {len(crit)}")
print(f"  Skipped: {len(skipped)} | Rejected: {len(rejected)}")
print(f"  Engine tick stalls: {len(stalls)}")
print(f"  Connection events: {len(conn)}")
