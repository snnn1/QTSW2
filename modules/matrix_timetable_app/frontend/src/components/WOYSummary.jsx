import { useMemo } from 'react'

/**
 * WOY Summary Component
 * 
 * WOY is analysis-only - not used for execution filtering.
 * This component provides quantitative summary statistics for Week of Year breakdowns.
 */
export default function WOYSummary({ data, yearData = {} }) {
  const formatCurrency = (value) => {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 0,
      maximumFractionDigits: 0
    }).format(value)
  }

  const summary = useMemo(() => {
    if (!data || Object.keys(data).length === 0) {
      return null
    }

    // Calculate totals for each week of year
    const weekTotals = []
    const streamTotals = {}
    const allStreams = new Set()

    Object.entries(data).forEach(([woy, streams]) => {
      if (!streams || typeof streams !== 'object') return
      
      let weekTotal = 0
      Object.entries(streams).forEach(([stream, profit]) => {
        if (stream !== 'profit' && stream !== 'trades') {
          allStreams.add(stream)
          const profitValue = parseFloat(profit) || 0
          weekTotal += profitValue
          streamTotals[stream] = (streamTotals[stream] || 0) + profitValue
        }
      })
      
      weekTotals.push({
        woy: parseInt(woy),
        total: weekTotal,
        streams: streams
      })
    })

    // Sort by total profit
    weekTotals.sort((a, b) => b.total - a.total)

    // Calculate statistics
    const profits = weekTotals.map(w => w.total)
    const totalProfit = profits.reduce((sum, p) => sum + p, 0)
    const avgProfit = profits.length > 0 ? totalProfit / profits.length : 0
    const maxProfit = Math.max(...profits, 0)
    const minProfit = Math.min(...profits, 0)
    
    // Calculate variance and standard deviation
    const variance = profits.length > 0
      ? profits.reduce((sum, p) => sum + Math.pow(p - avgProfit, 2), 0) / profits.length
      : 0
    const stdDev = Math.sqrt(variance)

    // Best and worst weeks
    const bestWeeks = weekTotals.slice(0, 10).filter(w => w.total > 0)
    const worstWeeks = weekTotals.slice(-10).reverse().filter(w => w.total < 0)

    // Count positive/negative/zero weeks
    const positiveWeeks = profits.filter(p => p > 0).length
    const negativeWeeks = profits.filter(p => p < 0).length
    const zeroWeeks = profits.filter(p => p === 0).length

    // Calculate win rate (weeks with profit > 0)
    const winRate = profits.length > 0 ? (positiveWeeks / profits.length) * 100 : 0

    // Top streams by total profit
    const topStreams = Object.entries(streamTotals)
      .sort((a, b) => b[1] - a[1])
      .slice(0, 5)

    // Find weeks with data
    const weeksWithData = weekTotals.length
    const totalPossibleWeeks = 53

    // Calculate year consistency for each WOY
    // yearData format: {woy: {year: total_profit}}
    const yearConsistency = {}
    Object.entries(yearData).forEach(([woy, years]) => {
      if (!years || typeof years !== 'object') return
      
      let profitableYears = 0
      let unprofitableYears = 0
      let totalYears = 0
      
      Object.entries(years).forEach(([year, profit]) => {
        const profitValue = parseFloat(profit) || 0
        totalYears++
        if (profitValue > 0) {
          profitableYears++
        } else if (profitValue < 0) {
          unprofitableYears++
        }
      })
      
      yearConsistency[parseInt(woy)] = {
        profitableYears,
        unprofitableYears,
        totalYears,
        consistencyRate: totalYears > 0 ? (profitableYears / totalYears) * 100 : 0
      }
    })

    // Add year consistency to week totals
    weekTotals.forEach(week => {
      week.yearConsistency = yearConsistency[week.woy] || {
        profitableYears: 0,
        unprofitableYears: 0,
        totalYears: 0,
        consistencyRate: 0
      }
    })

    return {
      totalProfit,
      avgProfit,
      maxProfit,
      minProfit,
      stdDev,
      variance,
      bestWeeks,
      worstWeeks,
      positiveWeeks,
      negativeWeeks,
      zeroWeeks,
      winRate,
      topStreams,
      weeksWithData,
      totalPossibleWeeks,
      totalStreams: allStreams.size,
      yearConsistency,
      weekTotals // Include for use in rendering
    }
  }, [data, yearData])

  if (!summary) {
    return (
      <div className="text-center py-4 text-gray-400">
        No data available for summary
      </div>
    )
  }

  return (
    <div className="space-y-4">
      {/* Overall Statistics */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Total Profit</div>
          <div className={`text-2xl font-bold ${summary.totalProfit >= 0 ? 'text-green-400' : 'text-red-400'}`}>
            {formatCurrency(summary.totalProfit)}
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Average per Week</div>
          <div className={`text-2xl font-bold ${summary.avgProfit >= 0 ? 'text-green-400' : 'text-red-400'}`}>
            {formatCurrency(summary.avgProfit)}
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Win Rate</div>
          <div className="text-2xl font-bold text-blue-400">
            {summary.winRate.toFixed(1)}%
          </div>
          <div className="text-xs text-gray-500 mt-1">
            {summary.positiveWeeks} profitable / {summary.weeksWithData} weeks
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Std Deviation</div>
          <div className="text-2xl font-bold text-yellow-400">
            {formatCurrency(summary.stdDev)}
          </div>
        </div>
      </div>

      {/* Best and Worst Weeks */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="bg-gray-800 rounded-lg p-4">
          <h3 className="text-lg font-semibold mb-3 text-green-400">Top 10 Best Weeks</h3>
          <div className="space-y-2 max-h-64 overflow-y-auto">
            {summary.bestWeeks.length > 0 ? (
              summary.bestWeeks.map((week, idx) => {
                const consistency = week.yearConsistency || {}
                const isLowSupport = consistency.totalYears > 0 && consistency.totalYears < 3
                return (
                  <div key={week.woy} className="flex justify-between items-center py-2 border-b border-gray-700">
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-medium">Week {week.woy}</span>
                        {week.woy === 53 && (
                          <span className="text-xs text-yellow-500">(rare)</span>
                        )}
                        {isLowSupport && (
                          <span className="text-xs text-gray-500">(low support)</span>
                        )}
                      </div>
                      {consistency.totalYears > 0 && (
                        <div className="text-xs text-gray-400 mt-1">
                          Profitable in {consistency.profitableYears} of {consistency.totalYears} years
                        </div>
                      )}
                    </div>
                    <div className="text-green-400 font-semibold">
                      {formatCurrency(week.total)}
                    </div>
                  </div>
                )
              })
            ) : (
              <div className="text-gray-400 text-sm">No profitable weeks</div>
            )}
          </div>
        </div>

        <div className="bg-gray-800 rounded-lg p-4">
          <h3 className="text-lg font-semibold mb-3 text-red-400">Top 10 Worst Weeks</h3>
          <div className="space-y-2 max-h-64 overflow-y-auto">
            {summary.worstWeeks.length > 0 ? (
              summary.worstWeeks.map((week, idx) => {
                const consistency = week.yearConsistency || {}
                const isLowSupport = consistency.totalYears > 0 && consistency.totalYears < 3
                return (
                  <div key={week.woy} className="flex justify-between items-center py-2 border-b border-gray-700">
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-medium">Week {week.woy}</span>
                        {week.woy === 53 && (
                          <span className="text-xs text-yellow-500">(rare)</span>
                        )}
                        {isLowSupport && (
                          <span className="text-xs text-gray-500">(low support)</span>
                        )}
                      </div>
                      {consistency.totalYears > 0 && (
                        <div className="text-xs text-gray-400 mt-1">
                          Unprofitable in {consistency.unprofitableYears} of {consistency.totalYears} years
                        </div>
                      )}
                    </div>
                    <div className="text-red-400 font-semibold">
                      {formatCurrency(week.total)}
                    </div>
                  </div>
                )
              })
            ) : (
              <div className="text-gray-400 text-sm">No losing weeks</div>
            )}
          </div>
        </div>
      </div>

      {/* Additional Statistics */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Best Week</div>
          <div className="text-xl font-bold text-green-400">
            {summary.bestWeeks.length > 0 
              ? `Week ${summary.bestWeeks[0].woy}`
              : 'N/A'}
          </div>
          <div className="text-xs text-gray-500 mt-1">
            {summary.bestWeeks.length > 0 ? formatCurrency(summary.bestWeeks[0].total) : ''}
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Worst Week</div>
          <div className="text-xl font-bold text-red-400">
            {summary.worstWeeks.length > 0 
              ? `Week ${summary.worstWeeks[0].woy}`
              : 'N/A'}
          </div>
          <div className="text-xs text-gray-500 mt-1">
            {summary.worstWeeks.length > 0 ? formatCurrency(summary.worstWeeks[0].total) : ''}
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Weeks with Data</div>
          <div className="text-xl font-bold text-blue-400">
            {summary.weeksWithData} / {summary.totalPossibleWeeks}
          </div>
          <div className="text-xs text-gray-500 mt-1">
            {((summary.weeksWithData / summary.totalPossibleWeeks) * 100).toFixed(1)}% coverage
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Active Streams</div>
          <div className="text-xl font-bold text-purple-400">
            {summary.totalStreams}
          </div>
        </div>
      </div>

      {/* Year Consistency Summary */}
      {Object.keys(summary.yearConsistency || {}).length > 0 && (
        <div className="bg-gray-800 rounded-lg p-4">
          <h3 className="text-lg font-semibold mb-3 text-cyan-400">Year Consistency Analysis</h3>
          <div className="text-sm text-gray-400 mb-3">
            Shows how many years each WOY was profitable vs unprofitable
          </div>
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3 max-h-96 overflow-y-auto">
            {Object.entries(summary.yearConsistency)
              .sort((a, b) => {
                // Sort by consistency rate (profitable years / total years), then by total years
                const aRate = a[1].totalYears > 0 ? a[1].profitableYears / a[1].totalYears : 0
                const bRate = b[1].totalYears > 0 ? b[1].profitableYears / b[1].totalYears : 0
                if (Math.abs(aRate - bRate) > 0.001) return bRate - aRate
                return b[1].totalYears - a[1].totalYears
              })
              .slice(0, 30)
              .map(([woy, consistency]) => {
                const woyNum = parseInt(woy)
                const isLowSupport = consistency.totalYears > 0 && consistency.totalYears < 3
                // Find the week total from the original data
                const weekTotalObj = Object.entries(data).find(([w]) => parseInt(w) === woyNum)
                const weekTotal = weekTotalObj ? (() => {
                  const streams = weekTotalObj[1]
                  if (!streams || typeof streams !== 'object') return null
                  let total = 0
                  Object.values(streams).forEach(profit => {
                    if (typeof profit === 'number') total += profit
                  })
                  return { total }
                })() : null
                return (
                  <div key={woy} className={`bg-gray-900 rounded p-3 ${isLowSupport ? 'opacity-75' : ''}`}>
                    <div className="flex items-center justify-between mb-2">
                      <div className="flex items-center gap-2">
                        <span className="font-medium">Week {woyNum}</span>
                        {woyNum === 53 && (
                          <span className="text-xs text-yellow-500">(rare)</span>
                        )}
                        {isLowSupport && (
                          <span className="text-xs text-gray-500">(low support)</span>
                        )}
                      </div>
                      {weekTotal && (
                        <span className={`text-sm font-semibold ${weekTotal.total >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                          {formatCurrency(weekTotal.total)}
                        </span>
                      )}
                    </div>
                    <div className="space-y-1 text-xs">
                      <div className="flex justify-between">
                        <span className="text-gray-400">Profitable:</span>
                        <span className="text-green-400 font-semibold">{consistency.profitableYears}</span>
                      </div>
                      <div className="flex justify-between">
                        <span className="text-gray-400">Unprofitable:</span>
                        <span className="text-red-400 font-semibold">{consistency.unprofitableYears}</span>
                      </div>
                      <div className="flex justify-between border-t border-gray-700 pt-1 mt-1">
                        <span className="text-gray-400">Total Years:</span>
                        <span className="text-blue-400 font-semibold">{consistency.totalYears}</span>
                      </div>
                      {consistency.totalYears > 0 && (
                        <div className="text-center pt-1 border-t border-gray-700 mt-1">
                          <span className="text-gray-400">Consistency: </span>
                          <span className={`font-semibold ${consistency.consistencyRate >= 50 ? 'text-green-400' : 'text-yellow-400'}`}>
                            {consistency.consistencyRate.toFixed(1)}%
                          </span>
                        </div>
                      )}
                    </div>
                  </div>
                )
              })}
          </div>
        </div>
      )}

      {/* Top Streams */}
      {summary.topStreams.length > 0 && (
        <div className="bg-gray-800 rounded-lg p-4">
          <h3 className="text-lg font-semibold mb-3">Top 5 Streams by Total Profit</h3>
          <div className="grid grid-cols-1 md:grid-cols-5 gap-3">
            {summary.topStreams.map(([stream, profit]) => (
              <div key={stream} className="bg-gray-900 rounded p-3">
                <div className="text-sm text-gray-400">{stream}</div>
                <div className={`text-lg font-semibold ${profit >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                  {formatCurrency(profit)}
                </div>
                <div className="text-xs text-gray-500 mt-1">
                  {((profit / summary.totalProfit) * 100).toFixed(1)}% of total
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
