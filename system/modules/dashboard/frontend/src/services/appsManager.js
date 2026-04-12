/**
 * Apps manager - handles starting external applications
 */

import * as pipelineManager from './pipelineManager'

/**
 * Start an application by name
 * @param {string} appName - Name of the app ('translator', 'analyzer', 'matrix')
 * @returns {Promise<void>}
 */
export const start = async (appName) => {
  try {
    console.log(`[appsManager] Starting ${appName} app...`)
    const data = await pipelineManager.startApp(appName)
    console.log(`[appsManager] ${appName} app start response:`, data)
    if (data && data.url) {
      // Only open URL if app was actually started (not if already running/starting)
      if (data.status === 'started' || data.status === 'already_running') {
        window.open(data.url, '_blank')
      }
    }
    return data
  } catch (error) {
    console.error(`[appsManager] Failed to start ${appName} app:`, error)
    throw error
  }
}

// Export as default object for convenience
export const appsManager = { start }

