/**
 * ChicagoClock - Isolated clock component that updates every second.
 * Only this component re-renders; parent (WatchdogPage) does not.
 */
import { useState, useEffect } from 'react'
import { getCurrentChicagoTime } from '../../utils/timeUtils.ts'

export function ChicagoClock() {
  const [time, setTime] = useState(getCurrentChicagoTime())

  useEffect(() => {
    const interval = setInterval(() => {
      setTime(getCurrentChicagoTime())
    }, 1000)
    return () => clearInterval(interval)
  }, [])

  return <span className="text-sm font-mono">{time}</span>
}
