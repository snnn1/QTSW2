#!/usr/bin/env python3
"""Generate synthetic replay JSONL for determinism testing."""
import json
import sys

def event(seq_base, intent_id, fill_price, tick_price, tick_offset_ms):
    base = "2025-01-15T15:30:00"
    return [
        {
            "source": "MNQ",
            "sequence": seq_base,
            "executionInstrumentKey": "MNQ",
            "type": "IntentRegistered",
            "payload": {
                "intentId": intent_id,
                "intent": {
                    "tradingDate": "2025-01-15",
                    "stream": "NG1",
                    "instrument": "MNQ",
                    "executionInstrument": "MNQ",
                    "session": "RTH",
                    "slotTimeChicago": "09:30",
                    "direction": "Long",
                    "entryPrice": fill_price,
                    "stopPrice": fill_price - 20,
                    "targetPrice": fill_price + 20,
                    "beTrigger": fill_price + 5,
                    "entryTimeUtc": f"{base}Z",
                    "triggerReason": "RANGE"
                }
            }
        },
        {
            "source": "MNQ",
            "sequence": seq_base + 1,
            "executionInstrumentKey": "MNQ",
            "type": "ExecutionUpdate",
            "payload": {
                "executionId": f"exec_{intent_id}",
                "orderId": f"ord_{intent_id}",
                "fillPrice": fill_price,
                "fillQuantity": 1,
                "marketPosition": "Long",
                "executionTime": f"{base}Z",
                "tag": intent_id,
                "intentId": intent_id,
                "executionInstrumentKey": "MNQ"
            }
        },
        {
            "source": "MNQ",
            "sequence": seq_base + 2,
            "executionInstrumentKey": "MNQ",
            "type": "Tick",
            "payload": {
                "tickPrice": tick_price,
                "tickTimeFromEvent": f"{base}.{tick_offset_ms:03d}Z",
                "executionInstrument": "MNQ"
            }
        }
    ]

def main():
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 100
    out = sys.argv[2] if len(sys.argv) > 2 else None
    events = []
    for i in range(n):
        intent_id = f"intent_{i:04d}"
        fill_price = 21500.0 + i * 0.25
        tick_price = fill_price + 2.0
        tick_offset_ms = (i * 3) * 1000
        events.extend(event(i * 3, intent_id, fill_price, tick_price, tick_offset_ms))
    # Fix sequences: must be 0,1,2,3,... per source
    for i, e in enumerate(events):
        e["sequence"] = i
    lines = [json.dumps(e) for e in events]
    text = "\n".join(lines)
    if out:
        with open(out, "w") as f:
            f.write(text)
        print(f"Wrote {len(events)} events to {out}")
    else:
        print(text)

if __name__ == "__main__":
    main()
