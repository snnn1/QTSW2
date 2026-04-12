/**
 * Matrix Metrics Dashboard
 * Displays performance metrics, stream health, and matrix diff tool.
 */

import { useState, useEffect, useCallback } from 'react'
import * as matrixApi from '../api/matrixApi'

function formatMs(ms) {
  if (ms == null) return '—'
  if (ms < 1000) return `${ms} ms`
  return `${(ms / 1000).toFixed(2)} s`
}

export default function MatrixMetricsDashboard() {
  const [metrics, setMetrics] = useState(null)
  const [streamHealth, setStreamHealth] = useState(null)
  const [files, setFiles] = useState([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)
  const [diffFileA, setDiffFileA] = useState('')
  const [diffFileB, setDiffFileB] = useState('')
  const [diffResult, setDiffResult] = useState(null)
  const [diffLoading, setDiffLoading] = useState(false)

  const fetchMetrics = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [metricsData, healthData, filesData] = await Promise.all([
        matrixApi.getPerformanceMetrics(),
        matrixApi.getStreamHealth().catch(() => ({ streams: [] })),
        matrixApi.listMatrixFiles().catch(() => ({ files: [] }))
      ])
      setMetrics(metricsData)
      setStreamHealth(healthData)
      setFiles(filesData.files || [])
    } catch (err) {
      setError(err.message)
      setMetrics(null)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    fetchMetrics()
    const interval = setInterval(fetchMetrics, 30000)
    return () => clearInterval(interval)
  }, [fetchMetrics])

  const runDiff = useCallback(async () => {
    if (!diffFileA || !diffFileB) return
    setDiffLoading(true)
    setDiffResult(null)
    try {
      const result = await matrixApi.diffMatrices(diffFileA, diffFileB)
      setDiffResult(result)
    } catch (err) {
      setDiffResult({ error: err.message })
    } finally {
      setDiffLoading(false)
    }
  }, [diffFileA, diffFileB])

  if (loading && !metrics) {
    return (
      <div className="bg-gray-900 rounded-lg p-6">
        <div className="text-center py-8 text-gray-400">Loading metrics...</div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="bg-gray-900 rounded-lg p-6">
        <div className="text-center py-8 text-red-400">
          <div className="mb-4">{error}</div>
          <button
            onClick={fetchMetrics}
            className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded"
          >
            Retry
          </button>
        </div>
      </div>
    )
  }

  const cards = [
    { label: 'Matrix size', value: metrics?.matrix_size != null ? metrics.matrix_size.toLocaleString() + ' rows' : '—' },
    { label: 'Rebuild time', value: formatMs(metrics?.rebuild_time_ms) },
    { label: 'Resequence time', value: formatMs(metrics?.resequence_time_ms) },
    { label: 'Matrix load time', value: formatMs(metrics?.matrix_load_time_ms) },
    { label: 'Matrix save time', value: formatMs(metrics?.matrix_save_time_ms) },
    { label: 'Timetable generation', value: formatMs(metrics?.timetable_time_ms) },
    {
      label: 'API cache hit rate',
      value: metrics?.api_cache_hit_rate != null ? `${metrics.api_cache_hit_rate}%` : '—',
      sub: metrics?.api_cache_hits != null && metrics?.api_cache_misses != null
        ? `(${metrics.api_cache_hits} hits / ${metrics.api_cache_misses} misses)`
        : null
    }
  ]

  return (
    <div className="space-y-6">
      <div className="bg-gray-900 rounded-lg p-6">
        <div className="flex items-center justify-between mb-6">
          <h2 className="text-xl font-semibold">Matrix Performance</h2>
          <button
            onClick={fetchMetrics}
            disabled={loading}
            className="px-4 py-2 rounded font-medium text-sm bg-gray-800 hover:bg-gray-700 disabled:opacity-50"
          >
            {loading ? 'Refreshing...' : 'Refresh'}
          </button>
        </div>
        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
          {cards.map(({ label, value, sub }) => (
            <div key={label} className="bg-gray-800 rounded-lg p-4">
              <div className="text-xs font-semibold text-gray-500 uppercase mb-1">{label}</div>
              <div className="text-lg font-semibold text-gray-200">{value}</div>
              {sub && <div className="text-xs text-gray-500 mt-1">{sub}</div>}
            </div>
          ))}
        </div>
      </div>

      {/* Stream Health */}
      <div className="bg-gray-900 rounded-lg p-6">
        <h2 className="text-xl font-semibold mb-4">Stream Health</h2>
        {streamHealth?.streams?.length > 0 ? (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="text-left text-gray-500 border-b border-gray-700">
                  <th className="py-2">Stream</th>
                  <th className="py-2">Win Rate</th>
                  <th className="py-2">Total Profit</th>
                  <th className="py-2">Max Drawdown</th>
                  <th className="py-2">Executed</th>
                  <th className="py-2">RS (recent)</th>
                </tr>
              </thead>
              <tbody>
                {streamHealth.streams.map((s) => (
                  <tr key={s.stream_id} className="border-b border-gray-800">
                    <td className="py-2 font-medium">{s.stream_id}</td>
                    <td className="py-2">{s.win_rate != null ? `${(s.win_rate * 100).toFixed(1)}%` : '—'}</td>
                    <td className="py-2">{s.total_profit != null ? s.total_profit : '—'}</td>
                    <td className="py-2">{s.max_drawdown != null ? s.max_drawdown : '—'}</td>
                    <td className="py-2">{s.executed_trades ?? '—'}</td>
                    <td className="py-2">{s.rs_value_recent != null ? s.rs_value_recent.toFixed(1) : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="text-gray-500">No stream data available</div>
        )}
      </div>

      {/* Matrix Diff */}
      <div className="bg-gray-900 rounded-lg p-6">
        <h2 className="text-xl font-semibold mb-4">Matrix Diff</h2>
        <p className="text-sm text-gray-400 mb-4">Compare two matrix files. Highlight RS, Time, and Profit differences.</p>
        <div className="flex flex-wrap gap-4 items-end mb-4">
          <div>
            <label className="block text-xs text-gray-500 mb-1">File A</label>
            <select
              value={diffFileA}
              onChange={(e) => setDiffFileA(e.target.value)}
              className="bg-gray-800 border border-gray-700 rounded px-3 py-2 min-w-[200px]"
            >
              <option value="">Select...</option>
              {files.map((f) => (
                <option key={f.name} value={f.name}>{f.name}</option>
              ))}
            </select>
          </div>
          <div>
            <label className="block text-xs text-gray-500 mb-1">File B</label>
            <select
              value={diffFileB}
              onChange={(e) => setDiffFileB(e.target.value)}
              className="bg-gray-800 border border-gray-700 rounded px-3 py-2 min-w-[200px]"
            >
              <option value="">Select...</option>
              {files.map((f) => (
                <option key={f.name} value={f.name}>{f.name}</option>
              ))}
            </select>
          </div>
          <button
            onClick={runDiff}
            disabled={!diffFileA || !diffFileB || diffLoading}
            className="px-4 py-2 bg-blue-600 hover:bg-blue-700 rounded disabled:opacity-50"
          >
            {diffLoading ? 'Comparing...' : 'Compare'}
          </button>
        </div>
        {diffResult && (
          <div className="mt-4">
            {diffResult.error ? (
              <div className="text-red-400">{diffResult.error}</div>
            ) : (
              <>
                <div className="text-sm text-gray-400 mb-2">
                  {diffResult.file_a} vs {diffResult.file_b} — {diffResult.total_differences} differences
                  (showing up to 500)
                </div>
                {diffResult.differences?.length > 0 ? (
                  <div className="overflow-x-auto max-h-64 overflow-y-auto">
                    <table className="w-full text-sm">
                      <thead>
                        <tr className="text-left text-gray-500 border-b border-gray-700 sticky top-0 bg-gray-900">
                          <th className="py-2">Stream</th>
                          <th className="py-2">Date</th>
                          <th className="py-2">Differences</th>
                        </tr>
                      </thead>
                      <tbody>
                        {diffResult.differences.map((d, i) => (
                          <tr key={i} className="border-b border-gray-800">
                            <td className="py-2">{d.stream}</td>
                            <td className="py-2">{d.trade_date}</td>
                            <td className="py-2">
                              <pre className="text-xs whitespace-pre-wrap">{JSON.stringify(d.differences, null, 2)}</pre>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <div className="text-green-400">No differences found</div>
                )}
              </>
            )}
          </div>
        )}
      </div>
    </div>
  )
}
