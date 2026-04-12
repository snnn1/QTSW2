import { formatChicagoWallIso } from './dateUtils'

/**
 * Canonical console audit line when the matrix UI trading day changes (CME or backend sync).
 * Matches engine / builder TRADING_DATE_ROLLED field names where applicable.
 */
export function logTradingDateRolledUi({ oldTradingDateStr, newTradingDateStr }) {
  const now = new Date()
  const payload = {
    event: 'TRADING_DATE_ROLLED',
    ts_utc: now.toISOString(),
    ts_chicago: formatChicagoWallIso(now),
    old_trading_date: oldTradingDateStr,
    new_trading_date: newTradingDateStr,
    source: 'UI',
    document_visibility_state: typeof document !== 'undefined' ? document.visibilityState : 'unknown'
  }
  console.info('[TRADING_DATE_ROLLED]', JSON.stringify(payload))
}
