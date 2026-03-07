/**
 * Development-only logging utilities.
 * Logs are suppressed in production builds.
 */
export const devLog = (...args) => {
  if (import.meta.env.DEV) {
    console.log(...args)
  }
}

export const devWarn = (...args) => {
  if (import.meta.env.DEV) {
    console.warn(...args)
  }
}
