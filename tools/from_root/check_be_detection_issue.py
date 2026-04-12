#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Check why break-even detection isn't working"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

recent = datetime.now(timezone.utc) - timedelta(hours=4)

print("=" * 100)
print("BREAK-EVEN DETECTION DIAGNOSIS")
print("=" * 100)
print()

# Track events
intent_registered = []
entry_fills = []
be_monitoring_events = []
be_trigger_reached = []
be_trigger_failed = []
be_modify_attempts = []
be_modify_success = []
be_modify_failed = []
on_market_data_calls = []
tick_events = []

for log_file in Path("logs/robot").glob("*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                try:
                    e = json.loads(line.strip())
                    ts_str = e.get('ts_utc', '')
                    if ts_str:
                        ts = datetime.fromisoformat(ts_str.replace('Z', '+00:00'))
                        if ts >= recent:
                            event_type = e.get('event', '')
                            data = e.get('data', {}) or {}
                            
                            if 'INTENT_REGISTERED' in event_type.upper():
                                intent_registered.append(e)
                            
                            if 'EXECUTION_FILLED' in event_type.upper() and 'ENTRY' in str(data.get('order_type', '')).upper():
                                entry_fills.append(e)
                            
                            if 'BE_TRIGGER' in event_type.upper():
                                if 'REACHED' in event_type.upper():
                                    be_trigger_reached.append(e)
                                elif 'FAILED' in event_type.upper() or 'RETRY' in event_type.upper():
                                    be_trigger_failed.append(e)
                            
                            if 'STOP_MODIFY' in event_type.upper():
                                be_modify_attempts.append(e)
                                if 'SUCCESS' in event_type.upper():
                                    be_modify_success.append(e)
                                elif 'FAIL' in event_type.upper():
                                    be_modify_failed.append(e)
                            
                            if 'ONMARKETDATA' in event_type.upper() or 'MARKET_DATA' in event_type.upper():
                                on_market_data_calls.append(e)
                            
                            if 'TICK' in event_type.upper() or 'ENGINE_TICK' in event_type.upper():
                                tick_events.append(e)
                except:
                    pass
    except:
        pass

print("1. INTENT REGISTRATION")
print("-" * 100)
print(f"Intent registrations: {len(intent_registered)}")

if intent_registered:
    for reg in intent_registered[-5:]:
        data = reg.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        be_trigger = data.get('be_trigger')
        has_be_trigger = data.get('has_be_trigger', False)
        print(f"\n  [{reg.get('ts_utc', '')[:19]}] Intent: {intent_id}...")
        print(f"    BE Trigger: {be_trigger}")
        print(f"    Has BE Trigger: {has_be_trigger}")
        
        if be_trigger is None and not has_be_trigger:
            print(f"    [CRITICAL] BE trigger not set!")
else:
    print("  [WARNING] No intent registrations found")

print()

print("2. ENTRY FILLS")
print("-" * 100)
print(f"Entry fills: {len(entry_fills)}")

if entry_fills:
    for fill in entry_fills[-5:]:
        data = fill.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        fill_price = data.get('fill_price') or data.get('price', 'N/A')
        print(f"\n  [{fill.get('ts_utc', '')[:19]}] Intent: {intent_id}...")
        print(f"    Fill Price: {fill_price}")
else:
    print("  [WARNING] No entry fills found")

print()

print("3. BE TRIGGER EVENTS")
print("-" * 100)
print(f"BE Trigger Reached: {len(be_trigger_reached)}")
print(f"BE Trigger Failed/Retry: {len(be_trigger_failed)}")

if be_trigger_reached:
    print("\n  Recent BE Trigger Reached events:")
    for e in be_trigger_reached[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        be_trigger_price = data.get('be_trigger_price', 'N/A')
        be_stop_price = data.get('be_stop_price', 'N/A')
        tick_price = data.get('tick_price', 'N/A')
        print(f"    [{ts}] Intent: {intent_id}...")
        print(f"      BE Trigger Price: {be_trigger_price}")
        print(f"      BE Stop Price: {be_stop_price}")
        print(f"      Tick Price: {tick_price}")

if be_trigger_failed:
    print("\n  Recent BE Trigger Failed/Retry events:")
    for e in be_trigger_failed[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        error = data.get('error', 'N/A')
        is_retryable = data.get('is_retryable', False)
        print(f"    [{ts}] Intent: {intent_id}...")
        print(f"      Error: {error[:100]}")
        print(f"      Retryable: {is_retryable}")

if len(be_trigger_reached) == 0 and len(be_trigger_failed) == 0:
    print("  [CRITICAL] No BE trigger events found - detection may not be running")

print()

print("4. STOP MODIFICATION ATTEMPTS")
print("-" * 100)
print(f"Modify Attempts: {len(be_modify_attempts)}")
print(f"Modify Success: {len(be_modify_success)}")
print(f"Modify Failed: {len(be_modify_failed)}")

if be_modify_attempts:
    print("\n  Recent modification attempts:")
    for e in be_modify_attempts[-5:]:
        ts = e.get('ts_utc', '')[:19]
        data = e.get('data', {})
        intent_id = data.get('intent_id', 'N/A')[:30]
        be_stop_price = data.get('be_stop_price', 'N/A')
        event_type = e.get('event', '')
        print(f"    [{ts}] {event_type}")
        print(f"      Intent: {intent_id}...")
        print(f"      BE Stop Price: {be_stop_price}")

print()

print("5. MARKET DATA / TICK PROCESSING")
print("-" * 100)
print(f"OnMarketData calls: {len(on_market_data_calls)}")
print(f"Tick events: {len(tick_events)}")

if len(on_market_data_calls) == 0 and len(tick_events) == 0:
    print("  [CRITICAL] No market data/tick events found")
    print("  This suggests OnMarketData() may not be called or tick processing is disabled")

print()

print("6. CHECKING FOR ACTIVE INTENTS")
print("-" * 100)

# Check if we have intents that should be monitored
active_intents_should_exist = []
for fill in entry_fills:
    data = fill.get('data', {})
    intent_id = data.get('intent_id', '')
    if intent_id and intent_id != 'UNKNOWN':
        # Check if intent was registered with BE trigger
        for reg in intent_registered:
            reg_data = reg.get('data', {})
            if reg_data.get('intent_id') == intent_id:
                be_trigger = reg_data.get('be_trigger')
                if be_trigger:
                    active_intents_should_exist.append({
                        'intent_id': intent_id,
                        'fill_time': fill.get('ts_utc', ''),
                        'be_trigger': be_trigger
                    })
                break

print(f"Intents that should be monitored: {len(active_intents_should_exist)}")

if active_intents_should_exist:
    print("\n  Intents that should be monitored:")
    for intent_info in active_intents_should_exist[:5]:
        print(f"    Intent: {intent_info['intent_id'][:30]}...")
        print(f"      Fill Time: {intent_info['fill_time'][:19]}")
        print(f"      BE Trigger: {intent_info['be_trigger']}")
        
        # Check if BE was triggered
        triggered = False
        for be_event in be_trigger_reached + be_trigger_failed:
            be_data = be_event.get('data', {})
            if be_data.get('intent_id') == intent_info['intent_id']:
                triggered = True
                break
        
        if not triggered:
            print(f"      [WARNING] BE trigger not reached yet (or detection not working)")

print()

print("=" * 100)
print("DIAGNOSIS SUMMARY")
print("=" * 100)
print()

issues = []

if len(intent_registered) == 0:
    issues.append("[CRITICAL] No intents registered - BE detection can't work")
elif any(not (reg.get('data', {}).get('be_trigger') or reg.get('data', {}).get('has_be_trigger')) for reg in intent_registered):
    issues.append("[CRITICAL] Some intents registered without BE trigger")

if len(entry_fills) == 0:
    issues.append("[WARNING] No entry fills found - no positions to monitor")
elif len(be_trigger_reached) == 0 and len(be_trigger_failed) == 0:
    issues.append("[CRITICAL] Entry fills exist but no BE trigger events - detection not working")

if len(on_market_data_calls) == 0 and len(tick_events) == 0:
    issues.append("[CRITICAL] No market data/tick events - OnMarketData() may not be called")

if len(be_modify_attempts) == 0:
    issues.append("[CRITICAL] No stop modification attempts - BE trigger may not be reached or ModifyStopToBreakEven not called")

if issues:
    print("ISSUES FOUND:")
    for issue in issues:
        print(f"  {issue}")
else:
    print("[OK] No obvious issues found - check individual events above")

print()
