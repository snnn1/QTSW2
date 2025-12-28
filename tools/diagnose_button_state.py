"""
Diagnostic tool to monitor pipeline button state and status changes
This helps identify timing issues with button enable/disable
"""

import json
import time
from pathlib import Path
from datetime import datetime
from collections import deque

qtsw2_root = Path(__file__).parent.parent

def analyze_latest_pipeline_run():
    """Analyze the latest pipeline run for state transitions"""
    print("="*80)
    print("LATEST PIPELINE RUN ANALYSIS")
    print("="*80)
    
    events_dir = qtsw2_root / "automation" / "logs" / "events"
    if not events_dir.exists():
        print("Events directory not found")
        return
    
    pipeline_files = sorted(
        events_dir.glob("pipeline_*.jsonl"),
        key=lambda p: p.stat().st_mtime,
        reverse=True
    )
    
    if not pipeline_files:
        print("No pipeline runs found")
        return
    
    latest_file = pipeline_files[0]
    print(f"\nLatest run: {latest_file.name}")
    mtime = datetime.fromtimestamp(latest_file.stat().st_mtime)
    print(f"Modified: {mtime.strftime('%Y-%m-%d %H:%M:%S')}\n")
    
    with open(latest_file, 'r') as f:
        events = [json.loads(l) for l in f if l.strip()]
    
    # Track state changes
    state_changes = []
    pipeline_events = []
    
    for event in events:
        if event.get('stage') == 'pipeline':
            pipeline_events.append(event)
        if event.get('event') == 'state_change':
            data = event.get('data', {})
            state_changes.append({
                'timestamp': event.get('timestamp'),
                'old_state': data.get('old_state'),
                'new_state': data.get('new_state'),
                'run_id': event.get('run_id')
            })
    
    print("[STATE TRANSITIONS]")
    if state_changes:
        for i, change in enumerate(state_changes):
            print(f"  {i+1}. {change['old_state']} -> {change['new_state']}")
            print(f"     Time: {change['timestamp']}")
            print(f"     Run ID: {change['run_id'][:8] if change['run_id'] else 'None'}")
    else:
        print("  No state transitions found")
    
    print(f"\n[PIPELINE EVENTS]")
    for event in pipeline_events[:10]:
        event_type = event.get('event')
        msg = event.get('msg', '')[:80]
        timestamp = event.get('timestamp', '')
        print(f"  {event_type}: {msg}")
        if timestamp:
            print(f"    Time: {timestamp}")
    
    return state_changes, pipeline_events

def check_backend_status_endpoint():
    """Check what the backend status endpoint currently returns"""
    print("\n" + "="*80)
    print("BACKEND STATUS ENDPOINT CHECK")
    print("="*80)
    print("\nTo check the backend status endpoint, you can:")
    print("  1. Open browser DevTools (F12)")
    print("  2. Go to Network tab")
    print("  3. Click 'Run Pipeline Now' button")
    print("  4. Look for requests to /api/pipeline/status")
    print("  5. Check the response body for 'state' and 'run_id' fields")
    print("\nOr run this in browser console:")
    print("  fetch('/api/pipeline/status').then(r => r.json()).then(console.log)")

def create_browser_diagnostic_script():
    """Create a JavaScript snippet to run in browser console"""
    script = """
// Pipeline Button State Diagnostic
// Paste this into browser console and click "Run Pipeline Now"

console.log("=".repeat(80));
console.log("PIPELINE BUTTON STATE DIAGNOSTIC");
console.log("=".repeat(80));

let stateLog = [];
let statusPollCount = 0;

// Monitor status polling
const originalFetch = window.fetch;
window.fetch = function(...args) {
  if (args[0] && args[0].includes('/api/pipeline/status')) {
    statusPollCount++;
    const url = args[0];
    console.log(`[${statusPollCount}] Status poll: ${url}`);
    
    return originalFetch.apply(this, args).then(response => {
      response.clone().json().then(data => {
        const timestamp = new Date().toISOString();
        stateLog.push({
          timestamp,
          poll: statusPollCount,
          state: data.state,
          isRunning: data.state === 'running',
          runId: data.run_id || data.runId,
          fullData: data
        });
        console.log(`[${statusPollCount}] Status response:`, {
          state: data.state,
          isRunning: data.state === 'running',
          runId: data.run_id || data.runId
        });
      });
      return response;
    });
  }
  return originalFetch.apply(this, args);
};

// Monitor WebSocket events
let wsEventCount = 0;
const wsEvents = [];

// Hook into WebSocket if available
const originalWebSocket = window.WebSocket;
window.WebSocket = function(...args) {
  const ws = new originalWebSocket(...args);
  
  ws.addEventListener('message', (event) => {
    try {
      const data = JSON.parse(event.data);
      if (data.type === 'event' && data.event === 'state_change') {
        wsEventCount++;
        const timestamp = new Date().toISOString();
        wsEvents.push({
          timestamp,
          event: wsEventCount,
          oldState: data.data?.old_state,
          newState: data.data?.new_state,
          runId: data.run_id,
          fullData: data
        });
        console.log(`[WS ${wsEventCount}] State change:`, {
          oldState: data.data?.old_state,
          newState: data.data?.new_state,
          runId: data.run_id
        });
      }
    } catch (e) {
      // Not JSON or not our event
    }
  });
  
  return ws;
};

// Monitor button clicks
document.addEventListener('click', (e) => {
  if (e.target.textContent.includes('Run Pipeline Now') || 
      e.target.textContent.includes('Running...')) {
    const timestamp = new Date().toISOString();
    console.log(`[CLICK] Button clicked at ${timestamp}`);
    console.log(`  Button text: ${e.target.textContent}`);
    console.log(`  Button disabled: ${e.target.disabled}`);
    console.log(`  Button classes: ${e.target.className}`);
  }
});

console.log("\\nDiagnostic active! Click 'Run Pipeline Now' button.");
console.log("After the pipeline runs, type 'showDiagnosticResults()' to see summary.");

window.showDiagnosticResults = function() {
  console.log("\\n" + "=".repeat(80));
  console.log("DIAGNOSTIC RESULTS");
  console.log("=".repeat(80));
  
  console.log(`\\nStatus polls: ${statusPollCount}`);
  console.log(`WebSocket state changes: ${wsEventCount}`);
  
  console.log("\\n[STATUS POLL TIMELINE]");
  stateLog.forEach((entry, i) => {
    console.log(`  ${i+1}. [${entry.timestamp}] State: ${entry.state}, isRunning: ${entry.isRunning}, RunID: ${entry.runId?.substring(0, 8) || 'None'}`);
  });
  
  console.log("\\n[WEBSOCKET STATE CHANGES]");
  wsEvents.forEach((entry, i) => {
    console.log(`  ${i+1}. [${entry.timestamp}] ${entry.oldState} -> ${entry.newState}, RunID: ${entry.runId?.substring(0, 8) || 'None'}`);
  });
  
  // Check for race conditions
  console.log("\\n[ANALYSIS]");
  if (stateLog.length > 0) {
    const firstPoll = stateLog[0];
    const runningPolls = stateLog.filter(e => e.isRunning);
    const idlePolls = stateLog.filter(e => !e.isRunning && e.state === 'idle');
    
    console.log(`  First poll state: ${firstPoll.state} (isRunning: ${firstPoll.isRunning})`);
    console.log(`  Polls showing running: ${runningPolls.length}`);
    console.log(`  Polls showing idle: ${idlePolls.length}`);
    
    // Check if we went from running to idle too quickly
    if (runningPolls.length > 0 && idlePolls.length > 0) {
      const firstRunning = runningPolls[0];
      const firstIdle = idlePolls[0];
      if (firstIdle.timestamp < firstRunning.timestamp || 
          (firstIdle.poll < firstRunning.poll && firstIdle.poll > 1)) {
        console.log("  [WARNING] POTENTIAL RACE CONDITION: Idle state appeared before/right after running state");
      }
    }
  }
};
"""
    
    output_file = qtsw2_root / "tools" / "browser_diagnostic_script.js"
    with open(output_file, 'w', encoding='utf-8') as f:
        f.write(script)
    
    print("\n" + "="*80)
    print("BROWSER DIAGNOSTIC SCRIPT CREATED")
    print("="*80)
    print(f"\nScript saved to: {output_file}")
    print("\nTo use:")
    print("  1. Open the dashboard in your browser")
    print("  2. Open DevTools (F12)")
    print("  3. Go to Console tab")
    print("  4. Copy and paste the contents of the script file")
    print("  5. Press Enter to run it")
    print("  6. Click 'Run Pipeline Now' button")
    print("  7. After pipeline completes, type: showDiagnosticResults()")
    print("\nThe script will monitor:")
    print("  - Status API polling")
    print("  - WebSocket state change events")
    print("  - Button click events")
    print("  - State transitions and timing")

def main():
    print("="*80)
    print("PIPELINE BUTTON STATE DIAGNOSTIC TOOL")
    print("="*80)
    print(f"Timestamp: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
    
    # Analyze latest run
    state_changes, pipeline_events = analyze_latest_pipeline_run()
    
    # Create browser diagnostic script
    create_browser_diagnostic_script()
    
    # Instructions
    print("\n" + "="*80)
    print("NEXT STEPS")
    print("="*80)
    print("\n1. Use the browser diagnostic script to monitor real-time behavior")
    print("2. Check the Network tab in DevTools for API calls")
    print("3. Look for timing issues between:")
    print("   - Button click")
    print("   - Status polling")
    print("   - WebSocket events")
    print("   - State updates")

if __name__ == "__main__":
    main()

