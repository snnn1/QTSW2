"""Check watchdog API for ES1/NQ1 status"""
import requests
import json

def main():
    try:
        r = requests.get('http://localhost:8001/api/watchdog/status', timeout=5)
        data = r.json()
        
        print("="*80)
        print("WATCHDOG STATUS - ES1 AND NQ1")
        print("="*80)
        
        streams = {s['stream']: s for s in data.get('streams', [])}
        
        for stream_id in ['ES1', 'NQ1']:
            if stream_id in streams:
                s = streams[stream_id]
                print(f"\n[{stream_id}]")
                print(f"  State: {s.get('state', 'N/A')}")
                print(f"  Committed: {s.get('committed', 'N/A')}")
                print(f"  Instrument: {s.get('instrument', 'N/A')}")
                print(f"  Session: {s.get('session', 'N/A')}")
                print(f"  Slot Time: {s.get('slot_time_chicago', 'N/A')}")
                print(f"  Range Start: {s.get('range_start_chicago', 'N/A')}")
                print(f"  Range End: {s.get('range_end_chicago', 'N/A')}")
                print(f"  Range High: {s.get('range_high', 'N/A')}")
                print(f"  Range Low: {s.get('range_low', 'N/A')}")
                print(f"  Last Bar: {s.get('last_bar_chicago', 'N/A')}")
                print(f"  Entry Detected: {s.get('entry_detected', 'N/A')}")
                print(f"  Breakout Long: {s.get('breakout_long', 'N/A')}")
                print(f"  Breakout Short: {s.get('breakout_short', 'N/A')}")
            else:
                print(f"\n[{stream_id}]: Not found in watchdog")
        
        # Check risk gates
        print(f"\n[RISK GATES]")
        gates = data.get('risk_gates', {})
        print(f"  Stream Armed: {gates.get('stream_armed', {})}")
        print(f"  Kill Switch: {gates.get('kill_switch', {})}")
        print(f"  Recovery State: {gates.get('recovery_state', 'N/A')}")
        
    except Exception as e:
        print(f"Error: {e}")

if __name__ == '__main__':
    main()
