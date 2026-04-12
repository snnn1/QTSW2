import { useMemo } from 'react'

export default function DOYSummary({ data, yearData = {} }) {
  const formatCurrency = (value) => {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 0,
      maximumFractionDigits: 0
    }).format(value)
  }

  const getOrdinalSuffix = (num) => {
    const j = num % 10
    const k = num % 100
    if (j === 1 && k !== 11) return 'st'
    if (j === 2 && k !== 12) return 'nd'
    if (j === 3 && k !== 13) return 'rd'
    return 'th'
  }

  const summary = useMemo(() => {
    if (!data || Object.keys(data).length === 0) {
      return null
    }

    // Calculate totals for each day of year
    const dayTotals = []
    const streamTotals = {}
    const allStreams = new Set()

    Object.entries(data).forEach(([doy, streams]) => {
      if (!streams || typeof streams !== 'object') return
      
      let dayTotal = 0
      Object.entries(streams).forEach(([stream, profit]) => {
        if (stream !== 'profit' && stream !== 'trades') {
          allStreams.add(stream)
          const profitValue = parseFloat(profit) || 0
          dayTotal += profitValue
          streamTotals[stream] = (streamTotals[stream] || 0) + profitValue
        }
      })
      
      dayTotals.push({
        doy: parseInt(doy),
        total: dayTotal,
        streams: streams
      })
    })

    // Sort by total profit
    dayTotals.sort((a, b) => b.total - a.total)

    // Calculate statistics
    const profits = dayTotals.map(d => d.total)
    const totalProfit = profits.reduce((sum, p) => sum + p, 0)
    const avgProfit = profits.length > 0 ? totalProfit / profits.length : 0
    const maxProfit = Math.max(...profits, 0)
    const minProfit = Math.min(...profits, 0)
    
    // Calculate variance and standard deviation
    const variance = profits.length > 0
      ? profits.reduce((sum, p) => sum + Math.pow(p - avgProfit, 2), 0) / profits.length
      : 0
    const stdDev = Math.sqrt(variance)

    // Best and worst days
    const bestDays = dayTotals.slice(0, 10).filter(d => d.total > 0)
    const worstDays = dayTotals.slice(-10).reverse().filter(d => d.total < 0)

    // Count positive/negative/zero days
    const positiveDays = profits.filter(p => p > 0).length
    const negativeDays = profits.filter(p => p < 0).length
    const zeroDays = profits.filter(p => p === 0).length

    // Calculate win rate (days with profit > 0)
    const winRate = profits.length > 0 ? (positiveDays / profits.length) * 100 : 0

    // Top streams by total profit
    const topStreams = Object.entries(streamTotals)
      .sort((a, b) => b[1] - a[1])
      .slice(0, 5)

    // Find days with data
    const daysWithData = dayTotals.length
    const totalPossibleDays = 366

    // Calculate year consistency for each DOY
    // yearData format: {doy: {year: total_profit}}
    const yearConsistency = {}
    Object.entries(yearData).forEach(([doy, years]) => {
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
      
      yearConsistency[parseInt(doy)] = {
        profitableYears,
        unprofitableYears,
        totalYears,
        consistencyRate: totalYears > 0 ? (profitableYears / totalYears) * 100 : 0
      }
    })

    // Add year consistency to day totals
    dayTotals.forEach(day => {
      day.yearConsistency = yearConsistency[day.doy] || {
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
      bestDays,
      worstDays,
      positiveDays,
      negativeDays,
      zeroDays,
      winRate,
      topStreams,
      daysWithData,
      totalPossibleDays,
      totalStreams: allStreams.size,
      yearConsistency,
      dayTotals // Include for use in rendering
    }
  }, [data])

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
          <div className="text-sm text-gray-400">Average per Day</div>
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
            {summary.positiveDays} profitable / {summary.daysWithData} days
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Std Deviation</div>
          <div className="text-2xl font-bold text-yellow-400">
            {formatCurrency(summary.stdDev)}
          </div>
        </div>
      </div>

      {/* Best and Worst Days */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="bg-gray-800 rounded-lg p-4">
          <h3 className="text-lg font-semibold mb-3 text-green-400">Top 10 Best Days</h3>
          <div className="space-y-2 max-h-64 overflow-y-auto">
            {summary.bestDays.length > 0 ? (
              summary.bestDays.map((day, idx) => {
                const consistency = day.yearConsistency || {}
                return (
                  <div key={day.doy} className="flex justify-between items-center py-2 border-b border-gray-700">
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-medium">{day.doy}{getOrdinalSuffix(day.doy)}</span>
                        <span className="text-xs text-gray-500">Day of Year</span>
                      </div>
                      {consistency.totalYears > 0 && (
                        <div className="text-xs text-gray-400 mt-1">
                          Profitable: {consistency.profitableYears} of {consistency.totalYears} years
                        </div>
                      )}
                    </div>
                    <div className="text-green-400 font-semibold">
                      {formatCurrency(day.total)}
                    </div>
                  </div>
                )
              })
            ) : (
              <div className="text-gray-400 text-sm">No profitable days</div>
            )}
          </div>
        </div>

        <div className="bg-gray-800 rounded-lg p-4">
          <h3 className="text-lg font-semibold mb-3 text-red-400">Top 10 Worst Days</h3>
          <div className="space-y-2 max-h-64 overflow-y-auto">
            {summary.worstDays.length > 0 ? (
              summary.worstDays.map((day, idx) => {
                const consistency = day.yearConsistency || {}
                return (
                  <div key={day.doy} className="flex justify-between items-center py-2 border-b border-gray-700">
                    <div className="flex-1">
                      <div className="flex items-center gap-2">
                        <span className="font-medium">{day.doy}{getOrdinalSuffix(day.doy)}</span>
                        <span className="text-xs text-gray-500">Day of Year</span>
                      </div>
                      {consistency.totalYears > 0 && (
                        <div className="text-xs text-gray-400 mt-1">
                          Unprofitable: {consistency.unprofitableYears} of {consistency.totalYears} years
                        </div>
                      )}
                    </div>
                    <div className="text-red-400 font-semibold">
                      {formatCurrency(day.total)}
                    </div>
                  </div>
                )
              })
            ) : (
              <div className="text-gray-400 text-sm">No losing days</div>
            )}
          </div>
        </div>
      </div>

      {/* Additional Statistics */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Best Day</div>
          <div className="text-xl font-bold text-green-400">
            {summary.bestDays.length > 0 
              ? `${summary.bestDays[0].doy}${getOrdinalSuffix(summary.bestDays[0].doy)}`
              : 'N/A'}
          </div>
          <div className="text-xs text-gray-500 mt-1">
            {summary.bestDays.length > 0 ? formatCurrency(summary.bestDays[0].total) : ''}
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Worst Day</div>
          <div className="text-xl font-bold text-red-400">
            {summary.worstDays.length > 0 
              ? `${summary.worstDays[0].doy}${getOrdinalSuffix(summary.worstDays[0].doy)}`
              : 'N/A'}
          </div>
          <div className="text-xs text-gray-500 mt-1">
            {summary.worstDays.length > 0 ? formatCurrency(summary.worstDays[0].total) : ''}
          </div>
        </div>
        <div className="bg-gray-800 rounded-lg p-4">
          <div className="text-sm text-gray-400">Days with Data</div>
          <div className="text-xl font-bold text-blue-400">
            {summary.daysWithData} / {summary.totalPossibleDays}
          </div>
          <div className="text-xs text-gray-500 mt-1">
            {((summary.daysWithData / summary.totalPossibleDays) * 100).toFixed(1)}% coverage
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
            Shows how many years each DOY was profitable vs unprofitable
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
              .map(([doy, consistency]) => {
                const doyNum = parseInt(doy)
                // Find the day total from the original data
                const dayTotalObj = Object.entries(data).find(([d]) => parseInt(d) === doyNum)
                const dayTotal = dayTotalObj ? (() => {
                  const streams = dayTotalObj[1]
                  if (!streams || typeof streams !== 'object') return null
                  let total = 0
                  Object.values(streams).forEach(profit => {
                    if (typeof profit === 'number') total += profit
                  })
                  return { total }
                })() : null
                return (
                  <div key={doy} className="bg-gray-900 rounded p-3">
                    <div className="flex items-center justify-between mb-2">
                      <span className="font-medium">{doyNum}{getOrdinalSuffix(doyNum)}</span>
                      {dayTotal && (
                        <span className={`text-sm font-semibold ${dayTotal.total >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                          {formatCurrency(dayTotal.total)}
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
