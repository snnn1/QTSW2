using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QTSW2.Robot.Core;

public sealed partial class RobotEngine
{
    private bool _playbackScenarioPreStartEventIgnoredLogged;

    private bool IsIsolatedPlaybackPersistenceRequested() =>
        _executionMode == ExecutionMode.SIM && (_requestedIgnoreExistingStreamJournals || _playbackScenarioActive);

    private void ApplyPlaybackScenarioTimetableOrThrow(DateTimeOffset eventUtc, string reason)
    {
        if (!_playbackScenarioActive)
            return;

        if (!TryActivatePlaybackScenarioTimetable(eventUtc, force: true, reason, out var error))
            throw new InvalidOperationException(error ?? "PLAYBACK_SCENARIO_TIMETABLE_UNAVAILABLE");
    }

    private bool TryActivatePlaybackScenarioTimetable(DateTimeOffset eventUtc, bool force, string reason, out string? error)
    {
        error = null;
        if (!_playbackScenarioActive || _playbackScenario == null)
            return false;

        var allowPreStartClamp = force && string.Equals(reason, "ENGINE_START", StringComparison.OrdinalIgnoreCase);
        if (!_playbackScenario.TryResolveScenarioDate(eventUtc, allowPreStartClamp, out var scenarioDate, out error, out var clampedPreStart))
        {
            if (string.Equals(reason, "EVENT_CLOCK_TICK", StringComparison.OrdinalIgnoreCase) &&
                IsPlaybackScenarioPreStartEvent(eventUtc, out var preStartDate, out var firstScenarioDate))
            {
                LogPlaybackScenarioPreStartEventIgnored(eventUtc, reason, preStartDate, firstScenarioDate);
                return false;
            }

            FailClosedPlaybackScenario(eventUtc, "PLAYBACK_SCENARIO_DATE_OUT_OF_RANGE", error ?? "");
            return false;
        }

        if (clampedPreStart)
        {
            LogEvent(RobotEvents.EngineBase(eventUtc, tradingDate: scenarioDate, eventType: "PLAYBACK_SCENARIO_START_ANCHOR_CLAMPED", state: "ENGINE",
                new
                {
                    scenario_id = _playbackScenario.scenario_id,
                    selected_scenario_date = scenarioDate,
                    event_session_date = PlaybackScenarioManifest.ComputeCmeSessionTradingDate(eventUtc),
                    event_utc = eventUtc.ToString("o"),
                    reason,
                    note = "NinjaTrader supplied a pre-scenario startup anchor during DataLoaded; engine selected the first scenario timetable and will ignore prestart historical events."
                }));
        }

        if (!force && string.Equals(_playbackScenarioActiveDate, scenarioDate, StringComparison.Ordinal))
            return false;

        if (!_playbackScenario.TryResolveTimetablePath(_root, scenarioDate, out var path, out var expectedHash, out error))
        {
            FailClosedPlaybackScenario(eventUtc, "PLAYBACK_SCENARIO_TIMETABLE_UNAVAILABLE", error ?? "");
            return false;
        }

        var previousDate = _playbackScenarioActiveDate;
        var previousPath = _timetablePath;
        _playbackScenarioActiveDate = scenarioDate;
        _playbackScenarioExpectedTimetableHash = expectedHash;
        _timetablePath = path;

        LogEvent(RobotEvents.EngineBase(eventUtc, tradingDate: TradingDateString, eventType: "PLAYBACK_SCENARIO_TIMETABLE_SELECTED", state: "ENGINE",
            new
            {
                scenario_id = _playbackScenario.scenario_id,
                scenario_date = scenarioDate,
                reason,
                previous_scenario_date = previousDate,
                previous_timetable_path = previousPath,
                timetable_path = path,
                expected_sha256 = expectedHash,
                note = "Event-clock multi-day playback selected a run-scoped replay timetable."
            }));

        PublishPlaybackScenarioRuntimeClock(
            eventUtc,
            scenarioDate,
            path,
            expectedHash,
            _playbackScenario.GetTimetableIdentityHash(scenarioDate),
            previousDate,
            previousPath,
            reason);

        return true;
    }

    private void PublishPlaybackScenarioRuntimeClock(
        DateTimeOffset eventUtc,
        string scenarioDate,
        string timetablePath,
        string? expectedFileSha256,
        string? timetableHash,
        string? previousDate,
        string? previousPath,
        string reason)
    {
        if (!_playbackScenarioActive || _playbackScenario == null)
            return;

        var activationKind = string.IsNullOrWhiteSpace(previousDate)
            ? "startup"
            : "playback_rollover";
        var clockPath = RobotRunArtifactPaths.RuntimeClockFile(_persistenceBase);
        string? tmpPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(clockPath) ?? _persistenceBase);
            var dto = new Dictionary<string, object?>
            {
                ["schema"] = "qtsw2.playback_runtime_clock.v1",
                ["source"] = "robot_playback_scenario",
                ["run_id"] = _runId ?? _playbackScenario.run_id ?? "",
                ["scenario_run_id"] = _playbackScenario.run_id ?? "",
                ["scenario_id"] = _playbackScenario.scenario_id,
                ["event_utc"] = eventUtc.ToString("o"),
                ["session_trading_date"] = scenarioDate,
                ["active_session_trading_date"] = scenarioDate,
                ["reason"] = reason,
                ["activation_kind"] = activationKind,
                ["timetable_path"] = timetablePath,
                ["timetable_hash"] = timetableHash ?? expectedFileSha256,
                ["timetable_file_sha256"] = expectedFileSha256,
                ["previous_session_trading_date"] = previousDate,
                ["previous_timetable_path"] = previousPath,
            };

            tmpPath = clockPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmpPath, JsonUtil.Serialize(dto));
            if (File.Exists(clockPath))
            {
                try
                {
                    File.Replace(tmpPath, clockPath, null);
                    tmpPath = null;
                }
                catch
                {
                    File.Delete(clockPath);
                    File.Move(tmpPath, clockPath);
                    tmpPath = null;
                }
            }
            else
            {
                File.Move(tmpPath, clockPath);
                tmpPath = null;
            }

            LogEvent(RobotEvents.EngineBase(eventUtc, tradingDate: scenarioDate, eventType: "SCENARIO_DAY_ACTIVE", state: "ENGINE",
                new
                {
                    scenario_id = _playbackScenario.scenario_id,
                    scenario_run_id = _playbackScenario.run_id,
                    run_id = _runId,
                    session_trading_date = scenarioDate,
                    active_session_trading_date = scenarioDate,
                    activation_kind = activationKind,
                    reason,
                    timetable_path = timetablePath,
                    timetable_hash = timetableHash ?? expectedFileSha256,
                    timetable_file_sha256 = expectedFileSha256,
                    previous_session_trading_date = previousDate,
                    runtime_clock_path = clockPath,
                    note = "Robot-published active playback scenario day for watchdog/operator timetable selection."
                }));
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(tmpPath))
            {
                try
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                }
                catch
                {
                    // Best-effort temp cleanup only.
                }
            }

            LogEvent(RobotEvents.EngineBase(eventUtc, tradingDate: scenarioDate, eventType: "SCENARIO_RUNTIME_CLOCK_WRITE_FAILED", state: "WARN",
                new
                {
                    scenario_id = _playbackScenario.scenario_id,
                    run_id = _runId,
                    session_trading_date = scenarioDate,
                    reason,
                    runtime_clock_path = clockPath,
                    error = ex.Message,
                    note = "Runtime-clock write is operator visibility only; robot continues with scenario timetable authority."
                }));
        }
    }

    private bool IsPlaybackScenarioPreStartEvent(DateTimeOffset eventUtc, out string eventSessionTradingDate, out string firstScenarioDate)
    {
        eventSessionTradingDate = "";
        firstScenarioDate = "";
        return _playbackScenarioActive &&
               _playbackScenario != null &&
               _playbackScenario.IsBeforeFirstScenarioDate(eventUtc, out eventSessionTradingDate, out firstScenarioDate);
    }

    private void LogPlaybackScenarioPreStartEventIgnored(
        DateTimeOffset eventUtc,
        string reason,
        string eventSessionTradingDate,
        string firstScenarioDate)
    {
        if (_playbackScenarioPreStartEventIgnoredLogged)
            return;

        _playbackScenarioPreStartEventIgnoredLogged = true;
        LogEvent(RobotEvents.EngineBase(eventUtc, tradingDate: firstScenarioDate, eventType: "PLAYBACK_SCENARIO_PRESTART_EVENT_IGNORED", state: "ENGINE",
            new
            {
                scenario_id = _playbackScenario?.scenario_id,
                event_session_date = eventSessionTradingDate,
                first_scenario_date = firstScenarioDate,
                event_utc = eventUtc.ToString("o"),
                reason,
                note = "Pre-scenario playback event ignored; waiting for event clock to reach the first configured scenario date."
            }));
    }

    private void FailClosedPlaybackScenario(DateTimeOffset utcNow, string eventType, string reason)
    {
        _playbackScenarioFailClosed = true;
        _playbackScenarioFailureReason = reason;
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: eventType, state: "CRITICAL",
            new
            {
                scenario_id = _playbackScenario?.scenario_id,
                reason,
                note = "Playback scenario cannot safely continue; engine will stand down."
            }));
    }

    private (int Retained, int Removed) RetainTimetableRolloverStreams(DateOnly previousDate, DateOnly newDate, DateTimeOffset utcNow)
    {
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: newDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_ROLLOVER_STARTED", state: "ENGINE",
            new
            {
                previous_trading_date = previousDate.ToString("yyyy-MM-dd"),
                new_trading_date = newDate.ToString("yyyy-MM-dd"),
                existing_stream_count = _streams.Count,
                playback_scenario = _playbackScenarioActive,
                note = "Timetable trading_date advanced; retaining nonterminal lifecycles before loading new-day rows."
            }));

        if (_streams.Count == 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: newDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_ROLLOVER_COMPLETED", state: "ENGINE",
                new
                {
                    previous_trading_date = previousDate.ToString("yyyy-MM-dd"),
                    new_trading_date = newDate.ToString("yyyy-MM-dd"),
                    retained_stream_count = 0,
                    removed_inactive_stream_count = 0,
                    retained_streams = Array.Empty<string>(),
                    playback_scenario = _playbackScenarioActive,
                    note = "Timetable rollover completed with no existing streams to retain."
                }));
            return (0, 0);
        }

        var retained = new Dictionary<string, StreamStateMachine>(StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var kvp in _streams.ToArray())
        {
            var stream = kvp.Value;
            if (!stream.HasTimetableRolloverRetentionEvidence)
            {
                removed++;
                continue;
            }

            var retainedFromTradingDate = stream.TradingDate;
            LogEvent(RobotEvents.Base(_time, utcNow, newDate.ToString("yyyy-MM-dd"), stream.Stream, stream.Instrument, stream.Session, stream.SlotTimeChicago, stream.SlotTimeUtc,
                "TIMETABLE_ROLLOVER_RETAINED_STREAM", "ENGINE",
                new
                {
                    previous_trading_date = previousDate.ToString("yyyy-MM-dd"),
                    new_trading_date = newDate.ToString("yyyy-MM-dd"),
                    retained_lifecycle_trading_date = retainedFromTradingDate,
                    stream_id = stream.Stream,
                    committed = stream.Committed,
                    slot_status = stream.SlotStatus.ToString(),
                    execution_interrupted_by_close = stream.ExecutionInterruptedByClose,
                    state = stream.State.ToString(),
                    playback_scenario = _playbackScenarioActive,
                    note = "Nonterminal stream lifecycle retained across timetable rollover."
                }));

            stream.UpdateTradingDate(newDate, utcNow, allowCarryForwardActive: true);
            retained[kvp.Key] = stream;
        }

        _streams.Clear();
        foreach (var kvp in retained)
            _streams[kvp.Key] = kvp.Value;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: newDate.ToString("yyyy-MM-dd"), eventType: "TIMETABLE_ROLLOVER_COMPLETED", state: "ENGINE",
            new
            {
                previous_trading_date = previousDate.ToString("yyyy-MM-dd"),
                new_trading_date = newDate.ToString("yyyy-MM-dd"),
                retained_stream_count = retained.Count,
                removed_inactive_stream_count = removed,
                retained_streams = retained.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                playback_scenario = _playbackScenarioActive,
                note = "Timetable rollover retention complete; new-day timetable rows may now be applied."
            }));

        if (_playbackScenarioActive)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: newDate.ToString("yyyy-MM-dd"), eventType: "PLAYBACK_SCENARIO_CARRYOVER_STREAMS_RETAINED", state: "ENGINE",
                new
                {
                    previous_trading_date = previousDate.ToString("yyyy-MM-dd"),
                    new_trading_date = newDate.ToString("yyyy-MM-dd"),
                    retained_stream_count = retained.Count,
                    removed_inactive_stream_count = removed,
                    retained_streams = retained.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
                    note = "Compatibility event for multi-day playback scenario rollover; TIMETABLE_ROLLOVER_* events are authoritative."
                }));
        }

        return (retained.Count, removed);
    }

    private void StandDownForPlaybackScenarioFailure(DateTimeOffset utcNow)
    {
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "PLAYBACK_SCENARIO_STANDDOWN", state: "CRITICAL",
            new
            {
                scenario_id = _playbackScenario?.scenario_id,
                reason = _playbackScenarioFailureReason,
                note = "Standing down after playback scenario guard failure."
            }));
        StandDown();
    }
}
