"""Check recent events in logs"""
import json
from pathlib import Path

def main():
    log_file = Path("logs/robot/robot_ENGINE.jsonl")
    
    events = []
    try:
        with open(log_file, 'r', encoding='utf-8-sig') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        print(f"Error: {e}")
        return
    
    recent = events[-30:] if len(events) > 30 else events
    
    print("="*80)
    print("RECENT EVENTS (Last 30)")
    print("="*80)
    
    for e in recent:
        ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
        level = e.get('level', 'N/A')
        event = e.get('event', 'N/A')
        print(f"{ts:19} | {level:5} | {event}")

if __name__ == '__main__':
    main()
