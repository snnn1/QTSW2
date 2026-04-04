/**
 * Ensures WatchdogPage's alert pipeline (deriveOverallExecutionStatus → executionSeverityAlert)
 * stays aligned with severity rules — same sequence as useMemo in WatchdogPage.
 */
import { describe, it, expect } from 'vitest'
import { deriveOverallExecutionStatus, executionSeverityAlert } from './utils/executionSeverity'

describe('WatchdogPage execution alert pipeline', () => {
  it('prepends canonical operator message for gate fail-closed', () => {
    const overall = deriveOverallExecutionStatus(
      {
        kill_switch_active: false,
        execution_safe: false,
        reconciliation_gate_state: 'FAIL_CLOSED',
        recovery_state: 'CONNECTED_OK',
        adoption_grace_expired_active: false,
      },
      null
    )
    const alert = executionSeverityAlert(overall)
    expect(alert?.type).toBe('critical')
    expect(alert?.message).toBe('Reconciliation gate fail-closed — execution blocked')
  })
})
