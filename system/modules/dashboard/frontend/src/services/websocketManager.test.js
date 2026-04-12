/**
 * Test file for WebSocket Manager fixes
 * 
 * Tests:
 * 1. Normal flow
 * 2. Backend not sending snapshot
 * 3. Malformed snapshot (simulate)
 * 4. Manual disconnect + reconnect
 * 
 * Run in browser console or Node.js with jsdom
 */

// Mock WebSocket for testing
class MockWebSocket {
  constructor(url) {
    this.url = url
    this.readyState = MockWebSocket.CONNECTING
    this._attemptId = null
    this.onopen = null
    this.onmessage = null
    this.onclose = null
    this.onerror = null
    
    // Simulate connection after a brief delay
    setTimeout(() => {
      if (this.readyState === MockWebSocket.CONNECTING) {
        this.readyState = MockWebSocket.OPEN
        if (this.onopen) this.onopen({})
      }
    }, 10)
  }
  
  close(code, reason) {
    this.readyState = MockWebSocket.CLOSED
    if (this.onclose) {
      this.onclose({ code: code || 1000, reason: reason || '' })
    }
  }
  
  send(data) {
    // Not used in our tests
  }
}

MockWebSocket.CONNECTING = 0
MockWebSocket.OPEN = 1
MockWebSocket.CLOSING = 2
MockWebSocket.CLOSED = 3

// Import the manager (adjust path as needed)
// For browser testing, you'll need to import it differently
// import { websocketManager } from './websocketManager.js'

// Test helper
function createTestManager() {
  // Create a fresh manager instance for testing
  // In real usage, we'd need to export the class, not just instance
  // For now, we'll test the singleton
  return websocketManager
}

// Test 1: Normal flow
async function testNormalFlow() {
  console.log('\n=== TEST 1: Normal Flow ===')
  const manager = createTestManager()
  const events = []
  
  // Replace WebSocket with mock
  const OriginalWebSocket = window.WebSocket
  window.WebSocket = MockWebSocket
  
  try {
    manager.connect('test-run-1', (event) => {
      events.push(event)
      console.log('Event received:', event.type || event.event)
    }, true)
    
    // Wait for connection
    await new Promise(resolve => setTimeout(resolve, 50))
    
    // Simulate snapshot
    const ws = manager.ws
    if (ws && ws.onmessage) {
      ws.onmessage({
        data: JSON.stringify({
          type: 'snapshot',
          events: [],
          window_hours: 1
        })
      })
    }
    
    // Simulate regular event
    await new Promise(resolve => setTimeout(resolve, 10))
    if (ws && ws.onmessage) {
      ws.onmessage({
        data: JSON.stringify({
          run_id: 'test-run-1',
          stage: 'pipeline',
          event: 'start',
          timestamp: new Date().toISOString()
        })
      })
    }
    
    await new Promise(resolve => setTimeout(resolve, 50))
    
    console.log('Events received:', events.length)
    console.log('Expected: 2 (snapshot + start event)')
    console.log('Result:', events.length === 2 ? 'PASS' : 'FAIL')
    
    manager.disconnect()
  } finally {
    window.WebSocket = OriginalWebSocket
  }
}

// Test 2: Backend not sending snapshot
async function testNoSnapshot() {
  console.log('\n=== TEST 2: Backend Not Sending Snapshot ===')
  const manager = createTestManager()
  const events = []
  
  const OriginalWebSocket = window.WebSocket
  window.WebSocket = MockWebSocket
  
  try {
    manager.connect('test-run-2', (event) => {
      events.push(event)
      console.log('Event received:', event.type || event.event)
    }, true)
    
    // Wait for connection
    await new Promise(resolve => setTimeout(resolve, 50))
    
    const ws = manager.ws
    if (ws && ws.onmessage) {
      // Send regular event immediately (before snapshot)
      ws.onmessage({
        data: JSON.stringify({
          run_id: 'test-run-2',
          stage: 'pipeline',
          event: 'start',
          timestamp: new Date().toISOString()
        })
      })
    }
    
    // Wait - event should be dropped
    await new Promise(resolve => setTimeout(resolve, 100))
    console.log('Events received (before timeout):', events.length)
    console.log('Expected: 0 (event dropped, no snapshot)')
    
    // Wait for snapshot timeout (5 seconds)
    console.log('Waiting for snapshot timeout (5 seconds)...')
    await new Promise(resolve => setTimeout(resolve, 5100))
    
    // Send another event after timeout
    if (ws && ws.onmessage) {
      ws.onmessage({
        data: JSON.stringify({
          run_id: 'test-run-2',
          stage: 'pipeline',
          event: 'log',
          timestamp: new Date().toISOString()
        })
      })
    }
    
    await new Promise(resolve => setTimeout(resolve, 50))
    
    console.log('Events received (after timeout):', events.length)
    console.log('Expected: 1 (event after timeout allows events through)')
    console.log('Result:', events.length === 1 ? 'PASS' : 'FAIL')
    
    manager.disconnect()
  } finally {
    window.WebSocket = OriginalWebSocket
  }
}

// Test 3: Malformed snapshot
async function testMalformedSnapshot() {
  console.log('\n=== TEST 3: Malformed Snapshot ===')
  const manager = createTestManager()
  const events = []
  
  const OriginalWebSocket = window.WebSocket
  window.WebSocket = MockWebSocket
  
  try {
    manager.connect('test-run-3', (event) => {
      events.push(event)
      console.log('Event received:', event.type || event.event)
    }, true)
    
    // Wait for connection
    await new Promise(resolve => setTimeout(resolve, 50))
    
    const ws = manager.ws
    if (ws && ws.onmessage) {
      // Send malformed snapshot (invalid JSON)
      try {
        ws.onmessage({
          data: '{"type":"snapshot",invalid json}'  // Malformed JSON
        })
      } catch (e) {
        // Expected to fail
      }
    }
    
    await new Promise(resolve => setTimeout(resolve, 100))
    
    // Send regular event - should be allowed through (fallback)
    if (ws && ws.onmessage) {
      ws.onmessage({
        data: JSON.stringify({
          run_id: 'test-run-3',
          stage: 'pipeline',
          event: 'start',
          timestamp: new Date().toISOString()
        })
      })
    }
    
    await new Promise(resolve => setTimeout(resolve, 50))
    
    console.log('Events received:', events.length)
    console.log('Expected: 1 (event allowed through after malformed snapshot)')
    console.log('Result:', events.length === 1 ? 'PASS' : 'FAIL')
    
    manager.disconnect()
  } finally {
    window.WebSocket = OriginalWebSocket
  }
}

// Test 4: Manual disconnect + reconnect
async function testDisconnectReconnect() {
  console.log('\n=== TEST 4: Manual Disconnect + Reconnect ===')
  const manager = createTestManager()
  const events = []
  let runIdBeforeDisconnect = null
  
  const OriginalWebSocket = window.WebSocket
  window.WebSocket = MockWebSocket
  
  try {
    // Connect
    manager.connect('test-run-4', (event) => {
      events.push(event)
      console.log('Event received:', event.type || event.event)
    }, true)
    
    await new Promise(resolve => setTimeout(resolve, 50))
    runIdBeforeDisconnect = manager.getRunId()
    console.log('RunId before disconnect:', runIdBeforeDisconnect)
    
    // Disconnect
    manager.disconnect()
    await new Promise(resolve => setTimeout(resolve, 50))
    
    const runIdAfterDisconnect = manager.getRunId()
    console.log('RunId after disconnect:', runIdAfterDisconnect)
    console.log('Expected: null')
    console.log('Result:', runIdAfterDisconnect === null ? 'PASS' : 'FAIL')
    
    // Reconnect
    manager.connect('test-run-4-new', (event) => {
      events.push(event)
      console.log('Event received:', event.type || event.event)
    }, true)
    
    await new Promise(resolve => setTimeout(resolve, 50))
    const runIdAfterReconnect = manager.getRunId()
    console.log('RunId after reconnect:', runIdAfterReconnect)
    console.log('Expected: test-run-4-new')
    console.log('Result:', runIdAfterReconnect === 'test-run-4-new' ? 'PASS' : 'FAIL')
    
    manager.disconnect()
  } finally {
    window.WebSocket = OriginalWebSocket
  }
}

// Run all tests
async function runAllTests() {
  console.log('Starting WebSocket Manager Tests...\n')
  
  try {
    await testNormalFlow()
    await testNoSnapshot()
    await testMalformedSnapshot()
    await testDisconnectReconnect()
    
    console.log('\n=== All Tests Complete ===')
  } catch (error) {
    console.error('Test error:', error)
  }
}

// Export for use
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    testNormalFlow,
    testNoSnapshot,
    testMalformedSnapshot,
    testDisconnectReconnect,
    runAllTests
  }
}

// For browser console usage:
// runAllTests()

