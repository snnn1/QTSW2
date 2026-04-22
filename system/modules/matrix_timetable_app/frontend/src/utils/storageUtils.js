function readStoredValue(key, parser, fallbackValue) {
  if (typeof localStorage === 'undefined') {
    return fallbackValue
  }

  const raw = localStorage.getItem(key)
  if (raw === null) {
    return fallbackValue
  }

  try {
    return parser(raw)
  } catch (error) {
    console.warn(`[storageUtils] Invalid localStorage value for "${key}". Resetting to default.`, error)
    try {
      localStorage.removeItem(key)
    } catch {
      // Ignore secondary storage failures and continue with the fallback.
    }
    return fallbackValue
  }
}

export function readStoredBoolean(key, fallbackValue = false) {
  return readStoredValue(
    key,
    (raw) => {
      const normalized = String(raw).trim().toLowerCase()
      if (normalized === 'true' || normalized === '1') return true
      if (normalized === 'false' || normalized === '0') return false

      const parsed = JSON.parse(raw)
      if (typeof parsed !== 'boolean') {
        throw new Error(`Expected boolean for ${key}`)
      }
      return parsed
    },
    fallbackValue
  )
}

export function readStoredNumber(key, fallbackValue = 0) {
  return readStoredValue(
    key,
    (raw) => {
      const parsed = Number.parseFloat(String(raw).trim())
      if (!Number.isFinite(parsed)) {
        throw new Error(`Expected finite number for ${key}`)
      }
      return parsed
    },
    fallbackValue
  )
}

export function readStoredChoice(key, allowedValues, fallbackValue) {
  return readStoredValue(
    key,
    (raw) => {
      const normalized = String(raw).trim()
      if (!allowedValues.includes(normalized)) {
        throw new Error(`Expected one of ${allowedValues.join(', ')} for ${key}`)
      }
      return normalized
    },
    fallbackValue
  )
}

export function readStoredJsonObject(key, fallbackValue = {}) {
  return readStoredValue(
    key,
    (raw) => {
      const parsed = JSON.parse(raw)
      if (parsed == null || typeof parsed !== 'object' || Array.isArray(parsed)) {
        throw new Error(`Expected object for ${key}`)
      }
      return parsed
    },
    fallbackValue
  )
}
