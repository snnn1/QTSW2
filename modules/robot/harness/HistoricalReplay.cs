using System.Linq;
using System.Text.Json;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Harness;

/// <summary>
/// Historical bar replay for DRYRUN mode.
/// Replays bars from snapshot parquet files in chronological order.
/// </summary>
public static class HistoricalReplay
{
    /// <summary>
    /// Replay historical bars for a date range using snapshot data.
    /// </summary>
    public static void Replay(
        RobotEngine engine,
        ParitySpec spec,
        TimeService timeService,
        string snapshotRoot,
        string projectRoot,
        DateOnly startDate,
        DateOnly endDate,
        string[]? instruments = null
    )
    {
        var barProvider = new SnapshotParquetBarProvider(snapshotRoot, timeService);

        // Get instruments to process
        var instrumentsToProcess = instruments ?? spec.Instruments.Keys.ToArray();

        // Process each trading date
        var currentDate = startDate;
        while (currentDate <= endDate)
        {
            // Skip weekends (basic check - can be enhanced)
            if (currentDate.DayOfWeek == DayOfWeek.Saturday || currentDate.DayOfWeek == DayOfWeek.Sunday)
            {
                currentDate = currentDate.AddDays(1);
                continue;
            }

            // Update timetable trading_date for this date if using replay timetable
            // The engine will reload the timetable when we tick with the historical date
            UpdateTimetableTradingDateIfReplay(projectRoot, currentDate, timeService);

            // Force engine to reload timetable for this date
            var dateUtc = timeService.ConvertChicagoLocalToUtc(currentDate, "00:00");
            engine.Tick(dateUtc); // This will trigger timetable reload

            // Collect all bars for this date across all instruments
            var dateBars = new List<(Bar bar, string instrument)>();

            foreach (var instrument in instrumentsToProcess)
            {
                // Calculate time range for this date
                // Need bars from earliest range start to forced flatten time
                var earliestRangeStart = spec.Sessions.Values.Min(s => s.RangeStartTime);
                if (string.IsNullOrEmpty(earliestRangeStart))
                {
                    continue; // Skip if no valid range start time
                }
                var rangeStartUtc = timeService.ConvertChicagoLocalToUtc(currentDate, earliestRangeStart);
                
                // Forced flatten time (5 minutes before market close)
                var forcedFlattenUtc = timeService.ConvertChicagoLocalToUtc(currentDate, "15:55"); // 5 min before 16:00 CT
                
                // Get bars for this date range
                var bars = barProvider.GetBars(instrument, rangeStartUtc, forcedFlattenUtc.AddHours(1))
                    .ToList();

                foreach (var bar in bars)
                {
                    dateBars.Add((bar, instrument));
                }
            }

            // Sort all bars by timestamp (across all instruments)
            dateBars.Sort((a, b) => a.bar.TimestampUtc.CompareTo(b.bar.TimestampUtc));

            // Feed bars to engine in chronological order
            foreach (var (bar, instrument) in dateBars)
            {
                var barUtc = bar.TimestampUtc;
                
                // Feed bar to engine
                engine.OnBar(barUtc, instrument, bar.High, bar.Low, bar.Close, barUtc);
                
                // Tick engine at bar time
                engine.Tick(barUtc);
            }

            // Final tick at end of day to ensure all state transitions complete
            var endOfDayUtc = timeService.ConvertChicagoLocalToUtc(currentDate, "16:00");
            engine.Tick(endOfDayUtc);

            currentDate = currentDate.AddDays(1);
        }
    }

    private static void UpdateTimetableTradingDateIfReplay(string projectRoot, DateOnly tradingDate, TimeService timeService)
    {
        // Check if replay timetable exists
        var replayTimetablePath = Path.Combine(projectRoot, "data", "timetable", "timetable_replay.json");
        if (!File.Exists(replayTimetablePath))
        {
            // No replay timetable - use dynamic creation (backward compatibility)
            var timetablePath = Path.Combine(projectRoot, "data", "timetable", "timetable_current.json");
            CreateTimetableForDate(timetablePath, tradingDate, timeService);
            return;
        }

        // Update replay timetable trading_date for this date
        try
        {
            var timetable = TimetableContract.LoadFromFile(replayTimetablePath);
            
            // Create updated timetable with new trading_date
            var updatedTimetable = new
            {
                as_of = timetable.AsOf,
                trading_date = tradingDate.ToString("yyyy-MM-dd"),
                timezone = timetable.Timezone,
                source = timetable.Source,
                metadata = timetable.Metadata != null ? new { replay = timetable.Metadata.Replay } : (object?)null,
                streams = timetable.Streams.Select(s => new
                {
                    stream = s.Stream,
                    instrument = s.Instrument,
                    session = s.Session,
                    slot_time = s.SlotTime,
                    enabled = s.Enabled
                }).ToArray()
            };

            // Write updated timetable (this will trigger engine reload on next tick)
            var json = JsonSerializer.Serialize(updatedTimetable, ParitySpec.JsonOptions());
            File.WriteAllText(replayTimetablePath, json);
        }
        catch
        {
            // If replay timetable is invalid, fall back to dynamic creation
            var timetablePath = Path.Combine(projectRoot, "data", "timetable", "timetable_current.json");
            CreateTimetableForDate(timetablePath, tradingDate, timeService);
        }
    }

    private static void CreateTimetableForDate(string timetablePath, DateOnly tradingDate, TimeService timeService)
    {
        // This method is kept for backward compatibility when no replay timetable exists
        // It creates a minimal timetable - should not be used when replay timetable is available
        var utcNow = DateTimeOffset.UtcNow;
        var chicagoNow = timeService.GetChicagoNow(utcNow);

        var timetable = new
        {
            as_of = chicagoNow.ToString("o"),
            trading_date = tradingDate.ToString("yyyy-MM-dd"),
            timezone = "America/Chicago",
            source = "parity_replay_dynamic",
            streams = Array.Empty<object>()
        };

        Directory.CreateDirectory(Path.GetDirectoryName(timetablePath)!);
        var json = JsonSerializer.Serialize(timetable, ParitySpec.JsonOptions());
        File.WriteAllText(timetablePath, json);
    }
}
