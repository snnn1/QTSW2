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

  return (
    <TimeDisplay
      label="Next Run"
      time={`${scheduleInfo.nextRunTime} (${scheduleInfo.waitDisplay})`}
    />
  )
}

