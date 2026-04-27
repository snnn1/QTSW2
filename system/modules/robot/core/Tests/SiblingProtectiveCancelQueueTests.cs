// Regression: sibling protective cancel must use urgent NT action lane so strategy-thread
// drain runs it before normal deferred work (partial STOP/TARGET fill safety).
// Run (when modules/robot/core builds): dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SIBLING_PROTECTIVE_CANCEL_QUEUE
// Runnable alternate (NT tree): dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Tests;

public static class SiblingProtectiveCancelQueueTests
{
    sealed class RecordingExecutor : INtActionExecutor, INtMarketReentryExecutionGate
    {
        private readonly List<string> _trace;
        private readonly Dictionary<string, string> _activeReentryByInstrument = new(StringComparer.OrdinalIgnoreCase);
        public RecordingExecutor(List<string> trace) => _trace = trace;

        public void ExecuteCancelOrders(NtCancelOrdersCommand cmd) =>
            _trace.Add($"CANCEL:{cmd.CorrelationId}:{cmd.Reason}");

        public void ExecuteSubmitProtectives(NtSubmitProtectivesCommand cmd) => _trace.Add("SUBMIT_PROTECTIVES");
        public void ExecuteFlattenInstrument(NtFlattenInstrumentCommand cmd) => _trace.Add("FLATTEN");
        public void ExecuteSubmitEntryIntent(NtSubmitEntryIntentCommand cmd) => _trace.Add("ENTRY");
        public void ExecuteSubmitMarketReentry(NtSubmitMarketReentryCommand cmd) => _trace.Add($"REENTRY:{cmd.CorrelationId}");

        public bool TryBeginMarketReentryExecution(
            NtSubmitMarketReentryCommand cmd,
            DateTimeOffset utcNow,
            out string deferReason,
            out string? activeIntentId)
        {
            deferReason = "";
            activeIntentId = null;

            var instrument = NormalizeInstrument(cmd);
            var intentId = cmd.IntentId ?? cmd.Command.ReentryIntentId ?? "";
            if (string.IsNullOrEmpty(instrument) || string.IsNullOrEmpty(intentId))
                return true;

            if (_activeReentryByInstrument.TryGetValue(instrument, out var active) &&
                !string.Equals(active, intentId, StringComparison.OrdinalIgnoreCase))
            {
                deferReason = "active_reentry_waiting_for_protection";
                activeIntentId = active;
                return false;
            }

            _activeReentryByInstrument[instrument] = intentId;
            return true;
        }

        public void ReleaseMarketReentryExecution(NtSubmitMarketReentryCommand cmd, DateTimeOffset utcNow, string reason)
        {
            var instrument = NormalizeInstrument(cmd);
            var intentId = cmd.IntentId ?? cmd.Command.ReentryIntentId ?? "";
            if (!string.IsNullOrEmpty(instrument) &&
                _activeReentryByInstrument.TryGetValue(instrument, out var active) &&
                string.Equals(active, intentId, StringComparison.OrdinalIgnoreCase))
            {
                _activeReentryByInstrument.Remove(instrument);
            }
        }

        public void MarkReentryProtectionAccepted(NtSubmitMarketReentryCommand cmd, DateTimeOffset utcNow) =>
            ReleaseMarketReentryExecution(cmd, utcNow, "REENTRY_PROTECTION_ACCEPTED");

        private static string NormalizeInstrument(NtSubmitMarketReentryCommand cmd) =>
            (cmd.InstrumentKey ?? cmd.Command.ExecutionInstrument ?? cmd.Command.Instrument ?? "").Trim().ToUpperInvariant();
    }

    private static NtSubmitMarketReentryCommand ReentryAction(string correlationId, string intentId, DateTimeOffset utc) =>
        new(correlationId, new SubmitMarketReentryCommand
        {
            Instrument = "MNG",
            ExecutionInstrument = "MNG",
            ReentryIntentId = intentId,
            OriginalIntentId = "original-" + intentId,
            Stream = intentId.EndsWith("1", StringComparison.Ordinal) ? "NG1" : "NG2",
            Direction = "short",
            Quantity = 2,
            Reason = "SESSION_REENTRY_ATTEMPT",
            TimestampUtc = utc
        });

    public static (bool Pass, string? Error) RunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "SiblingProtectiveCancelQueue_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var utc = DateTimeOffset.Parse("2026-04-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

            var trace = new List<string>();

            // 1) Normal work enqueued first; sibling urgent enqueued second — urgent must still run first.
            var exec = new StrategyThreadExecutor(log, () => utc);
            if (!exec.EnqueueNtAction(new NtDeferredAction("norm-1", null, null, "test", () => trace.Add("DEFER:norm-1")), out var dupNorm1) || dupNorm1)
                return (false, "expected norm-1 enqueue");
            var sibling = new NtCancelOrdersCommand(
                "sib-1",
                "intent-partial-stop-2lot",
                null,
                true,
                NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill,
                utc,
                preferUrgentDrain: true);
            if (!exec.EnqueueNtAction(sibling, out var dupSib) || dupSib)
                return (false, "expected sib-1 enqueue");
            if (!exec.EnqueueNtAction(new NtDeferredAction("norm-2", null, null, "test", () => trace.Add("DEFER:norm-2")), out var dupNorm2) || dupNorm2)
                return (false, "expected norm-2 enqueue");

            var recorder = new RecordingExecutor(trace);
            exec.DrainNtActions(recorder);

            if (trace.Count != 3)
                return (false, $"expected 3 trace entries, got {trace.Count}: {string.Join(" | ", trace)}");

            if (trace[0] != "CANCEL:sib-1:" + NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill)
                return (false, $"first must be urgent sibling cancel, got [{trace[0]}]");

            if (trace[1] != "DEFER:norm-1" || trace[2] != "DEFER:norm-2")
                return (false, $"after sibling cancel, normal FIFO must hold: {string.Join(" | ", trace)}");

            // 2) Urgent lane preserves FIFO among urgent commands.
            var trace2 = new List<string>();
            var exec2 = new StrategyThreadExecutor(log, () => utc);
            if (!exec2.EnqueueNtAction(new NtCancelOrdersCommand("u1", "i", null, true, NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill, utc, preferUrgentDrain: true), out _))
                return (false, "u1 enqueue");
            if (!exec2.EnqueueNtAction(new NtCancelOrdersCommand("u2", "i", null, true, NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill, utc, preferUrgentDrain: true), out _))
                return (false, "u2 enqueue");
            exec2.DrainNtActions(new RecordingExecutor(trace2));
            if (trace2.Count != 2 ||
                trace2[0] != "CANCEL:u1:" + NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill ||
                trace2[1] != "CANCEL:u2:" + NtCancelOrdersCommand.ReasonSiblingProtectiveExitFill)
                return (false, $"urgent FIFO: {string.Join(" | ", trace2)}");

            // 3) Normal-only queue is FIFO.
            var trace3 = new List<string>();
            var exec3 = new StrategyThreadExecutor(log, () => utc);
            if (!exec3.EnqueueNtAction(new NtDeferredAction("n1", null, null, "t", () => trace3.Add("D1")), out _)) return (false, "n1");
            if (!exec3.EnqueueNtAction(new NtDeferredAction("n2", null, null, "t", () => trace3.Add("D2")), out _)) return (false, "n2");
            exec3.DrainNtActions(new RecordingExecutor(trace3));
            if (trace3.Count != 2 || trace3[0] != "D1" || trace3[1] != "D2")
                return (false, $"normal-only FIFO: {string.Join(" | ", trace3)}");

            // 4) Market reentry submit pauses the drain and same-instrument siblings stay
            // queued until the active reentry reaches protection accepted or fails.
            var trace4 = new List<string>();
            var exec4 = new StrategyThreadExecutor(log, () => utc);
            var reentry1 = ReentryAction("r1", "intent-1", utc);
            var reentry2 = ReentryAction("r2", "intent-2", utc);
            if (!exec4.EnqueueNtAction(reentry1, out _))
                return (false, "r1 enqueue");
            if (!exec4.EnqueueNtAction(reentry2, out _))
                return (false, "r2 enqueue");
            var recorder4 = new RecordingExecutor(trace4);
            exec4.DrainNtActions(recorder4);
            if (trace4.Count != 1 || trace4[0] != "REENTRY:r1")
                return (false, $"first reentry drain must execute exactly one market reentry: {string.Join(" | ", trace4)}");
            if (exec4.PendingCount != 1 || exec4.NormalPendingCount != 1)
                return (false, $"second reentry must remain queued after pause, pending={exec4.PendingCount}, normal={exec4.NormalPendingCount}");

            exec4.DrainNtActions(recorder4);
            if (trace4.Count != 1 || exec4.PendingCount != 1 || exec4.NormalPendingCount != 1)
                return (false, $"second reentry must remain queued before protection accepted: trace={string.Join(" | ", trace4)}, pending={exec4.PendingCount}, normal={exec4.NormalPendingCount}");

            recorder4.MarkReentryProtectionAccepted(reentry1, utc);
            exec4.DrainNtActions(recorder4);
            if (trace4.Count != 2 || trace4[1] != "REENTRY:r2")
                return (false, $"second reentry must execute after first protection accepted: {string.Join(" | ", trace4)}");

            if (exec.PendingCount != 0 || exec2.PendingCount != 0 || exec3.PendingCount != 0 || exec4.PendingCount != 0)
                return (false, "queues must be empty after drain");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* temp */ }
        }
    }
}
