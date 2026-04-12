#!/usr/bin/env python3
"""
Check watchdog's data stall detection - what is it reporting and why?
"""
import requests
import json

def main():
    print("="*80)
    print("WATCHDOG DATA STALL STATUS")
    print("="*80)
    
    try:
        # Get watchdog status
        response = requests.get("http://localhost:8002/api/watchdog/status", timeout=5)
        if response.status_code == 200:
            status = response.json()
            
            print("\n1. DATA STALL DETECTION:")
            print("-" * 80)
            
            data_stall_detected = status.get('data_stall_detected', {})
            bars_expected_count = status.get('bars_expected_count', 0)
            worst_last_bar_age = status.get('worst_last_bar_age_seconds')
            market_open = status.get('market_open', False)
            
            print(f"  Market Open: {market_open}")
            print(f"  Bars Expected Count: {bars_expected_count}")
            print(f"  Worst Last Bar Age: {worst_last_bar_age}s" if worst_last_bar_age else "  Worst Last Bar Age: N/A")
            print(f"  Data Stall Detected: {len(data_stall_detected)} instrument(s)")
            
            if data_stall_detected:
                print("\n  Instruments with Data Stalls:")
                for instrument, info in data_stall_detected.items():
                    stall_detected = info.get('stall_detected', False)
                    last_bar = info.get('last_bar_chicago', 'N/A')
                    market_open_inst = info.get('market_open', False)
                    print(f"    {instrument}:")
                    print(f"      Stall Detected: {stall_detected}")
                    print(f"      Last Bar: {last_bar}")
                    print(f"      Market Open: {market_open_inst}")
            else:
                print("\n  [OK] No data stalls detected")
            
            print("\n2. ENGINE STATUS:")
            print("-" * 80)
            engine_alive = status.get('engine_alive', False)
            engine_activity_state = status.get('engine_activity_state', 'UNKNOWN')
            last_engine_tick = status.get('last_engine_tick_chicago', 'N/A')
            print(f"  Engine Alive: {engine_alive}")
            print(f"  Engine Activity State: {engine_activity_state}")
            print(f"  Last Engine Tick: {last_engine_tick}")
            
            print("\n3. STREAM STATES:")
            print("-" * 80)
            stream_states_response = requests.get("http://localhost:8002/api/watchdog/stream-states", timeout=5)
            if stream_states_response.status_code == 200:
                stream_states = stream_states_response.json()
                streams = stream_states.get('streams', [])
                
                bar_dependent_states = ['PRE_HYDRATION', 'ARMED', 'RANGE_BUILDING', 'RANGE_LOCKED']
                streams_expecting_bars = [s for s in streams if s.get('state') in bar_dependent_states and not s.get('committed', False)]
                
                print(f"  Total Streams: {len(streams)}")
                print(f"  Streams Expecting Bars: {len(streams_expecting_bars)}")
                
                if streams_expecting_bars:
                    print("\n  Streams in Bar-Dependent States:")
                    for s in streams_expecting_bars:
                        print(f"    {s.get('stream', 'N/A')}: {s.get('state', 'N/A')} | Instrument: {s.get('execution_instrument', 'N/A')}")
                else:
                    print("\n  [INFO] No streams are currently expecting bars")
                    print("         This is normal if ranges haven't formed yet")
            
            print("\n4. SUMMARY:")
            print("-" * 80)
            if data_stall_detected:
                print("  [WARN] DATA STALL DETECTED")
                print("         Check which instruments are stalled and why")
            elif bars_expected_count == 0:
                print("  [OK] No data stalls - no streams expecting bars")
                print("       This is normal if ranges haven't formed yet")
            else:
                print("  [OK] No data stalls - bars arriving normally")
                
        else:
            print(f"  [ERROR] Watchdog API returned status {response.status_code}")
            
    except requests.exceptions.ConnectionError:
        print("  [ERROR] Cannot connect to watchdog backend (http://localhost:8002)")
        print("         Is the watchdog backend running?")
    except Exception as e:
        print(f"  [ERROR] {e}")
    
    print("="*80)

if __name__ == "__main__":
    main()
