// SINGLE SOURCE OF TRUTH
// This file is the authoritative implementation of RobotLoggingService.
// It is compiled into Robot.Core.dll and should be referenced from that DLL.
// Do not duplicate this file elsewhere - if source is needed, reference Robot.Core.dll instead.
//
// Linked into: Robot.Core.csproj (modules/robot/core/)
// Referenced by: RobotCore_For_NinjaTrader (via Robot.Core.dll)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QTSW2.Robot.Core;

public sealed partial class RobotLoggingService
{
    private sealed class DailySummaryAggregator
    {
        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public long TotalEvents { get; private set; }
        public long Errors { get; private set; }
        public long Warnings { get; private set; }

        public long RangeEvents { get; private set; }
        public long RangeLocked { get; private set; }

        public long OrdersSubmitted { get; private set; }
        public long ExecutionsFilled { get; private set; }
        public long OrdersRejected { get; private set; }

        public long PushoverEvents { get; private set; }
        public long LoggingPipelineErrors { get; private set; } // LOG_* events

        public readonly Dictionary<string, long> OrderTypeCounts = new(StringComparer.OrdinalIgnoreCase);

        private RobotLogEvent? _latestError;
        private RobotLogEvent? _latestOrderSubmitted;
        private RobotLogEvent? _latestRangeLocked;
        private RobotLogEvent? _latestPushover;
        private RobotLogEvent? _latestLoggingPipelineError;

        public void Observe(RobotLogEvent evt)
        {
            TotalEvents++;

            if (string.Equals(evt.level, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(evt.level, "CRITICAL", StringComparison.OrdinalIgnoreCase))
            {
                Errors++;
                _latestError = evt;
            }
            else if (string.Equals(evt.level, "WARN", StringComparison.OrdinalIgnoreCase))
            {
                Warnings++;
            }

            var name = evt.@event ?? "";

            if (name.StartsWith("LOG_", StringComparison.OrdinalIgnoreCase))
            {
                LoggingPipelineErrors++;
                _latestLoggingPipelineError = evt;
            }

            if (name.StartsWith("RANGE_", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "RANGE_LOCKED", StringComparison.OrdinalIgnoreCase))
            {
                RangeEvents++;
                if (string.Equals(name, "RANGE_LOCKED", StringComparison.OrdinalIgnoreCase))
                {
                    RangeLocked++;
                    _latestRangeLocked = evt;
                }
            }

            if (string.Equals(name, "ORDER_SUBMITTED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ORDER_SUBMIT_SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                OrdersSubmitted++;
                _latestOrderSubmitted = evt;

                // Best-effort order_type breakdown
                if (evt.data != null && evt.data.TryGetValue("order_type", out var otObj))
                {
                    var ot = otObj?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(ot))
                    {
                        if (!OrderTypeCounts.TryGetValue(ot, out var cur)) cur = 0;
                        OrderTypeCounts[ot] = cur + 1;
                    }
                }
            }

            if (string.Equals(name, "EXECUTION_FILLED", StringComparison.OrdinalIgnoreCase))
            {
                ExecutionsFilled++;
            }

            if (string.Equals(name, "ORDER_REJECTED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ORDER_SUBMIT_FAIL", StringComparison.OrdinalIgnoreCase))
            {
                OrdersRejected++;
            }

            if (name.StartsWith("PUSHOVER_", StringComparison.OrdinalIgnoreCase))
            {
                PushoverEvents++;
                _latestPushover = evt;
            }
        }

        private static string Compact(string? s, int maxLen = 160)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }

        private static string Fmt(RobotLogEvent? e)
        {
            if (e == null) return "N/A";
            return $"{e.ts_utc} | {e.instrument} | {e.@event} | {Compact(e.message)}";
        }

        public string RenderMarkdown(string logDir, long droppedDebug, long droppedInfo, int writeFailures)
        {
            var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var date = DateTime.UtcNow.ToString("yyyyMMdd");

            var sb = new StringBuilder();
            sb.AppendLine($"# Robot Daily Log Summary ({date})");
            sb.AppendLine();
            sb.AppendLine($"Generated: {nowUtc} UTC");
            sb.AppendLine();
            sb.AppendLine("## Location");
            sb.AppendLine($"- log_dir: `{logDir}`");
            sb.AppendLine();
            sb.AppendLine("## Health");
            sb.AppendLine($"- total_events: {TotalEvents}");
            sb.AppendLine($"- errors: {Errors}");
            sb.AppendLine($"- warnings: {Warnings}");
            sb.AppendLine($"- dropped_debug: {droppedDebug}");
            sb.AppendLine($"- dropped_info: {droppedInfo}");
            sb.AppendLine($"- write_failures: {writeFailures}");
            sb.AppendLine();
            sb.AppendLine("## Latest notable events");
            sb.AppendLine($"- latest_error: {Fmt(_latestError)}");
            sb.AppendLine($"- latest_logging_pipeline_error: {Fmt(_latestLoggingPipelineError)}");
            sb.AppendLine();
            sb.AppendLine("## Ranges");
            sb.AppendLine($"- range_events: {RangeEvents}");
            sb.AppendLine($"- range_locked: {RangeLocked}");
            sb.AppendLine($"- latest_range_locked: {Fmt(_latestRangeLocked)}");
            sb.AppendLine();
            sb.AppendLine("## Orders");
            sb.AppendLine($"- orders_submitted: {OrdersSubmitted}");
            sb.AppendLine($"- orders_rejected: {OrdersRejected}");
            sb.AppendLine($"- executions_filled: {ExecutionsFilled}");
            sb.AppendLine($"- latest_order_submitted: {Fmt(_latestOrderSubmitted)}");
            if (OrderTypeCounts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### Order types");
                foreach (var kv in OrderTypeCounts.OrderByDescending(kv => kv.Value))
                {
                    sb.AppendLine($"- {kv.Key}: {kv.Value}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("## Notifications");
            sb.AppendLine($"- pushover_events: {PushoverEvents}");
            sb.AppendLine($"- latest_pushover: {Fmt(_latestPushover)}");

            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine("- This file is maintained by the logging background worker (non-blocking).");
            sb.AppendLine("- For full details, grep the JSONL files in `logs/robot/`.");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Get or create the singleton instance for the given project root.
    /// Multiple engines share the same instance to prevent file lock contention.
    /// </summary>
    /// <param name="configRoot">Repo root for <see cref="LoggingConfig"/> (<c>configs/robot/logging.json</c>).</param>
    /// <param name="robotLogDirectory">Absolute run-authoritative <c>logs/robot</c> directory (typically <see cref="RobotRunArtifactPaths.LogsRobot"/>).</param>
}
