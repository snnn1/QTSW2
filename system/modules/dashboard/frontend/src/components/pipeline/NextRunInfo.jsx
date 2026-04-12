import { TimeDisplay } from '../ui/TimeDisplay'

/**
 * Next Run Information component - Shows 15-minute scheduler countdown
 */
export function NextRunInfo({ scheduleInfo }) {
  if (!scheduleInfo) {
    return (
      <TimeDisplay
        label="Next Run"
        time="Loading..."
      />
    )
  }

  // Handle API response (snake_case) or frontend format (camelCase)
  const nextRunTime = scheduleInfo?.nextRunTime || scheduleInfo?.next_run_time_short || scheduleInfo?.next_run_time
  const waitDisplay = scheduleInfo?.waitDisplay || scheduleInfo?.wait_display

  // If there's an error, show it
  if (scheduleInfo?.error) {
    return (
      <TimeDisplay
        label="Next Run"
        time="Error calculating"
      />
    )
  }

  // If we don't have the required data, show loading
  if (!nextRunTime || !waitDisplay) {
    return (
      <TimeDisplay
        label="Next Run"
        time="Loading..."
      />
    )
  }

  return (
    <TimeDisplay
      label="Next Run"
      time={`${nextRunTime} (${waitDisplay})`}
    />
  )
}

