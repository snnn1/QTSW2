#!/usr/bin/env python3
"""Check execution journals for today's trades"""
import json
import glob
from pathlib import Path
from datetime import datetime

print("=" * 100)
print("EXECUTION JOURNALS ANALYSIS - February 2, 2026")
print("=" * 100)
print()

journal_dir = Path("data/execution_journals")
today_journals = list(journal_dir.glob("2026-02-02_*.json"))

print(f"Found {len(today_journals)} execution journals for today")
print()

# Group by stream
by_stream = {}
for journal_file in sorted(today_journals):
    parts = journal_file.stem.split('_')
    if len(parts) >= 3:
        stream = parts[1]
        intent_id = parts[2]
        
        with open(journal_file, 'r', encoding='utf-8') as f:
            data = json.load(f)
        
        if stream not in by_stream:
            by_stream[stream] = []
        
        by_stream[stream].append({
            'intent_id': intent_id,
            'file': journal_file.name,
            'data': data
        })

# Display details for each stream
for stream in sorted(by_stream.keys()):
    journals = by_stream[stream]
    print(f"\n{'='*100}")
    print(f"STREAM: {stream} ({len(journals)} intent(s))")
    print(f"{'='*100}")
    
    for journal in journals:
        data = journal['data']
        intent_id = journal['intent_id']
        
        print(f"\nIntent ID: {intent_id}")
        print(f"  Instrument: {data.get('Instrument', 'N/A')}")
        print(f"  Direction: {data.get('Direction', 'N/A')}")
        
        # Entry details
        if data.get('EntrySubmitted'):
            print(f"  Entry Submitted: [OK] {data.get('EntrySubmittedAt', 'N/A')}")
            print(f"    Broker Order ID: {data.get('BrokerOrderId', 'N/A')}")
            print(f"    Order Type: {data.get('EntryOrderType', 'N/A')}")
        
        if data.get('EntryFilled') or data.get('EntryFilledQuantityTotal', 0) > 0:
            print(f"  Entry Filled: [OK]")
            print(f"    Filled At: {data.get('EntryFilledAtUtc') or data.get('EntryFilledAt', 'N/A')}")
            print(f"    Quantity: {data.get('EntryFilledQuantityTotal') or data.get('FillQuantity', 'N/A')}")
            print(f"    Avg Fill Price: {data.get('EntryAvgFillPrice') or data.get('FillPrice', 'N/A')}")
            if data.get('ExpectedEntryPrice'):
                print(f"    Expected Price: {data.get('ExpectedEntryPrice')}")
                if data.get('SlippagePoints'):
                    print(f"    Slippage: {data.get('SlippagePoints')} points ({data.get('SlippageDollars', 0):.2f} $)")
        
        # Exit details
        if data.get('ExitFilledQuantityTotal', 0) > 0:
            print(f"  Exit Filled: [OK]")
            print(f"    Filled At: {data.get('ExitFilledAtUtc', 'N/A')}")
            print(f"    Quantity: {data.get('ExitFilledQuantityTotal')}")
            print(f"    Avg Fill Price: {data.get('ExitAvgFillPrice', 'N/A')}")
            print(f"    Exit Order Type: {data.get('ExitOrderType', 'N/A')}")
        
        # Trade completion
        if data.get('TradeCompleted'):
            print(f"  Trade Completed: [OK]")
            print(f"    Completed At: {data.get('CompletedAtUtc', 'N/A')}")
            print(f"    Completion Reason: {data.get('CompletionReason', 'N/A')}")
            if data.get('RealizedPnLPoints') is not None:
                print(f"    Realized P&L: {data.get('RealizedPnLPoints')} points")
                print(f"    Gross P&L: ${data.get('RealizedPnLGross', 0):.2f}")
                print(f"    Net P&L: ${data.get('RealizedPnLNet', 0):.2f}")
        
        # Rejection
        if data.get('Rejected'):
            print(f"  Rejected: [FAIL] {data.get('RejectedAt', 'N/A')}")
            print(f"    Reason: {data.get('RejectionReason', 'N/A')}")
        
        # BE modification
        if data.get('BEModified'):
            print(f"  BE Modified: [OK] {data.get('BEModifiedAt', 'N/A')}")
            print(f"    BE Stop Price: {data.get('BEStopPrice', 'N/A')}")

print()
print("=" * 100)
print("SUMMARY")
print("=" * 100)

completed_trades = []
for stream, journals in by_stream.items():
    for journal in journals:
        if journal['data'].get('TradeCompleted'):
            completed_trades.append({
                'stream': stream,
                'intent_id': journal['intent_id'],
                'pnl': journal['data'].get('RealizedPnLNet', 0)
            })

if completed_trades:
    print(f"\nCompleted Trades: {len(completed_trades)}")
    total_pnl = sum(t['pnl'] for t in completed_trades)
    print(f"Total Net P&L: ${total_pnl:.2f}")
    for trade in completed_trades:
        print(f"  {trade['stream']} ({trade['intent_id'][:8]}): ${trade['pnl']:.2f}")
else:
    print("\nNo completed trades yet (entries may still be open)")

print()
print("=" * 100)
