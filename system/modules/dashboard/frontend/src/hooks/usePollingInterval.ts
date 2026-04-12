/**
 * Shared polling interval utility
 * Handles setInterval + cleanup, optionally respects tab visibility
 */
import { useEffect, useRef, useState, useCallback } from 'react'

interface UsePollingIntervalOptions {
  pauseWhenHidden?: boolean
}

export function usePollingInterval(
  callback: () => void | Promise<void>,
  intervalMs: number,
  options: UsePollingIntervalOptions = {}
): { isPolling: boolean; lastSuccessfulPollTimestamp: number | null } {
  const [isPolling, setIsPolling] = useState(true)
  const [lastSuccessfulPollTimestamp, setLastSuccessfulPollTimestamp] = useState<number | null>(null)
  const intervalRef = useRef<number | null>(null)
  const callbackRef = useRef(callback)
  
  // Update callback ref when it changes
  useEffect(() => {
    callbackRef.current = callback
  }, [callback])
  
  useEffect(() => {
    if (!isPolling) {
      return
    }
    
    // Check tab visibility if option is enabled
    const checkVisibility = () => {
      if (options.pauseWhenHidden && document.hidden) {
        setIsPolling(false)
        return false
      }
      return true
    }
    
    const poll = async () => {
      if (!checkVisibility()) {
        return
      }
      
      try {
        await callbackRef.current()
        setLastSuccessfulPollTimestamp(Date.now())
      } catch (error) {
        // Error handling is done in the callback
        console.error('Polling error:', error)
      }
    }
    
    // Initial poll
    poll()
    
    // Set up interval
    intervalRef.current = window.setInterval(poll, intervalMs)
    
    // Handle visibility change
    const handleVisibilityChange = () => {
      if (options.pauseWhenHidden) {
        if (document.hidden) {
          setIsPolling(false)
        } else {
          setIsPolling(true)
          // Poll immediately when tab becomes visible
          poll()
        }
      }
    }
    
    if (options.pauseWhenHidden) {
      document.addEventListener('visibilitychange', handleVisibilityChange)
    }
    
    return () => {
      if (intervalRef.current !== null) {
        clearInterval(intervalRef.current)
      }
      if (options.pauseWhenHidden) {
        document.removeEventListener('visibilitychange', handleVisibilityChange)
      }
    }
  }, [isPolling, intervalMs, options.pauseWhenHidden])
  
  return { isPolling, lastSuccessfulPollTimestamp }
}
