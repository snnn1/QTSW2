using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using QTSW2.Robot.Core.Diagnostics;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core;

public sealed partial class RobotEngine
{
    /// <summary>NT build: persists <c>STREAM_COMMIT_PERSIST_FAILED</c> to KEY_EVENTS. Modules tree: no-op.</summary>
    public void TryAppendKeyEventStreamCommitPersistFailed(
        DateTimeOffset utcNow, string stream, string instrument, string commitReason, string eventType, string stateLabel)
    {
    }

    public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;
    private bool IsTerminalShutdownLatched() =>
        Volatile.Read(ref _shutdownRequested) != 0 ||
        Volatile.Read(ref _shutdownCompleted) != 0 ||
        (_isolatedPlaybackPersistence && RunRootArtifacts.HasRunShutdownSignal(_persistenceBase));

    private void LatchRunWideShutdownSignal(DateTimeOffset utcNow, string reason, string source)
    {
        if (!_isolatedPlaybackPersistence)
            return;

        RunRootArtifacts.WriteRunShutdownSignal(_persistenceBase, _runId, utcNow, reason, source);
    }

    private bool TryRespectRunWideShutdownSignal(DateTimeOffset utcNow, string source)
    {
        if (!_isolatedPlaybackPersistence || Volatile.Read(ref _shutdownCompleted) != 0)
            return false;
        if (!RunRootArtifacts.HasRunShutdownSignal(_persistenceBase))
            return false;

        if (Volatile.Read(ref _shutdownRequested) != 0)
            return true;

        if (Interlocked.Exchange(ref _runWideShutdownStopRequested, 1) != 0)
            return true;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "RUN_WIDE_SHUTDOWN_SIGNAL_OBSERVED", state: "ENGINE",
            new
            {
                source,
                note = "Observed shared run shutdown signal from another engine instance. Requesting local stop."
            }));

        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                ((RobotEngine)state!).Stop();
            }
            catch
            {
                // Best-effort follower stop; never throw on thread pool.
            }
        }, this);

        return true;
    }

    public void Stop(bool writeRunSummary = true, string stopSource = "direct")
    {
        var utcNow = DateTimeOffset.UtcNow;

        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            StopEngineHeartbeatTimer();
            return;
        }
        StopEngineHeartbeatTimer();
        _runtimeAudit?.EmitEngineAuditSummary(utcNow, TradingDateString);
        RuntimeAuditHubRef.Active = null;
        WaitForShutdownCallbackIngressQuiet();
        _ownershipEventJournal?.Flush(TimeSpan.FromMilliseconds(500), "engine_stop");

        string? summaryPathToWrite = null;
        string? summaryJson = null;

        ExecutionSummarySnapshot? summarySnap = null;
        var writeIsolatedRunSummary = false;
        string? isolatedRunId = null;
        var isolatedEngineStart = DateTimeOffset.MinValue;
        var isolatedPersistence = "";
        var isolatedRoot = "";
        var isolatedMode = ExecutionMode.DRYRUN;
        var isolatedInstruments = new List<string>();
        RobotLoggingService? loggingServiceToRelease = null;

        lock (_engineLock)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ENGINE_STOP", state: "ENGINE",
                new
                {
                    stop_source = stopSource,
                    write_run_summary = writeRunSummary
                }));

            var needSummarySnap = _executionMode != ExecutionMode.DRYRUN || _isolatedPlaybackPersistence;
            if (needSummarySnap)
            {
                summarySnap = _executionSummary.GetSnapshot();
                summarySnap = HydrateExecutionSummaryFromKeyEventsIfEmpty(_persistenceBase, summarySnap);
            }

            // Prepare execution summary if not DRYRUN (write to disk outside lock)
            if (_executionMode != ExecutionMode.DRYRUN && summarySnap != null)
            {
                var summaryDir = RobotRunArtifactPaths.DerivedExecutionSummaries(_persistenceBase);
                Directory.CreateDirectory(summaryDir);
                summaryPathToWrite = Path.Combine(summaryDir, $"summary_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
                summaryJson = JsonUtil.Serialize(summarySnap);
            }

            if (writeRunSummary && _isolatedPlaybackPersistence && summarySnap != null)
            {
                writeIsolatedRunSummary = true;
                isolatedRunId = _runId;
                isolatedEngineStart = _engineStartUtc;
                isolatedPersistence = _persistenceBase;
                isolatedRoot = _root;
                isolatedMode = _executionMode;
                isolatedInstruments.Clear();
                if (_spec?.instruments != null)
                {
                    isolatedInstruments.AddRange(_spec.instruments.Keys
                        .Select(k => k.ToUpperInvariant())
                        .OrderBy(x => x, StringComparer.Ordinal));
                }
            }

            // Stop health monitor
            _healthMonitor?.Stop();
            _stateEmitter?.Dispose();
            _stateEmitter = null;

            loggingServiceToRelease = _loggingService;
        }

        // Disk I/O outside engine lock
        if (!string.IsNullOrWhiteSpace(summaryPathToWrite) && summaryJson != null)
        {
            try
            {
                File.WriteAllText(summaryPathToWrite, summaryJson);
            }
            catch
            {
                // If summary write fails, do not throw during shutdown.
            }

            lock (_engineLock)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "EXECUTION_SUMMARY_WRITTEN", state: "ENGINE",
                    new { summary_path = summaryPathToWrite }));
            }
        }

        if (writeIsolatedRunSummary && summarySnap != null)
        {
            loggingServiceToRelease?.FlushNowForSummary();
            RunRootArtifacts.WriteRunSummaryJson(
                isolatedPersistence,
                isolatedRoot,
                isolatedRunId,
                isolatedEngineStart,
                isolatedMode,
                isolatedInstruments,
                summarySnap);
        }

        RunRootArtifacts.WriteAuditManifestJson(
            _persistenceBase,
            _runId,
            _engineStartUtc,
            TradingDateString,
            _isolatedPlaybackPersistence,
            FeatureFlags.CanonicalOwnershipLedgerEnabled,
            FeatureFlags.UnifiedExecutionAuthorityShadowEnabled,
            FeatureFlags.UnifiedExecutionAuthorityEnabled,
            FeatureFlags.ReconciliationRepairExecutorEnabled,
            FeatureFlags.StructuralLayerUseLedgerOwnership);

        loggingServiceToRelease?.FlushNowForSummary();
        loggingServiceToRelease?.Release();

        Volatile.Write(ref _shutdownCompleted, 1);
    }

    private void WaitForShutdownCallbackIngressQuiet()
    {
        if (!_isolatedPlaybackPersistence || _executionAdapter is not NinjaTraderSimAdapter simAdapter)
            return;

        var deadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(1500);
        var quietSamples = 0;
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            var execQueued = 0;
            var orderQueued = 0;
            try
            {
                simAdapter.GetTotalCallbackIngressQueueLengths(out execQueued, out orderQueued);
            }
            catch
            {
                return;
            }

            if (execQueued == 0 && orderQueued == 0)
            {
                quietSamples++;
                if (quietSamples >= 3)
                    return;
            }
            else
            {
                quietSamples = 0;
            }

            Thread.Sleep(100);
        }
    }

    private static ExecutionSummarySnapshot HydrateExecutionSummaryFromKeyEventsIfEmpty(string persistenceBase, ExecutionSummarySnapshot snapshot)
    {
        if (snapshot.IntentsSeen > 0 || snapshot.OrdersSubmitted > 0 || snapshot.OrdersFilled > 0 ||
            snapshot.OrdersRejected > 0 || snapshot.OrdersBlocked > 0)
            return snapshot;

        var keyEventsPath = Path.Combine(persistenceBase, RunRootArtifacts.KeyEventsFileName);
        if (!File.Exists(keyEventsPath))
            return snapshot;

        var intents = new Dictionary<string, IntentSummary>(StringComparer.OrdinalIgnoreCase);
        var blockedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var submitted = 0;
        var rejected = 0;
        var filled = 0;
        var blocked = 0;

        foreach (var raw in File.ReadLines(keyEventsPath))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var ev = TryGetJsonString(root, "event") ?? "";
                if (ev.Length == 0)
                    continue;

                var data = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object
                    ? dataEl
                    : default;
                var intentId = TryGetJsonString(data, "intent_id") ?? "";
                var stream = TryGetJsonString(root, "stream") ?? TryGetJsonString(data, "stream") ?? "";
                var instrument = TryGetJsonString(root, "instrument") ?? TryGetJsonString(data, "instrument") ?? "";
                var tradingDate = TryGetJsonString(data, "trading_date") ?? "";

                if (!string.IsNullOrWhiteSpace(intentId) && !intents.ContainsKey(intentId))
                {
                    intents[intentId] = new IntentSummary
                    {
                        IntentId = intentId,
                        TradingDate = tradingDate,
                        Stream = stream,
                        Instrument = instrument
                    };
                }

                if (string.Equals(ev, "ENTRY_FILLED", StringComparison.OrdinalIgnoreCase))
                {
                    filled++;
                    submitted++;
                    if (!string.IsNullOrWhiteSpace(intentId) && intents.TryGetValue(intentId, out var intent))
                    {
                        intent.Executed = true;
                        intent.OrdersFilled++;
                        intent.OrdersSubmitted++;
                        intent.OrderTypes.Add("ENTRY");
                    }
                }
                else if (string.Equals(ev, "PROTECTIVE_SUBMITTED", StringComparison.OrdinalIgnoreCase))
                {
                    submitted++;
                }
                else if (string.Equals(ev, "ENTRY_REJECTED", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(ev, "ORDER_REJECTED", StringComparison.OrdinalIgnoreCase))
                {
                    rejected++;
                    var reason = TryGetJsonString(root, "reason") ?? TryGetJsonString(data, "reason") ?? "rejected";
                    if (!string.IsNullOrWhiteSpace(intentId) && intents.TryGetValue(intentId, out var intent))
                    {
                        intent.OrdersRejected++;
                        intent.RejectionReasons.Add(reason);
                    }
                }
                else if (string.Equals(ev, "EXECUTION_BLOCKED", StringComparison.OrdinalIgnoreCase))
                {
                    blocked++;
                    var reason = TryGetJsonString(root, "reason") ?? TryGetJsonString(data, "reason") ?? "blocked";
                    if (!blockedByReason.TryGetValue(reason, out var count)) count = 0;
                    blockedByReason[reason] = count + 1;
                    if (!string.IsNullOrWhiteSpace(intentId) && intents.TryGetValue(intentId, out var intent))
                    {
                        intent.Blocked = true;
                        intent.BlockReason = reason;
                    }
                }
            }
            catch
            {
                // A malformed forensic line should not block shutdown summary writes.
            }
        }

        if (intents.Count == 0 && submitted == 0 && filled == 0 && rejected == 0 && blocked == 0)
            return snapshot;

        snapshot.IntentsSeen = intents.Count;
        snapshot.IntentsExecuted = intents.Values.Count(i => i.Executed);
        snapshot.OrdersSubmitted = submitted;
        snapshot.OrdersFilled = filled;
        snapshot.OrdersRejected = rejected;
        snapshot.OrdersBlocked = blocked;
        snapshot.BlockedByReason = blockedByReason;
        snapshot.IntentDetails = intents.Values.ToList();
        return snapshot;
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
}
