/**
 * Stats display component - renders formatted statistics for master or individual streams.
 * Receives pre-formatted stats from formatWorkerStats (called by parent).
 */
import { devLog } from '../utils/logger'

export default function StatsContent({
  stats,
  streamId,
  includeFilteredExecuted,
  setIncludeFilteredExecuted
}) {
  if (!stats) return null

  if (streamId === 'master') {
    return (
      <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
        {/* Toggle for including filtered executed trades */}
        <div className="mb-4 pb-3 border-b border-gray-700 flex items-center justify-between">
          <div>
            <h4 className="text-sm font-semibold text-gray-300 mb-1">Statistics Settings</h4>
            <p className="text-xs text-gray-500">Performance stats are computed on executed trades only (Win, Loss, BE, TIME)</p>
          </div>
          <div className="flex items-center">
            <span className="text-sm text-gray-400 mr-3">Include filtered executed trades</span>
            <div
              className="relative cursor-pointer"
              onClick={() => {
                const newValue = !includeFilteredExecuted
                devLog(`[Toggle] Button clicked: ${includeFilteredExecuted} -> ${newValue}`)
                setIncludeFilteredExecuted(newValue)
              }}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault()
                  const newValue = !includeFilteredExecuted
                  devLog(`[Toggle] Keyboard activated: ${includeFilteredExecuted} -> ${newValue}`)
                  setIncludeFilteredExecuted(newValue)
                }
              }}
            >
              <input
                type="checkbox"
                className="sr-only"
                checked={includeFilteredExecuted}
                onChange={(e) => {
                  devLog(`[Toggle] Checkbox changed: ${includeFilteredExecuted} -> ${e.target.checked}`)
                  setIncludeFilteredExecuted(e.target.checked)
                }}
                readOnly
              />
              <div className={`block w-14 h-8 rounded-full ${includeFilteredExecuted ? 'bg-green-500' : 'bg-gray-600'}`}></div>
              <div className={`absolute left-1 top-1 bg-white w-6 h-6 rounded-full transition transform ${includeFilteredExecuted ? 'translate-x-6' : ''}`}></div>
            </div>
          </div>
        </div>

        {/* Section 1: Core Performance */}
        <div className="mb-4">
          <h4 className="text-sm font-semibold mb-3 text-gray-300">Core Performance</h4>
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
            <div>
              <div className="text-xs text-gray-400 mb-1">Total Profit ($)</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.totalProfit) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.totalProfitDollars}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Executed Trades</div>
              <div className="text-lg font-semibold">{stats.executedTradesTotal !== undefined ? stats.executedTradesTotal : (stats.allowedTrades || stats.totalTrades)}</div>
              {stats.executedTradesAllowed !== undefined && stats.executedTradesFiltered !== undefined && (
                <div className="text-xs text-gray-500 mt-1">
                  Allowed: {stats.executedTradesAllowed} | Filtered: {stats.executedTradesFiltered}
                </div>
              )}
              {stats.totalRows !== undefined && stats.filteredRows !== undefined && (
                <div className="text-xs text-gray-400 mt-1">
                  Total Rows: {stats.totalRows} | Filtered Rows: {stats.filteredRows}
                </div>
              )}
              {stats.notradeTotal !== undefined && (
                <div className="text-xs text-gray-500 mt-1">
                  NoTrade: {stats.notradeTotal}
                </div>
              )}
            </div>
            {stats.executedTradingDays !== undefined && stats.allowedTradingDays !== undefined && (
              <div>
                <div className="text-xs text-gray-400 mb-1">Trading Days</div>
                <div className="text-lg font-semibold">
                  Executed: {stats.executedTradingDays}
                  <span className="text-xs text-gray-500 ml-2">Allowed: {stats.allowedTradingDays}</span>
                </div>
              </div>
            )}
            <div>
              <div className="text-xs text-gray-400 mb-1">Avg Trades per Active Day</div>
              <div className="text-lg font-semibold">{stats.avgTradesPerDay}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Profit per Active Day</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.profitPerDay?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.profitPerDay || 'N/A'}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Profit per Week</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.profitPerWeek?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.profitPerWeek || 'N/A'}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Profit per Month</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.profitPerMonth?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.profitPerMonth || 'N/A'}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Profit per Year</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.profitPerYear?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.profitPerYear || 'N/A'}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Profit per Trade (Mean PnL)</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.profitPerTrade?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.profitPerTrade || 'N/A'}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Win Rate</div>
              <div className="text-lg font-semibold text-green-400">{stats.winRate}%</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Wins</div>
              <div className="text-lg font-semibold text-green-400">{stats.wins}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Losses</div>
              <div className="text-lg font-semibold text-red-400">{stats.losses}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Break-Even</div>
              <div className="text-lg font-semibold">{stats.breakEven}</div>
            </div>
            {stats.time !== undefined && (
              <div>
                <div className="text-xs text-gray-400 mb-1">TIME</div>
                <div className="text-lg font-semibold">{stats.time}</div>
              </div>
            )}
          </div>
        </div>

        {/* Section 2: Risk-Adjusted Performance */}
        <div className="mb-4 pt-4 border-t border-gray-700">
          <h4 className="text-sm font-semibold mb-3 text-gray-300">Risk-Adjusted Performance</h4>
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
            <div>
              <div className="text-xs text-gray-400 mb-1">Sharpe Ratio</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.sharpeRatio) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.sharpeRatio}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Sortino Ratio</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.sortinoRatio) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.sortinoRatio}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Calmar Ratio</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.calmarRatio) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.calmarRatio}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Profit Factor</div>
              <div className="text-lg font-semibold">{stats.profitFactor}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Risk-Reward</div>
              <div className="text-lg font-semibold">{stats.rrRatio}</div>
            </div>
          </div>
        </div>

        {/* Section 3: Drawdowns & Stability */}
        <div className="mb-4 pt-4 border-t border-gray-700">
          <h4 className="text-sm font-semibold mb-3 text-gray-300">Drawdowns & Stability</h4>
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
            <div>
              <div className="text-xs text-gray-400 mb-1">Max Drawdown ($)</div>
              <div className="text-lg font-semibold text-red-400">{stats.maxDrawdownDollars}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Time-to-Recovery (Days)</div>
              <div className="text-lg font-semibold">{stats.timeToRecoveryDays ?? 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Average Drawdown ($)</div>
              <div className="text-lg font-semibold text-red-400">{stats.avgDrawdownDollars || 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Avg Drawdown Duration (Days)</div>
              <div className="text-lg font-semibold">{stats.avgDrawdownDurationDays ?? 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Drawdown Frequency (per Year)</div>
              <div className="text-lg font-semibold">{stats.drawdownEpisodesPerYear ?? 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Max Consecutive Losses</div>
              <div className="text-lg font-semibold text-red-400">{stats.maxConsecutiveLosses ?? 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Monthly Return Std Dev</div>
              <div className="text-lg font-semibold">{stats.monthlyReturnStdDev || 'N/A'}</div>
            </div>
          </div>
        </div>

        {/* Section 4: PnL Distribution & Tail Risk */}
        <div className="pt-4 border-t border-gray-700">
          <h4 className="text-sm font-semibold mb-3 text-gray-300">PnL Distribution & Tail Risk</h4>
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 gap-4">
            <div>
              <div className="text-xs text-gray-400 mb-1">Median PnL per Trade</div>
              <div className={`text-lg font-semibold ${parseFloat(stats.medianPnLPerTrade?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                {stats.medianPnLPerTrade || 'N/A'}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Std Dev of PnL</div>
              <div className="text-lg font-semibold">{stats.stdDevPnL || 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">95% VaR (per trade)</div>
              <div className="text-lg font-semibold text-red-400">{stats.var95 || 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Expected Shortfall (CVaR 95%)</div>
              <div className="text-lg font-semibold text-red-400">{stats.cvar95 || 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Skewness</div>
              <div className="text-lg font-semibold">{stats.skewness ?? 'N/A'}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Kurtosis</div>
              <div className="text-lg font-semibold">{stats.kurtosis ?? 'N/A'}</div>
            </div>
          </div>
        </div>
      </div>
    )
  }

  // Individual streams - show 3 sections
  return (
    <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
      <div className="mb-4">
        <h4 className="text-sm font-semibold mb-3 text-gray-300">Core Performance</h4>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          <div>
            <div className="text-xs text-gray-400 mb-1">Trades</div>
            <div className="text-lg font-semibold">{stats.totalTrades}</div>
          </div>
          <div>
            <div className="text-xs text-gray-400 mb-1">Profit ($)</div>
            <div className={`text-lg font-semibold ${parseFloat(stats.totalProfit) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
              {stats.totalProfitDollars}
            </div>
          </div>
          <div>
            <div className="text-xs text-gray-400 mb-1">Win Rate</div>
            <div className="text-lg font-semibold text-green-400">{stats.winRate}%</div>
          </div>
          <div>
            <div className="text-xs text-gray-400 mb-1">Profit per Trade</div>
            <div className={`text-lg font-semibold ${parseFloat(stats.profitPerTrade?.replace(/[^0-9.-]/g, '') || 0) >= 0 ? 'text-green-400' : 'text-red-400'}`}>
              {stats.profitPerTrade || 'N/A'}
            </div>
          </div>
        </div>
      </div>
      <div className="mb-4 pt-4 border-t border-gray-700">
        <h4 className="text-sm font-semibold mb-3 text-gray-300">Risk & Volatility</h4>
        <div className="grid grid-cols-2 md:grid-cols-2 gap-4">
          <div>
            <div className="text-xs text-gray-400 mb-1">Std Dev of PnL</div>
            <div className="text-lg font-semibold">{stats.stdDevPnL || 'N/A'}</div>
          </div>
          <div>
            <div className="text-xs text-gray-400 mb-1">Max Consecutive Losses</div>
            <div className="text-lg font-semibold text-red-400">{stats.maxConsecutiveLosses ?? 'N/A'}</div>
          </div>
        </div>
      </div>
      <div className="pt-4 border-t border-gray-700">
        <h4 className="text-sm font-semibold mb-3 text-gray-300">Efficiency</h4>
        <div className="grid grid-cols-2 md:grid-cols-2 gap-4">
          <div>
            <div className="text-xs text-gray-400 mb-1">Profit Factor</div>
            <div className="text-lg font-semibold">{stats.profitFactor}</div>
          </div>
          <div>
            <div className="text-xs text-gray-400 mb-1">Rolling 30-Day Win Rate</div>
            <div className="text-lg font-semibold text-green-400">
              {stats.rolling30DayWinRate !== null ? `${stats.rolling30DayWinRate}%` : 'N/A'}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
