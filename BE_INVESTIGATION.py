#!/usr/bin/env python3
"""
Break-Even Detection - Full Investigation & Diagnosis
Traces entry fills, BE triggers, price movement, and identifies root cause.
"""
import json
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

def parse_ts(s):
    if not s: return None
    try:
        s = s.replace('Z', '+00:00')
        if '+' not in s: s += '+00:00'
        dt = datetime.fromisoformat(s)
        return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt
    except: return None

def load_events(log_dir, cutoff, patterns=None):
    events = []
    for f in sorted(log_dir.glob("robot_*.jsonl")):
        if f.name.count('_') > 2: continue  # Skip dated archives
        try:
            for line in open(f, 'r', encoding='utf-8'):
                line = line.strip()
                if not line: continue
                try:
                    e = json.loads(line)
                    ts = parse_ts(e.get('ts_utc', ''))
                    if ts and ts >= cutoff:
                        e['_source'] = f.name
                        if patterns is None or any(p in e.get('event','') for p in patterns):
                            events.append(e)
                except: pass
        except: pass
    events.sort(key=lambda x: parse_ts(x.get('ts_utc','')) or datetime.min.replace(tzinfo=timezone.utc))
    return events

def main():
    log_dir = Path("logs/robot")
    cutoff = datetime.now(timezone.utc) - timedelta(hours=48)
    
    print("="*90)
    print("BREAK-EVEN DETECTION - FULL INVESTIGATION")
    print("="*90)
    print(f"Cutoff: {cutoff.strftime('%Y-%m-%d %H:%M:%S')} UTC\n")
    
    # 1. Entry fills with full data
    all_events = load_events(log_dir, cutoff)
    fills = [e for e in all_events if e.get('event') == 'EXECUTION_FILLED']
    # Filter to entry fills (has order_type or fill_type indicating entry)
    entry_fills = []
    for e in fills:
        d = e.get('data', {})
        order_type = d.get('order_type', '') or d.get('fill_type', '')
        if 'ENTRY' in order_type.upper() or 'entry' in str(d.get('note','')).lower():
            entry_fills.append(e)
        elif not order_type and d.get('intent_id'):  # Assume entry if has intent
            entry_fills.append(e)
    
    if not entry_fills:
        entry_fills = fills  # Use all fills if no filter match
    
    print("1. ENTRY FILLS (full detail)")
    print("-"*90)
    for e in entry_fills[-15:]:
        ts = parse_ts(e.get('ts_utc',''))
        d = e.get('data', {})
        print(f"  {ts.strftime('%H:%M:%S') if ts else 'N/A'} UTC | {e.get('instrument','')} | {d.get('stream','')} | "
              f"Intent: {str(d.get('intent_id',''))[:12]}... | "
              f"Price: {d.get('fill_price', d.get('actual_fill_price', 'N/A'))} | "
              f"Dir: {d.get('direction','')} | Qty: {d.get('quantity','')}")
        print(f"      be_trigger: {d.get('be_trigger')} | entry_price: {d.get('entry_price')} | stop: {d.get('stop_price')}")
    
    # 2. INTENT_REGISTERED with be_trigger
    intents = [e for e in all_events if e.get('event') == 'INTENT_REGISTERED']
    print("\n2. INTENT_REGISTERED (BE trigger set?)")
    print("-"*90)
    intent_be = [e for e in intents if e.get('data',{}).get('be_trigger') is not None or e.get('data',{}).get('has_be_trigger')]
    intent_no_be = [e for e in intents if e.get('data',{}).get('be_trigger') is None and e.get('data',{}).get('has_be_trigger') == False]
    print(f"  With be_trigger: {len(intent_be)} | Without: {len(intent_no_be)}")
    for e in intents[-10:]:
        d = e.get('data', {})
        ts = parse_ts(e.get('ts_utc',''))
        be = d.get('be_trigger', d.get('be_trigger_price'))
        has_be = d.get('has_be_trigger')
        print(f"  {ts.strftime('%H:%M') if ts else 'N/A'} | {e.get('instrument','')} | intent_id: {str(d.get('intent_id',''))[:8]}... | "
              f"be_trigger={be} | has_be_trigger={has_be}")
    
    # 3. PROTECTIVE_ORDERS_SUBMITTED - has BE trigger in protective compute
    protective = [e for e in all_events if e.get('event') == 'PROTECTIVE_ORDERS_SUBMITTED']
    print("\n3. PROTECTIVE_ORDERS_SUBMITTED (BE trigger in protective compute)")
    print("-"*90)
    for e in protective[-8:]:
        d = e.get('data', {})
        ts = parse_ts(e.get('ts_utc',''))
        payload = d.get('payload') or d.get('data') or {}
        if isinstance(payload, str):
            import re
            be_match = re.search(r'be_trigger[^=]*=\s*([0-9.]+)', payload)
            be = be_match.group(1) if be_match else 'N/A'
        else:
            be = payload.get('be_trigger', payload.get('long_be_trigger', 'N/A'))
        print(f"  {ts.strftime('%H:%M') if ts else 'N/A'} | {e.get('instrument','')} | be_trigger in payload: {str(payload)[:120]}...")
    
    # 4. PROTECTIVE_ORDERS_PARTIAL_COMPUTE - BE trigger set when range not available
    partial = [e for e in all_events if 'PROTECTIVE_ORDERS_PARTIAL' in e.get('event','') or 'PARTIAL_COMPUTE' in e.get('event','')]
    print("\n4. PROTECTIVE_ORDERS_PARTIAL_COMPUTE (range unavailable - BE only)")
    print("-"*90)
    for e in partial[-5:]:
        d = e.get('data', {})
        print(f"  {d.get('be_trigger_price')} | {d.get('note','')[:80]}")
    
    # 5. Build fill -> intent -> BE trigger mapping
    print("\n5. FILL-TO-BE-TRIGGER MAPPING")
    print("-"*90)
    intent_by_id = {}
    for e in intents:
        iid = (e.get('data') or {}).get('intent_id')
        if iid:
            intent_by_id[iid] = e
    
    for e in entry_fills[-10:]:
        d = e.get('data', {})
        iid = d.get('intent_id')
        fill_price = float(d.get('fill_price') or d.get('actual_fill_price') or 0)
        direction = d.get('direction', '')
        stream = d.get('stream', '')
        instrument = e.get('instrument', '')
        
        be_trigger = d.get('be_trigger')
        if be_trigger is None and iid and iid in intent_by_id:
            be_trigger = (intent_by_id[iid].get('data') or {}).get('be_trigger')
        
        if be_trigger is not None:
            be_trigger = float(be_trigger)
            reached = (direction == 'Long' and fill_price >= be_trigger) or (direction == 'Short' and fill_price <= be_trigger)
            print(f"  {stream} {instrument} | Fill: {fill_price} | Dir: {direction} | BE trigger: {be_trigger} | "
                  f"Would trigger: {reached}")
        else:
            print(f"  {stream} {instrument} | Fill: {fill_price} | Dir: {direction} | BE trigger: NOT SET")
    
    # 6. Price data - BAR_ACCEPTED, ONBARUPDATE - get high/low after fill
    print("\n6. PRICE MOVEMENT AFTER FILL (bar high/low)")
    print("-"*90)
    # Group bars by instrument
    bars = [e for e in all_events if e.get('event') in ('BAR_ACCEPTED','ONBARUPDATE_CALLED')]
    # Get execution fills with timestamps
    for ef in entry_fills[-5:]:
        fill_ts = parse_ts(ef.get('ts_utc',''))
        inst = ef.get('instrument','') or (ef.get('data') or {}).get('instrument','')
        d = ef.get('data', {})
        fill_price = float(d.get('fill_price') or d.get('actual_fill_price') or 0)
        direction = d.get('direction', '')
        be_trigger = d.get('be_trigger')
        if be_trigger is None and d.get('intent_id') in intent_by_id:
            be_trigger = (intent_by_id.get(d.get('intent_id'), {}).get('data') or {}).get('be_trigger')
        if be_trigger is None:
            continue
        be_trigger = float(be_trigger)
        
        # Find bars for this instrument after fill
        inst_bars = [e for e in bars if (e.get('instrument') or (e.get('data') or {}).get('instrument')) == inst 
                    and parse_ts(e.get('ts_utc','')) and parse_ts(e.get('ts_utc','')) >= fill_ts][:20]
        
        if direction == 'Long':
            # Need high >= be_trigger
            max_high = 0
            for b in inst_bars:
                bd = (b.get('data') or {})
                h = bd.get('high') or bd.get('bar_high')
                if h is not None:
                    max_high = max(max_high, float(h))
            print(f"  {inst} {stream} LONG | Fill: {fill_price} | BE: {be_trigger} | Max bar high after fill: {max_high if max_high else 'N/A'} | "
                  f"Reached: {max_high >= be_trigger if max_high else 'no bar data'}")
        else:
            min_low = 999999
            for b in inst_bars:
                bd = (b.get('data') or {})
                lo = bd.get('low') or bd.get('bar_low')
                if lo is not None:
                    min_low = min(min_low, float(lo))
            print(f"  {inst} {stream} SHORT | Fill: {fill_price} | BE: {be_trigger} | Min bar low after fill: {min_low if min_low < 999999 else 'N/A'} | "
                  f"Reached: {min_low <= be_trigger if min_low < 999999 else 'no bar data'}")
    
    # 7. Check execution journal for intents
    print("\n7. EXECUTION JOURNAL (active intents)")
    print("-"*90)
    journal_dir = Path("logs/robot/journal")
    for jf in sorted(journal_dir.glob("2026-02-13_*.json"))[:5]:
        try:
            j = json.load(open(jf))
            if j.get('EntryDetected') or j.get('OriginalIntentId'):
                print(f"  {jf.stem}: LastState={j.get('LastState')} EntryDetected={j.get('EntryDetected')} "
                      f"ProtectionSubmitted={j.get('ProtectionSubmitted')} Intent={str(j.get('OriginalIntentId',''))[:12]}...")
        except: pass
    
    # 8. GetActiveIntentsForBEMonitoring - what does it require?
    print("\n8. BE MONITORING REQUIREMENTS (from code)")
    print("-"*90)
    print("  - Intent must have: BeTrigger, EntryPrice, Direction")
    print("  - Intent must be in execution journal with EntryFilled=true")
    print("  - CheckBreakEvenTriggersTickBased uses OnMarketData(tickPrice) - TICK price for strategy's Instrument")
    print("  - Each strategy instance = ONE instrument. ES1 chart gets ES ticks only.")
    print("  - Long: trigger when tickPrice >= beTriggerPrice")
    print("  - Short: trigger when tickPrice <= beTriggerPrice")
    
    # 9. Root cause diagnosis
    print("\n9. DIAGNOSIS")
    print("-"*90)
    if not entry_fills:
        print("  [INFO] No entry fills - nothing to diagnose")
    elif not intent_be and intents:
        print("  [ISSUE] Intents registered but be_trigger is NULL or not logged")
        print("  -> Check StreamStateMachine: Is BeTrigger set when creating intent?")
        print("  -> Check INTENT_REGISTERED event: Does it include be_trigger in data?")
    elif intent_be and not any(e.get('event') == 'BE_TRIGGER_REACHED' for e in all_events):
        # Check if price reached
        print("  [INVESTIGATING] Intents have be_trigger. Checking if price reached...")
        for ef in entry_fills[-3:]:
            d = ef.get('data', {})
            be = d.get('be_trigger')
            if be is None: be = (intent_by_id.get(d.get('intent_id')) or {}).get('data',{}).get('be_trigger')
            if be:
                print(f"    Fill {d.get('stream')} @ {d.get('fill_price')} Dir={d.get('direction')} BE={be}")
        print("  -> If price HAS reached BE: Check OnMarketData - is it being called?")
        print("  -> Check GetActiveIntentsForBEMonitoring - does it return these intents?")
        print("  -> Check execution journal - is EntryFilled true for these intents?")
    else:
        print("  [OK] BE triggers detected - system working")
    
    print("\n" + "="*90)

if __name__ == "__main__":
    main()
