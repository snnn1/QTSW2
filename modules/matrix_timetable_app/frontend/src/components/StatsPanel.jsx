export default function StatsPanel({ streamId, stats, loading, error, noData }) {
  if (loading) {
    return (
      <div className="bg-gray-900 rounded-lg p-4 mb-4">
        <p className="text-gray-400 text-sm">Calculating statistics...</p>
      </div>
    )
  }

  if (error) {
    return (
      <div className="bg-gray-900 rounded-lg p-4 mb-4">
        <p className="text-red-400 text-sm">Error calculating statistics</p>
      </div>
    )
  }

  if (noData || !stats) {
    return (
      <div className="bg-gray-900 rounded-lg p-4 mb-4">
        <p className="text-gray-400 text-sm">No data available for statistics</p>
      </div>
    )
  }

  // Render different stats for master vs individual streams
  if (streamId === 'master') {
    // Master stream - show all 4 sections
    return (
      <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
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
              <div className="text-xs text-gray-400 mb-1">Total Trades</div>
              <div className="text-lg font-semibold">{stats.totalTrades}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Total Days</div>
              <div className="text-lg font-semibold">{stats.totalDays}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Avg Trades/Day</div>
              <div className="text-lg font-semibold">{stats.avgTradesPerDay}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Profit per Day</div>
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
              <div className="text-xs text-gray-400 mb-1">Daily Win Rate</div>
              <div className="text-lg font-semibold text-green-400">
                {stats.dailyWinRate !== null && stats.dailyWinRate !== undefined ? `${stats.dailyWinRate}%` : 'N/A'}
              </div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Weekly Win Rate</div>
              <div className="text-lg font-semibold text-green-400">
                {stats.weeklyWinRate !== null && stats.weeklyWinRate !== undefined ? `${stats.weeklyWinRate}%` : 'N/A'}
              </div>
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
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
            <div>
              <div className="text-xs text-gray-400 mb-1">Max Drawdown ($)</div>
              <div className="text-lg font-semibold text-red-400">{stats.maxDrawdownDollars}</div>
            </div>
            <div>
              <div className="text-xs text-gray-400 mb-1">Time-to-Recovery (Days)</div>
              <div className="text-lg font-semibold">{stats.timeToRecoveryDays ?? 'N/A'}</div>
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
  } else {
    // Individual streams - show 3 sections with 8 stats total
    return (
      <div className="bg-gray-900 rounded-lg p-4 mb-4 border border-gray-700">
        {/* Section 1: Core Performance */}
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
        
        {/* Section 2: Risk & Volatility */}
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
        
        {/* Section 3: Efficiency */}
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
}

























