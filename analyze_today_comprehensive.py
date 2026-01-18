import json
import re
from datetime import datetime, timedelta
from pathlib import Path
from collections import defaultdict, Counter
import sys

def analyze_today_logs():
    today = datetime.now().date()
    today_str = today.strftime("%Y-%m-%d")
    
    print(f"{'='*80}")
    print(f"COMPREHENSIVE LOG ANALYSIS FOR {today_str}")
    print(f"{'='*80}\n")
    
    # Paths
    log_dir = Path("logs/robot")
    engine_log = log_dir / "robot_ENGINE.jsonl"
    journal_dir = log_dir / "journal"
    notification_log = log_dir / "notification_errors.log"
    
    # ============================================================================
    # 1. TRADING DATE ANALYSIS
    # ============================================================================
    print("1. TRADING DATE ANALYSIS")
    print("-" * 80)
    
    trading_date_events = []
    date_mismatches = []
    
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    entry = json.loads(line)
                    ts_str = entry.get("ts_utc", "")
                    if ts_str:
                        entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                        if entry_ts.date() == today:
                            event_type = entry.get("event", "")
                            
                            if event_type == "TRADING_DATE_LOCKED":
                                trading_date_events.append({
                                    "timestamp": ts_str,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                            
                            if event_type == "BAR_DATE_MISMATCH":
                                date_mismatches.append({
                                    "timestamp": ts_str,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                except json.JSONDecodeError:
                    continue
    
    if trading_date_events:
        print(f"[OK] Trading date locked {len(trading_date_events)} time(s):")
        for event in trading_date_events:
            payload = event["data"]
            print(f"   [{event['timestamp']}] Trading Date: {payload.get('trading_date', 'N/A')}")
            print(f"      Bar Time Chicago: {payload.get('bar_timestamp_chicago', 'N/A')}")
            print(f"      Session: {payload.get('session', 'N/A')}")
            print(f"      Note: {payload.get('note', 'N/A')}")
    else:
        print(f"[WARN] No TRADING_DATE_LOCKED events found for {today_str}")
    
    if date_mismatches:
        print(f"\n[WARN] Found {len(date_mismatches)} BAR_DATE_MISMATCH events:")
        for mismatch in date_mismatches[:5]:  # Show first 5
            payload = mismatch["data"]
            print(f"   [{mismatch['timestamp']}] Locked: {payload.get('locked_trading_date', 'N/A')}, "
                  f"Bar: {payload.get('bar_trading_date', 'N/A')}")
    
    # ============================================================================
    # 2. JOURNAL ENTRIES (RANGES)
    # ============================================================================
    print(f"\n2. JOURNAL ENTRIES (RANGES) FOR {today_str}")
    print("-" * 80)
    
    today_journals = list(journal_dir.glob(f"{today_str}_*.json"))
    
    if today_journals:
        print(f"[OK] Found {len(today_journals)} journal entries:")
        for journal_file in sorted(today_journals):
            try:
                with open(journal_file, 'r', encoding='utf-8') as f:
                    journal = json.load(f)
                    stream = journal.get('Stream', 'N/A')
                    instrument = journal.get('Instrument', 'N/A')
                    session = journal.get('Session', 'N/A')
                    state = journal.get('LastState', 'N/A')
                    range_high = journal.get('RangeHigh')
                    range_low = journal.get('RangeLow')
                    freeze_close = journal.get('FreezeClose')
                    
                    print(f"\n   [{stream}] {instrument} {session}")
                    print(f"      State: {state}")
                    print(f"      RangeHigh: {range_high}, RangeLow: {range_low}")
                    print(f"      FreezeClose: {freeze_close}")
                    print(f"      Committed: {journal.get('Committed', False)}")
            except Exception as e:
                print(f"   [ERROR] Could not read {journal_file.name}: {e}")
    else:
        print(f"[WARN] No journal files found for {today_str}")
        # Show recent journal dates
        all_journals = sorted(journal_dir.glob("*.json"), key=lambda p: p.stat().st_mtime, reverse=True)
        if all_journals:
            recent_dates = sorted(set([f.name.split('_')[0] for f in all_journals[:20]]))
            print(f"   Recent journal dates: {recent_dates[:5]}")
    
    # ============================================================================
    # 3. ERROR AND WARNING ANALYSIS
    # ============================================================================
    print(f"\n3. ERROR AND WARNING ANALYSIS")
    print("-" * 80)
    
    errors = []
    warnings = []
    critical_events = []
    
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    entry = json.loads(line)
                    ts_str = entry.get("ts_utc", "")
                    if ts_str:
                        entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                        if entry_ts.date() == today:
                            level = entry.get("level", "").upper()
                            event_type = entry.get("event", "")
                            
                            if level == "ERROR":
                                errors.append({
                                    "timestamp": ts_str,
                                    "event": event_type,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                            
                            if level == "WARN":
                                warnings.append({
                                    "timestamp": ts_str,
                                    "event": event_type,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                            
                            # Critical events
                            if event_type in ["CONNECTION_LOST_SUSTAINED", "DATA_LOSS", 
                                            "RANGE_INVALIDATED", "KILL_SWITCH_ACTIVATED",
                                            "NO_BARS_IN_WINDOW", "RANGE_COMPUTE_FAILED"]:
                                critical_events.append({
                                    "timestamp": ts_str,
                                    "event": event_type,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                except json.JSONDecodeError:
                    continue
    
    print(f"Errors: {len(errors)}")
    if errors:
        error_types = Counter([e["event"] for e in errors])
        print("   Top error types:")
        for event_type, count in error_types.most_common(10):
            print(f"      {event_type}: {count}")
    
    print(f"\nWarnings: {len(warnings)}")
    if warnings:
        warn_types = Counter([w["event"] for w in warnings])
        print("   Top warning types:")
        for event_type, count in warn_types.most_common(10):
            print(f"      {event_type}: {count}")
    
    print(f"\nCritical Events: {len(critical_events)}")
    if critical_events:
        print("   Critical events:")
        for event in critical_events[:10]:  # Show first 10
            payload = event["data"]
            print(f"      [{event['timestamp']}] {event['event']}")
            if payload:
                for k, v in list(payload.items())[:3]:  # Show first 3 fields
                    print(f"         {k}: {v}")
    
    # ============================================================================
    # 4. CONNECTION AND DATA FEED ISSUES
    # ============================================================================
    print(f"\n4. CONNECTION AND DATA FEED ISSUES")
    print("-" * 80)
    
    connection_events = []
    data_stall_events = []
    
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    entry = json.loads(line)
                    ts_str = entry.get("ts_utc", "")
                    if ts_str:
                        entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                        if entry_ts.date() == today:
                            event_type = entry.get("event", "")
                            
                            if "CONNECTION" in event_type:
                                connection_events.append({
                                    "timestamp": ts_str,
                                    "event": event_type,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                            
                            if "STALL" in event_type or "DATA_LOSS" in event_type:
                                data_stall_events.append({
                                    "timestamp": ts_str,
                                    "event": event_type,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                except json.JSONDecodeError:
                    continue
    
    if connection_events:
        print(f"[ALERT] Found {len(connection_events)} connection-related events:")
        for event in connection_events[:5]:
            payload = event["data"]
            print(f"   [{event['timestamp']}] {event['event']}")
            if payload:
                for k, v in list(payload.items())[:3]:
                    print(f"      {k}: {v}")
    else:
        print("[OK] No connection issues detected")
    
    if data_stall_events:
        print(f"\n[ALERT] Found {len(data_stall_events)} data stall/loss events:")
        for event in data_stall_events[:5]:
            payload = event["data"]
            print(f"   [{event['timestamp']}] {event['event']}")
            if payload:
                for k, v in list(payload.items())[:3]:
                    print(f"      {k}: {v}")
    
    # ============================================================================
    # 5. STREAM STATE ANALYSIS
    # ============================================================================
    print(f"\n5. STREAM STATE ANALYSIS")
    print("-" * 80)
    
    stream_states = defaultdict(list)
    
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    entry = json.loads(line)
                    ts_str = entry.get("ts_utc", "")
                    if ts_str:
                        entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                        if entry_ts.date() == today:
                            event_type = entry.get("event", "")
                            
                            if "STREAM" in event_type or "RANGE" in event_type:
                                payload = entry.get("data", {}).get("payload", {})
                                stream = payload.get("stream", "")
                                if stream:
                                    stream_states[stream].append({
                                        "timestamp": ts_str,
                                        "event": event_type,
                                        "state": payload.get("state", ""),
                                        "data": payload
                                    })
                except json.JSONDecodeError:
                    continue
    
    if stream_states:
        print(f"Found events for {len(stream_states)} streams:")
        for stream, events in list(stream_states.items())[:5]:  # Show first 5 streams
            print(f"\n   Stream: {stream}")
            # Group by event type
            event_types = Counter([e["event"] for e in events])
            for event_type, count in event_types.most_common(5):
                print(f"      {event_type}: {count}")
    else:
        print("[INFO] No stream-specific events found")
    
    # ============================================================================
    # 6. NOTIFICATION ANALYSIS
    # ============================================================================
    print(f"\n6. NOTIFICATION ANALYSIS")
    print("-" * 80)
    
    notification_events = []
    
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    entry = json.loads(line)
                    ts_str = entry.get("ts_utc", "")
                    if ts_str:
                        entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                        if entry_ts.date() == today:
                            event_type = entry.get("event", "")
                            
                            if "PUSHOVER" in event_type or "NOTIFY" in event_type:
                                notification_events.append({
                                    "timestamp": ts_str,
                                    "event": event_type,
                                    "data": entry.get("data", {}).get("payload", {})
                                })
                except json.JSONDecodeError:
                    continue
    
    if notification_events:
        print(f"Found {len(notification_events)} notification events:")
        for event in notification_events[:5]:
            payload = event["data"]
            print(f"   [{event['timestamp']}] {event['event']}")
            if payload:
                for k, v in list(payload.items())[:3]:
                    print(f"      {k}: {v}")
    else:
        print("[INFO] No notification events found")
    
    # Check notification_errors.log
    if notification_log.exists():
        with open(notification_log, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
            today_notification_errors = [l for l in lines if today_str in l]
            if today_notification_errors:
                print(f"\n[ALERT] Found {len(today_notification_errors)} notification errors in log:")
                for line in today_notification_errors[-5:]:  # Show last 5
                    print(f"   {line.strip()}")
            else:
                print("\n[OK] No notification errors in log file today")
    
    # ============================================================================
    # 7. TIMELINE SUMMARY
    # ============================================================================
    print(f"\n7. TIMELINE SUMMARY")
    print("-" * 80)
    
    timeline = []
    
    if engine_log.exists():
        with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
            for line in f:
                try:
                    entry = json.loads(line)
                    ts_str = entry.get("ts_utc", "")
                    if ts_str:
                        entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                        if entry_ts.date() == today:
                            event_type = entry.get("event", "")
                            level = entry.get("level", "")
                            
                            # Only include significant events
                            if event_type in ["TRADING_DATE_LOCKED", "STREAMS_CREATED", 
                                            "CONNECTION_LOST_SUSTAINED", "DATA_LOSS",
                                            "RANGE_INVALIDATED", "KILL_SWITCH_ACTIVATED",
                                            "OPERATOR_BANNER"] or level in ["ERROR", "WARN"]:
                                timeline.append({
                                    "timestamp": ts_str,
                                    "event": event_type,
                                    "level": level
                                })
                except json.JSONDecodeError:
                    continue
    
    if timeline:
        print("Significant events timeline:")
        for event in sorted(timeline, key=lambda x: x["timestamp"])[:20]:  # Show first 20
            level_marker = "[ERROR]" if event["level"] == "ERROR" else "[WARN]" if event["level"] == "WARN" else "[INFO]"
            print(f"   {level_marker} [{event['timestamp']}] {event['event']}")
    
    # ============================================================================
    # 8. SUMMARY AND RECOMMENDATIONS
    # ============================================================================
    print(f"\n8. SUMMARY AND RECOMMENDATIONS")
    print("-" * 80)
    
    issues_found = []
    
    if not trading_date_events:
        issues_found.append("Trading date was never locked - strategy may not have started properly")
    
    if not today_journals:
        issues_found.append("No journal entries (ranges) for today - no trading activity")
    
    if len(errors) > 50:
        issues_found.append(f"High error count ({len(errors)}) - investigate error patterns")
    
    if critical_events:
        issues_found.append(f"Critical events detected: {len(critical_events)}")
    
    if connection_events:
        issues_found.append(f"Connection issues detected: {len(connection_events)}")
    
    if issues_found:
        print("[ISSUES DETECTED]:")
        for issue in issues_found:
            print(f"   - {issue}")
    else:
        print("[OK] No major issues detected")
    
    print(f"\n{'='*80}")
    print("Analysis complete!")
    print(f"{'='*80}")

if __name__ == "__main__":
    try:
        analyze_today_logs()
    except Exception as e:
        print(f"Error during analysis: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
