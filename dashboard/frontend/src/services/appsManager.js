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
    const data = await pipelineManager.startApp(appName)
    if (data.url) {
      window.open(data.url, '_blank')
    }
  } catch (error) {
    console.error(`Failed to start ${appName} app:`, error)
    throw error
  }
}

// Export as default object for convenience
export const appsManager = { start }

