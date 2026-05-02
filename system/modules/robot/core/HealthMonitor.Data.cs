using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Core.Notifications;

namespace QTSW2.Robot.Core;

public sealed partial class HealthMonitor
{
    public void OnBar(string instrument, DateTimeOffset barUtc, decimal? close = null, long? volume = null)
    {
        var utcNow = DateTimeOffset.UtcNow;

        if (_lastBarUtcByInstrument.TryGetValue(instrument, out var prevBarUtc))
        {
            var intervalSec = (barUtc - prevBarUtc).TotalSeconds;
            if (intervalSec > 0 && intervalSec < 86400)
            {
                if (!_lastBarIntervals.TryGetValue(instrument, out var list))
                {
                    list = new List<double>();
                    _lastBarIntervals[instrument] = list;
                }
                list.Add(intervalSec);
                if (list.Count > BAR_INTERVAL_HISTORY_SIZE)
                    list.RemoveAt(0);
            }
        }

        if (close.HasValue) _lastClose[instrument] = close.Value;
        if (volume.HasValue) _lastVolume[instrument] = volume.Value;

        var prevBarUtcForRecovery = _lastBarUtcByInstrument.TryGetValue(instrument, out var pbu) ? pbu : (DateTimeOffset?)null;
        _lastBarUtcByInstrument[instrument] = barUtc;

        if (!_instrumentHasReceivedData.ContainsKey(instrument))
        {
            _instrumentHasReceivedData[instrument] = true;
            _instrumentFirstBarUtc[instrument] = barUtc;
        }
        if (_dataStallActive.TryGetValue(instrument, out var isActive) && isActive)
        {
            _dataStallActive[instrument] = false;
            _stallDetectedAtUtc.Remove(instrument);

            var expectedInterval = GetExpectedIntervalSeconds(instrument);
            var cooldownSec = Math.Max(expectedInterval * 2, 60);
            _recoveryCooldownUntil[instrument] = utcNow.AddSeconds(cooldownSec);

            var stallDuration = prevBarUtcForRecovery.HasValue ? (barUtc - prevBarUtcForRecovery.Value).TotalSeconds : 0;
            var barsMissedEstimate = expectedInterval > 0 ? (int)Math.Round(stallDuration / expectedInterval) : 0;

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "DATA_STALL_RECOVERED", state: "ENGINE",
                new
                {
                    instrument = instrument,
                    last_bar_utc = barUtc.ToString("o"),
                    stall_duration_seconds = Math.Round(stallDuration, 1),
                    bars_missed_estimate = barsMissedEstimate
                }));
        }
    }

    private double GetExpectedIntervalSeconds(string instrument)
    {
        if (!_lastBarIntervals.TryGetValue(instrument, out var list) || list.Count < 3)
            return 0;
        var sorted = new List<double>(list);
        sorted.Sort();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
    
    /// <summary>
    /// PHASE 3: Update engine heartbeat timestamp.
    /// </summary>
}
