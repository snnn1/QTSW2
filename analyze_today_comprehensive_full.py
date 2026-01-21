#!/usr/bin/env python3
"""
Comprehensive Robot Log Analysis - Full Analysis
Analyzes all of today's robot logs to produce a detailed failure analysis.
"""

import json
import sys
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict, Counter
from typing import Dict, List, Optional, Any, Tuple
import pytz

# Setup paths
QTSW2_ROOT = Path(__file__).parent
LOGS_DIR = QTSW2_ROOT / "logs" / "robot"
JOURNAL_DIR = LOGS_DIR / "journal"
OUTPUT_DIR = QTSW2_ROOT / "docs" / "robot"
CHICAGO_TZ = pytz.timezone("America/Chicago")

# Get today's date
today = datetime.now(CHICAGO_TZ).date()
today_str = today.strftime("%Y-%m-%d")


class LogAnalyzer:
    """Comprehensive log analyzer for robot logs."""
    
    def __init__(self, date_str: str):
        self.date_str = date_str
        self.date = datetime.strptime(date_str, "%Y-%m-%d").date()
        self.engine_events = []
        self.instrument_events = defaultdict(list)
        self.journals = {}
        
        # Analysis results
        self.startup_info = {}
        self.barsrequest_info = defaultdict(dict)
        self.bar_stats = defaultdict(lambda: {"accepted": 0, "rejected": 0, "reasons": Counter()})
        self.stream_states = defaultdict(list)
        self.errors = []
        self.warnings = []
        self.timeline = []
        
    def load_logs(self):
        """Load all log files for today."""
        print(f"Loading logs for {self.date_str}...")
        
        # Load ENGINE log
        engine_log = LOGS_DIR / "robot_ENGINE.jsonl"
        if engine_log.exists():
            print(f"  Loading ENGINE log: {engine_log}")
            with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
                for line_num, line in enumerate(f, 1):
                    try:
                        entry = json.loads(line.strip())
                        ts_str = entry.get("ts_utc", "")
                        if ts_str:
                            entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                            if entry_ts.date() == self.date:
                                self.engine_events.append(entry)
                    except json.JSONDecodeError:
                        continue
                    except Exception as e:
                        if line_num % 10000 == 0:
                            print(f"    Warning: Error parsing line {line_num}: {e}")
            print(f"    Loaded {len(self.engine_events)} ENGINE events")
        else:
            print(f"  WARNING: ENGINE log not found: {engine_log}")
        
        # Load per-instrument logs
        instrument_logs = list(LOGS_DIR.glob("robot_*.jsonl"))
        instrument_logs = [f for f in instrument_logs if f.name != "robot_ENGINE.jsonl" and not f.name.startswith("robot_ENGINE_")]
        
        for log_file in instrument_logs:
            instrument = self._extract_instrument(log_file.name)
            if instrument:
                print(f"  Loading {instrument} log: {log_file.name}")
                count = 0
                with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
                    for line in f:
                        try:
                            entry = json.loads(line.strip())
                            ts_str = entry.get("ts_utc", "")
                            if ts_str:
                                entry_ts = datetime.fromisoformat(ts_str.replace("Z", "+00:00"))
                                if entry_ts.date() == self.date:
                                    self.instrument_events[instrument].append(entry)
                                    count += 1
                        except json.JSONDecodeError:
                            continue
                print(f"    Loaded {count} events for {instrument}")
        
        # Load journal files
        journal_files = list(JOURNAL_DIR.glob(f"{self.date_str}_*.json"))
        for journal_file in journal_files:
            try:
                with open(journal_file, 'r', encoding='utf-8') as f:
                    journal = json.load(f)
                    stream = journal.get('Stream') or journal.get('stream', 'UNKNOWN')
                    self.journals[stream] = journal
            except Exception as e:
                print(f"  WARNING: Could not load journal {journal_file.name}: {e}")
        
        print(f"\nLoaded {len(self.engine_events)} ENGINE events, {sum(len(v) for v in self.instrument_events.values())} instrument events, {len(self.journals)} journals")
    
    def _extract_instrument(self, filename: str) -> Optional[str]:
        """Extract instrument name from log filename."""
        # robot_ES.jsonl -> ES
        # robot_ES_abc123.jsonl -> ES
        parts = filename.replace("robot_", "").replace(".jsonl", "").split("_")
        if parts:
            inst = parts[0]
            if inst and inst != "skeleton" and inst != "dryrun":
                return inst.upper()
        return None
    
    def analyze_engine_startup(self):
        """Analyze engine startup and initialization."""
        print("\nAnalyzing engine startup...")
        
        startup_events = {
            "ENGINE_START": [],
            "TRADING_DATE_LOCKED": [],
            "STREAMS_CREATED": [],
            "OPERATOR_BANNER": [],
            "ADAPTER_SELECTED": [],
            "EXECUTION_MODE_SET": [],
            "HEALTH_MONITOR_CONFIG_LOADED": [],
            "HEALTH_MONITOR_DISABLED": [],
            "HEALTH_MONITOR_CONFIG_MISSING": [],
        }
        
        for event in self.engine_events:
            event_type = event.get("event", "")
            if event_type in startup_events:
                startup_events[event_type].append({
                    "timestamp": event.get("ts_utc", ""),
                    "payload": event.get("data", {}).get("payload", {})
                })
        
        self.startup_info = {
            "engine_start": startup_events["ENGINE_START"][-1] if startup_events["ENGINE_START"] else None,
            "trading_date_locked": startup_events["TRADING_DATE_LOCKED"][-1] if startup_events["TRADING_DATE_LOCKED"] else None,
            "streams_created": startup_events["STREAMS_CREATED"][-1] if startup_events["STREAMS_CREATED"] else None,
            "operator_banner": startup_events["OPERATOR_BANNER"][-1] if startup_events["OPERATOR_BANNER"] else None,
            "adapter_selected": startup_events["ADAPTER_SELECTED"][-1] if startup_events["ADAPTER_SELECTED"] else None,
            "execution_mode": startup_events["EXECUTION_MODE_SET"][-1] if startup_events["EXECUTION_MODE_SET"] else None,
            "health_monitor": startup_events["HEALTH_MONITOR_CONFIG_LOADED"][-1] or startup_events["HEALTH_MONITOR_DISABLED"][-1] or startup_events["HEALTH_MONITOR_CONFIG_MISSING"][-1],
        }
        
        print(f"  Engine start: {self.startup_info['engine_start']['timestamp'] if self.startup_info['engine_start'] else 'NOT FOUND'}")
        print(f"  Trading date locked: {self.startup_info['trading_date_locked']['timestamp'] if self.startup_info['trading_date_locked'] else 'NOT FOUND'}")
    
    def analyze_barsrequest(self):
        """Analyze BarsRequest execution."""
        print("\nAnalyzing BarsRequest...")
        
        barsrequest_events = {
            "BARSREQUEST_INITIALIZATION": [],
            "BARSREQUEST_RAW_RESULT": [],
            "BARSREQUEST_SKIPPED": [],
            "BARSREQUEST_STREAM_STATUS": [],
            "BARSREQUEST_RANGE_DETERMINED": [],
            "PRE_HYDRATION_BARS_LOADED": [],
        }
        
        for event in self.engine_events:
            event_type = event.get("event", "")
            if event_type in barsrequest_events:
                data = event.get("data", {})
                payload = data.get("payload", {})
                # Some events have instrument in data, others in payload
                instrument = payload.get("instrument") or data.get("instrument", "UNKNOWN")
                barsrequest_events[event_type].append({
                    "timestamp": event.get("ts_utc", ""),
                    "instrument": instrument,
                    "payload": payload if payload else data,  # Use data if payload is empty
                    "full_data": data  # Keep full data for skipped events
                })
        
        # Group by instrument
        for event_type, events in barsrequest_events.items():
            for event in events:
                inst = event["instrument"]
                if inst not in self.barsrequest_info:
                    self.barsrequest_info[inst] = {
                        "initialization": [],
                        "raw_result": [],
                        "skipped": [],
                        "stream_status": [],
                        "range_determined": [],
                        "bars_loaded": [],
                    }
                if event_type == "BARSREQUEST_INITIALIZATION":
                    self.barsrequest_info[inst]["initialization"].append(event)
                elif event_type == "BARSREQUEST_RAW_RESULT":
                    self.barsrequest_info[inst]["raw_result"].append(event)
                elif event_type == "BARSREQUEST_SKIPPED":
                    # For skipped events, reason might be in full_data
                    skipped_event = event.copy()
                    if "full_data" in event:
                        skipped_event["payload"] = event["full_data"]
                    self.barsrequest_info[inst]["skipped"].append(skipped_event)
                elif event_type == "BARSREQUEST_STREAM_STATUS":
                    # Track stream status during BarsRequest
                    if "stream_status" not in self.barsrequest_info[inst]:
                        self.barsrequest_info[inst]["stream_status"] = []
                    self.barsrequest_info[inst]["stream_status"].append(event)
                elif event_type == "BARSREQUEST_RANGE_DETERMINED":
                    # Track range determination
                    if "range_determined" not in self.barsrequest_info[inst]:
                        self.barsrequest_info[inst]["range_determined"] = []
                    self.barsrequest_info[inst]["range_determined"].append(event)
                elif event_type == "PRE_HYDRATION_BARS_LOADED":
                    self.barsrequest_info[inst]["bars_loaded"].append(event)
        
        print(f"  Found BarsRequest events for {len(self.barsrequest_info)} instruments")
        for inst, info in self.barsrequest_info.items():
            init_count = len(info["initialization"])
            skipped_count = len(info["skipped"])
            result_count = len(info["raw_result"])
            bars_count = len(info["bars_loaded"])
            print(f"    {inst}: {init_count} init, {skipped_count} skipped, {result_count} results, {bars_count} loaded")
    
    def analyze_bar_acceptance(self):
        """Analyze bar acceptance and rejection."""
        print("\nAnalyzing bar acceptance/rejection...")
        
        for event in self.engine_events:
            event_type = event.get("event", "")
            payload = event.get("data", {}).get("payload", {})
            instrument = payload.get("instrument", "UNKNOWN")
            
            if event_type == "BAR_ACCEPTED":
                self.bar_stats[instrument]["accepted"] += 1
            elif event_type in ["BAR_REJECTED", "BAR_PARTIAL_REJECTED"]:
                self.bar_stats[instrument]["rejected"] += 1
                reason = payload.get("reason", event_type)
                self.bar_stats[instrument]["reasons"][reason] += 1
            elif event_type == "BAR_DATE_MISMATCH":
                self.bar_stats[instrument]["rejected"] += 1
                self.bar_stats[instrument]["reasons"]["BAR_DATE_MISMATCH"] += 1
        
        print(f"  Bar statistics for {len(self.bar_stats)} instruments:")
        for inst, stats in sorted(self.bar_stats.items()):
            total = stats["accepted"] + stats["rejected"]
            if total > 0:
                accept_rate = (stats["accepted"] / total) * 100
                print(f"    {inst}: {stats['accepted']} accepted, {stats['rejected']} rejected ({accept_rate:.1f}% acceptance)")
                if stats["reasons"]:
                    print(f"      Rejection reasons: {dict(stats['reasons'])}")
    
    def analyze_stream_states(self):
        """Analyze stream state transitions."""
        print("\nAnalyzing stream state transitions...")
        
        # Track state changes from various event types
        # STATE_TRANSITION events
        for event in self.engine_events:
            event_type = event.get("event", "")
            if event_type == "STATE_TRANSITION":
                payload = event.get("data", {}).get("payload", {})
                stream = payload.get("stream", "")
                if stream:
                    self.stream_states[stream].append({
                        "timestamp": event.get("ts_utc", ""),
                        "from_state": payload.get("from_state", ""),
                        "to_state": payload.get("to_state", ""),
                        "reason": payload.get("reason", ""),
                        "source": "STATE_TRANSITION"
                    })
        
        # From instrument logs
        for instrument, events in self.instrument_events.items():
            for event in events:
                event_type = event.get("event", "")
                if event_type == "STATE_TRANSITION":
                    payload = event.get("data", {}).get("payload", {})
                    stream = payload.get("stream", "")
                    if stream:
                        self.stream_states[stream].append({
                            "timestamp": event.get("ts_utc", ""),
                            "from_state": payload.get("from_state", ""),
                            "to_state": payload.get("to_state", ""),
                            "reason": payload.get("reason", ""),
                            "source": "STATE_TRANSITION"
                        })
        
        # Also track state from GAP_VIOLATIONS_SUMMARY and other events that show state
        for event in self.engine_events:
            event_type = event.get("event", "")
            if event_type in ["GAP_VIOLATIONS_SUMMARY", "BARSREQUEST_STREAM_STATUS", "STREAMS_CREATED"]:
                payload = event.get("data", {}).get("payload", {})
                streams_info = payload.get("streams", [])
                if not streams_info and "invalidated_streams" in payload:
                    streams_info = payload.get("invalidated_streams", [])
                
                for stream_info in streams_info:
                    stream_id = stream_info.get("stream_id", "")
                    state = stream_info.get("state", "")
                    if stream_id and state:
                        # Check if this is a new state
                        if stream_id not in self.stream_states or not self.stream_states[stream_id]:
                            self.stream_states[stream_id].append({
                                "timestamp": event.get("ts_utc", ""),
                                "from_state": "UNKNOWN",
                                "to_state": state,
                                "reason": f"From {event_type}",
                                "source": event_type
                            })
                        else:
                            # Check if state changed
                            last_state = self.stream_states[stream_id][-1]["to_state"]
                            if state != last_state:
                                self.stream_states[stream_id].append({
                                    "timestamp": event.get("ts_utc", ""),
                                    "from_state": last_state,
                                    "to_state": state,
                                    "reason": f"From {event_type}",
                                    "source": event_type
                                })
        
        print(f"  Found state transitions for {len(self.stream_states)} streams")
        for stream, transitions in sorted(self.stream_states.items()):
            print(f"    {stream}: {len(transitions)} transitions")
            if transitions:
                first = transitions[0]
                last = transitions[-1]
                print(f"      First: {first['from_state']} -> {first['to_state']} at {first['timestamp']}")
                print(f"      Last: {last['from_state']} -> {last['to_state']} at {last['timestamp']}")
    
    def analyze_range_computation(self):
        """Analyze range computation."""
        print("\nAnalyzing range computation...")
        
        range_events = {
            "RANGE_COMPUTE_START": [],
            "RANGE_LOCK_ASSERT": [],
            "RANGE_COMPUTE_FAILED": [],
        }
        
        all_events = self.engine_events + [e for events in self.instrument_events.values() for e in events]
        
        for event in all_events:
            event_type = event.get("event", "")
            if event_type in range_events:
                payload = event.get("data", {}).get("payload", {})
                stream = payload.get("stream", "")
                range_events[event_type].append({
                    "timestamp": event.get("ts_utc", ""),
                    "stream": stream,
                    "payload": payload
                })
        
        print(f"  Range computation events:")
        print(f"    RANGE_COMPUTE_START: {len(range_events['RANGE_COMPUTE_START'])}")
        print(f"    RANGE_LOCK_ASSERT: {len(range_events['RANGE_LOCK_ASSERT'])}")
        print(f"    RANGE_COMPUTE_FAILED: {len(range_events['RANGE_COMPUTE_FAILED'])}")
        
        return range_events
    
    def analyze_errors_warnings(self):
        """Analyze errors and warnings."""
        print("\nAnalyzing errors and warnings...")
        
        critical_events = {
            "CONNECTION_LOST_SUSTAINED",
            "DATA_LOSS",
            "RANGE_INVALIDATED",
            "KILL_SWITCH_ACTIVATED",
            "GAP_VIOLATIONS_SUMMARY",
            "DATA_LOSS_DETECTED",
        }
        
        all_events = self.engine_events + [e for events in self.instrument_events.values() for e in events]
        
        for event in all_events:
            level = event.get("level", "").upper()
            event_type = event.get("event", "")
            
            if level == "ERROR":
                self.errors.append({
                    "timestamp": event.get("ts_utc", ""),
                    "event": event_type,
                    "payload": event.get("data", {}).get("payload", {})
                })
            
            if level == "WARN":
                self.warnings.append({
                    "timestamp": event.get("ts_utc", ""),
                    "event": event_type,
                    "payload": event.get("data", {}).get("payload", {})
                })
        
        error_types = Counter([e["event"] for e in self.errors])
        warn_types = Counter([w["event"] for w in self.warnings])
        
        print(f"  Found {len(self.errors)} errors, {len(self.warnings)} warnings")
        print(f"  Top error types: {dict(error_types.most_common(10))}")
        print(f"  Top warning types: {dict(warn_types.most_common(10))}")
        
        return error_types, warn_types
    
    def build_timeline(self):
        """Build chronological timeline of significant events."""
        print("\nBuilding timeline...")
        
        timeline_events = []
        
        all_events = self.engine_events + [e for events in self.instrument_events.values() for e in events]
        
        significant_event_types = {
            "ENGINE_START",
            "TRADING_DATE_LOCKED",
            "STREAMS_CREATED",
            "BARSREQUEST_INITIALIZATION",
            "BARSREQUEST_SKIPPED",
            "BARSREQUEST_RAW_RESULT",
            "PRE_HYDRATION_BARS_LOADED",
            "STATE_TRANSITION",
            "RANGE_COMPUTE_START",
            "RANGE_LOCK_ASSERT",
            "BAR_REJECTION_RATE_HIGH",
            "GAP_VIOLATIONS_SUMMARY",
            "DATA_LOSS_DETECTED",
            "CONNECTION_LOST_SUSTAINED",
        }
        
        for event in all_events:
            event_type = event.get("event", "")
            level = event.get("level", "").upper()
            
            if event_type in significant_event_types or level in ["ERROR", "WARN"]:
                timeline_events.append({
                    "timestamp": event.get("ts_utc", ""),
                    "event": event_type,
                    "level": level,
                    "payload": event.get("data", {}).get("payload", {})
                })
        
        # Sort by timestamp
        timeline_events.sort(key=lambda x: x["timestamp"])
        self.timeline = timeline_events
        
        print(f"  Built timeline with {len(self.timeline)} significant events")
    
    def generate_report(self) -> str:
        """Generate comprehensive markdown report."""
        print("\nGenerating report...")
        
        report = []
        report.append(f"# Comprehensive Robot Log Analysis - {self.date_str}\n")
        report.append(f"Generated: {datetime.now(CHICAGO_TZ).strftime('%Y-%m-%d %H:%M:%S %Z')}\n")
        
        # Executive Summary
        report.append("## Executive Summary\n")
        
        # Count streams
        streams_created = 0
        if self.startup_info.get("streams_created"):
            payload = self.startup_info["streams_created"]["payload"]
            streams_created = payload.get("stream_count", 0)
        
        ranges_computed = len([j for j in self.journals.values() if j.get("RangeHigh") is not None and j.get("RangeLow") is not None])
        ranges_committed = len([j for j in self.journals.values() if j.get("Committed", False)])
        
        report.append(f"- **Trading Date**: {self.date_str}")
        report.append(f"- **Streams Created**: {streams_created}")
        report.append(f"- **Ranges Computed**: {ranges_computed}")
        report.append(f"- **Ranges Committed**: {ranges_committed}")
        report.append(f"- **Total Errors**: {len(self.errors)}")
        report.append(f"- **Total Warnings**: {len(self.warnings)}\n")
        
        # Engine Startup
        report.append("## 1. Engine Startup & Initialization\n")
        if self.startup_info.get("engine_start"):
            e = self.startup_info["engine_start"]
            report.append(f"- **Engine Start**: {e['timestamp']}")
        if self.startup_info.get("trading_date_locked"):
            e = self.startup_info["trading_date_locked"]
            payload = e["payload"]
            report.append(f"- **Trading Date Locked**: {e['timestamp']}")
            report.append(f"  - Trading Date: {payload.get('trading_date', 'N/A')}")
        if self.startup_info.get("streams_created"):
            e = self.startup_info["streams_created"]
            payload = e["payload"]
            report.append(f"- **Streams Created**: {e['timestamp']}")
            report.append(f"  - Stream Count: {payload.get('stream_count', 'N/A')}")
        report.append("")
        
        # BarsRequest Analysis
        report.append("## 2. BarsRequest Analysis\n")
        if self.barsrequest_info:
            for inst in sorted(self.barsrequest_info.keys()):
                info = self.barsrequest_info[inst]
                report.append(f"### {inst}")
                report.append(f"- Initialization Events: {len(info['initialization'])}")
                report.append(f"- Skipped Events: {len(info['skipped'])}")
                report.append(f"- Raw Result Events: {len(info['raw_result'])}")
                report.append(f"- Stream Status Events: {len(info.get('stream_status', []))}")
                report.append(f"- Range Determined Events: {len(info.get('range_determined', []))}")
                report.append(f"- Bars Loaded Events: {len(info['bars_loaded'])}")
                
                if info["raw_result"]:
                    for result in info["raw_result"]:
                        payload = result.get("payload", {})
                        bars_returned = payload.get("bars_returned_raw", "N/A")
                        first_bar = payload.get("first_bar_time", "")
                        last_bar = payload.get("last_bar_time", "")
                        report.append(f"  - Raw result at {result['timestamp']}: {bars_returned} bars")
                        if first_bar:
                            report.append(f"    First bar: {first_bar}, Last bar: {last_bar}")
                
                if info["skipped"]:
                    for skip in info["skipped"]:
                        payload = skip.get("payload", {})
                        reason = payload.get("reason", "Unknown")
                        current_time = payload.get("current_time_chicago", "")
                        range_start = payload.get("range_start_time", "")
                        report.append(f"  - Skipped at {skip['timestamp']}: {reason}")
                        if current_time and range_start:
                            report.append(f"    (Current: {current_time}, Range Start: {range_start})")
                
                if info["bars_loaded"]:
                    for loaded in info["bars_loaded"]:
                        payload = loaded.get("payload", {})
                        count = payload.get("bar_count", "N/A")
                        streams_fed = payload.get("streams_fed", "N/A")
                        report.append(f"  - Bars loaded at {loaded['timestamp']}: {count} bars, {streams_fed} streams fed")
                
                report.append("")
        else:
            report.append("**No BarsRequest events found.**\n")
        
        # Bar Acceptance Analysis
        report.append("## 3. Bar Acceptance/Rejection Analysis\n")
        if self.bar_stats:
            report.append("| Instrument | Accepted | Rejected | Acceptance Rate |")
            report.append("|------------|----------|----------|------------------|")
            for inst in sorted(self.bar_stats.keys()):
                stats = self.bar_stats[inst]
                total = stats["accepted"] + stats["rejected"]
                if total > 0:
                    rate = (stats["accepted"] / total) * 100
                    report.append(f"| {inst} | {stats['accepted']} | {stats['rejected']} | {rate:.1f}% |")
                else:
                    report.append(f"| {inst} | 0 | 0 | N/A |")
            report.append("")
            
            # Rejection reasons
            report.append("### Rejection Reasons\n")
            for inst in sorted(self.bar_stats.keys()):
                stats = self.bar_stats[inst]
                if stats["reasons"]:
                    report.append(f"**{inst}**:")
                    for reason, count in stats["reasons"].most_common():
                        report.append(f"- {reason}: {count}")
                    report.append("")
        else:
            report.append("**No bar acceptance/rejection events found.**\n")
        
        # Stream State Analysis
        report.append("## 4. Stream State Analysis\n")
        if self.stream_states:
            for stream in sorted(self.stream_states.keys()):
                transitions = self.stream_states[stream]
                report.append(f"### {stream}")
                report.append(f"- Total Transitions: {len(transitions)}")
                if transitions:
                    report.append("| Timestamp | From State | To State | Reason |")
                    report.append("|-----------|------------|----------|--------|")
                    for t in transitions:
                        report.append(f"| {t['timestamp']} | {t['from_state']} | {t['to_state']} | {t['reason']} |")
                else:
                    report.append("- **No state transitions found**")
                report.append("")
        else:
            report.append("**No state transitions found.**\n")
        
        # Range Computation
        report.append("## 5. Range Computation Analysis\n")
        # Re-analyze range computation for report
        range_events = {
            "RANGE_COMPUTE_START": [],
            "RANGE_LOCK_ASSERT": [],
            "RANGE_COMPUTE_FAILED": [],
        }
        
        all_events = self.engine_events + [e for events in self.instrument_events.values() for e in events]
        
        for event in all_events:
            event_type = event.get("event", "")
            if event_type in range_events:
                payload = event.get("data", {}).get("payload", {})
                stream = payload.get("stream", "")
                range_events[event_type].append({
                    "timestamp": event.get("ts_utc", ""),
                    "stream": stream,
                    "payload": payload
                })
        
        report.append(f"- RANGE_COMPUTE_START: {len(range_events['RANGE_COMPUTE_START'])}")
        report.append(f"- RANGE_LOCK_ASSERT: {len(range_events['RANGE_LOCK_ASSERT'])}")
        report.append(f"- RANGE_COMPUTE_FAILED: {len(range_events['RANGE_COMPUTE_FAILED'])}\n")
        
        # Journal analysis
        report.append("### Journal Files\n")
        if self.journals:
            report.append("| Stream | Instrument | Session | State | Range High | Range Low | Committed |")
            report.append("|--------|------------|---------|-------|------------|-----------|-----------|")
            for stream in sorted(self.journals.keys()):
                j = self.journals[stream]
                report.append(f"| {stream} | {j.get('Instrument', 'N/A')} | {j.get('Session', 'N/A')} | "
                            f"{j.get('LastState', 'N/A')} | {j.get('RangeHigh', 'N/A')} | "
                            f"{j.get('RangeLow', 'N/A')} | {j.get('Committed', False)} |")
            report.append("")
        else:
            report.append("**No journal files found for today.**\n")
        
        # Errors & Warnings
        report.append("## 6. Error & Warning Analysis\n")
        # Re-analyze errors/warnings for report
        error_types = Counter([e["event"] for e in self.errors])
        warn_types = Counter([w["event"] for w in self.warnings])
        
        report.append(f"### Errors ({len(self.errors)} total)\n")
        if error_types:
            report.append("| Event Type | Count |")
            report.append("|------------|-------|")
            for event_type, count in error_types.most_common(20):
                report.append(f"| {event_type} | {count} |")
            report.append("")
        else:
            report.append("**No errors found.**\n")
        
        report.append(f"### Warnings ({len(self.warnings)} total)\n")
        if warn_types:
            report.append("| Event Type | Count |")
            report.append("|------------|-------|")
            for event_type, count in warn_types.most_common(20):
                report.append(f"| {event_type} | {count} |")
            report.append("")
        else:
            report.append("**No warnings found.**\n")
        
        # Timeline
        report.append("## 7. Timeline of Significant Events\n")
        report.append("| Timestamp | Event | Level | Details |")
        report.append("|-----------|------|-------|---------|")
        for event in self.timeline[:100]:  # First 100 events
            details = ""
            payload = event.get("payload", {})
            if isinstance(payload, dict):
                # Extract key details
                stream = payload.get("stream", "")
                instrument = payload.get("instrument", "")
                reason = payload.get("reason", "")
                if stream:
                    details = f"stream={stream}"
                elif instrument:
                    details = f"instrument={instrument}"
                elif reason:
                    details = f"reason={reason}"
            
            level_marker = event.get("level", "")
            report.append(f"| {event['timestamp']} | {event['event']} | {level_marker} | {details} |")
        
        if len(self.timeline) > 100:
            report.append(f"\n*Showing first 100 of {len(self.timeline)} timeline events.*\n")
        
        # Root Cause Analysis
        report.append("## 8. Root Cause Analysis\n")
        
        # Check for BarsRequest issues
        instruments_with_no_barsrequest = []
        instruments_with_skipped_barsrequest = []
        
        # Get all instruments from streams
        all_instruments = set()
        if self.startup_info.get("streams_created"):
            payload = self.startup_info["streams_created"]["payload"]
            streams = payload.get("streams", [])
            for stream_info in streams:
                inst = stream_info.get("instrument", "")
                if inst:
                    all_instruments.add(inst)
        
        for inst in all_instruments:
            if inst not in self.barsrequest_info:
                instruments_with_no_barsrequest.append(inst)
            elif self.barsrequest_info[inst]["skipped"]:
                instruments_with_skipped_barsrequest.append(inst)
        
        if instruments_with_no_barsrequest:
            report.append(f"### BarsRequest Never Executed\n")
            report.append(f"Instruments with NO BarsRequest events: {', '.join(instruments_with_no_barsrequest)}\n")
        
        if instruments_with_skipped_barsrequest:
            report.append(f"### BarsRequest Skipped\n")
            report.append(f"Instruments with skipped BarsRequest: {', '.join(instruments_with_skipped_barsrequest)}\n")
        
        # Check bar rejection rates
        high_rejection_instruments = []
        for inst, stats in self.bar_stats.items():
            total = stats["accepted"] + stats["rejected"]
            if total > 0:
                rejection_rate = (stats["rejected"] / total) * 100
                if rejection_rate > 90:
                    high_rejection_instruments.append((inst, rejection_rate))
        
        if high_rejection_instruments:
            report.append(f"### High Bar Rejection Rate\n")
            for inst, rate in high_rejection_instruments:
                report.append(f"- **{inst}**: {rate:.1f}% rejection rate")
            report.append("")
        
        # Check streams stuck in PRE_HYDRATION
        streams_stuck = []
        for stream, transitions in self.stream_states.items():
            if not transitions:
                streams_stuck.append((stream, "No transitions"))
            else:
                last_state = transitions[-1]["to_state"]
                if last_state == "PRE_HYDRATION":
                    streams_stuck.append((stream, "Stuck in PRE_HYDRATION"))
        
        if streams_stuck:
            report.append(f"### Streams Stuck in PRE_HYDRATION\n")
            for stream, reason in streams_stuck:
                report.append(f"- **{stream}**: {reason}")
            report.append("")
        
        # Stream-by-Stream Summary
        report.append("## 9. Stream-by-Stream Summary\n")
        
        # Get stream info from journals and events
        stream_summary = {}
        for stream, journal in self.journals.items():
            stream_summary[stream] = {
                "journal": journal,
                "barsrequest": None,
                "bar_stats": None,
                "transitions": self.stream_states.get(stream, []),
            }
        
        # Add BarsRequest info
        for inst, info in self.barsrequest_info.items():
            # Try to match to streams
            for stream in stream_summary.keys():
                if inst in stream:
                    stream_summary[stream]["barsrequest"] = info
        
        # Add bar stats
        for inst, stats in self.bar_stats.items():
            for stream in stream_summary.keys():
                if inst in stream:
                    stream_summary[stream]["bar_stats"] = stats
        
        if stream_summary:
            for stream in sorted(stream_summary.keys()):
                info = stream_summary[stream]
                journal = info["journal"]
                report.append(f"### {stream}")
                report.append(f"- **Instrument**: {journal.get('Instrument', 'N/A')}")
                report.append(f"- **Session**: {journal.get('Session', 'N/A')}")
                report.append(f"- **State**: {journal.get('LastState', 'N/A')}")
                report.append(f"- **Range**: {journal.get('RangeLow', 'N/A')} - {journal.get('RangeHigh', 'N/A')}")
                report.append(f"- **Committed**: {journal.get('Committed', False)}")
                
                if info["barsrequest"]:
                    br_info = info["barsrequest"]
                    report.append(f"- **BarsRequest**: {len(br_info['initialization'])} init, {len(br_info['skipped'])} skipped")
                
                if info["bar_stats"]:
                    stats = info["bar_stats"]
                    total = stats["accepted"] + stats["rejected"]
                    report.append(f"- **Bars**: {stats['accepted']} accepted, {stats['rejected']} rejected")
                
                report.append(f"- **State Transitions**: {len(info['transitions'])}")
                report.append("")
        else:
            report.append("**No stream information available.**\n")
        
        # Recommendations
        report.append("## 10. Recommendations\n")
        
        recommendations = []
        
        if instruments_with_no_barsrequest:
            recommendations.append(f"1. **Investigate missing BarsRequest**: Instruments {', '.join(instruments_with_no_barsrequest)} had no BarsRequest events. Check strategy initialization code.")
        
        if instruments_with_skipped_barsrequest:
            recommendations.append(f"2. **Review BarsRequest skip logic**: Instruments {', '.join(instruments_with_skipped_barsrequest)} had BarsRequest skipped. Review timing logic in RobotSimStrategy.cs.")
        
        if high_rejection_instruments:
            recommendations.append(f"3. **Investigate bar rejection**: High rejection rates detected. Review bar age checks and date matching logic.")
        
        if streams_stuck:
            recommendations.append(f"4. **Fix state transition logic**: Streams stuck in PRE_HYDRATION. Review transition conditions in StreamStateMachine.cs.")
        
        if ranges_computed == 0:
            recommendations.append("5. **No ranges computed**: System-wide failure. Review all root causes above.")
        
        if recommendations:
            for rec in recommendations:
                report.append(rec)
        else:
            report.append("No specific recommendations - system appears to be functioning normally.")
        
        report.append("")
        
        return "\n".join(report)


def main():
    """Main entry point."""
    print("=" * 80)
    print(f"COMPREHENSIVE ROBOT LOG ANALYSIS - {today_str}")
    print("=" * 80)
    
    analyzer = LogAnalyzer(today_str)
    
    # Load logs
    analyzer.load_logs()
    
    # Run analyses
    analyzer.analyze_engine_startup()
    analyzer.analyze_barsrequest()
    analyzer.analyze_bar_acceptance()
    analyzer.analyze_stream_states()
    analyzer.analyze_range_computation()
    analyzer.analyze_errors_warnings()
    analyzer.build_timeline()
    
    # Generate report
    report = analyzer.generate_report()
    
    # Save report
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    output_file = OUTPUT_DIR / f"TODAY_COMPREHENSIVE_ANALYSIS_{today_str}.md"
    
    with open(output_file, 'w', encoding='utf-8') as f:
        f.write(report)
    
    print(f"\n{'='*80}")
    print(f"Analysis complete! Report saved to: {output_file}")
    print(f"{'='*80}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"Error during analysis: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
        sys.exit(1)
