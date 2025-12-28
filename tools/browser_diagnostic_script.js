
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

console.log("\nDiagnostic active! Click 'Run Pipeline Now' button.");
console.log("After the pipeline runs, type 'showDiagnosticResults()' to see summary.");

window.showDiagnosticResults = function() {
  console.log("\n" + "=".repeat(80));
  console.log("DIAGNOSTIC RESULTS");
  console.log("=".repeat(80));
  
  console.log(`\nStatus polls: ${statusPollCount}`);
  console.log(`WebSocket state changes: ${wsEventCount}`);
  
  console.log("\n[STATUS POLL TIMELINE]");
  stateLog.forEach((entry, i) => {
    console.log(`  ${i+1}. [${entry.timestamp}] State: ${entry.state}, isRunning: ${entry.isRunning}, RunID: ${entry.runId?.substring(0, 8) || 'None'}`);
  });
  
  console.log("\n[WEBSOCKET STATE CHANGES]");
  wsEvents.forEach((entry, i) => {
    console.log(`  ${i+1}. [${entry.timestamp}] ${entry.oldState} -> ${entry.newState}, RunID: ${entry.runId?.substring(0, 8) || 'None'}`);
  });
  
  // Check for race conditions
  console.log("\n[ANALYSIS]");
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
