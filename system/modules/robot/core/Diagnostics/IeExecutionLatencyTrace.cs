// Opt-in JSONL forensics for IEA execution path timing (strategy → enqueue → worker → resolve).
// Enable: set environment variable QTSW2_IEA_EXEC_LATENCY_TRACE=1 before starting NinjaTrader / strategy.
// Output: <run artifact root>/logs/robot/iea_execution_latency.jsonl (append-only, thread-safe). Uses QTSW2_ROBOT_PERSISTENCE_BASE when set.

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Diagnostics;

public static class IeExecutionLatencyTrace
{
    private static readonly object IoLock = new();
    private static string? _path;

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("QTSW2_IEA_EXEC_LATENCY_TRACE"), "1",
            StringComparison.Ordinal);

    private static string EnsurePath()
    {
        if (_path != null) return _path;
        lock (IoLock)
        {
            if (_path != null) return _path;
            var root = ProjectRootResolver.ResolveRunArtifactRoot();
            var dir = RobotRunArtifactPaths.LogsRobot(root);
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "iea_execution_latency.jsonl");
            return _path;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <param name="phase">STRATEGY_ON_ORDER_UPDATE_FILLED | STRATEGY_ON_EXECUTION_UPDATE | ADAPTER_EXEC_ENQUEUE | IEA_WORKER_EXEC_BEGIN | ORDER_REGISTRY_EXEC_RESOLVED</param>
    /// <param name="order">NinjaTrader Order (object to avoid NT type in net8 module — dynamic access)</param>
    /// <param name="execution">NinjaTrader Execution (optional)</param>
    /// <param name="instrument">Logical execution instrument key when known</param>
    /// <param name="ieaInstanceId">IEA instance id string when known</param>
    /// <param name="fillQty">Execution quantity when known (else 0)</param>
    public static void Write(
        string phase,
        object? order,
        object? execution,
        string? instrument,
        string? ieaInstanceId,
        int fillQty)
    {
        if (!IsEnabled) return;
        try
        {
            TryExtractIds(order, execution, out var brokerId, out var intentId, out var execId, out var orderState);
            var dto = new LatencyLineDto
            {
                TsUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Phase = phase ?? "",
                BrokerOrderId = brokerId,
                IntentId = intentId,
                ExecutionId = execId,
                OrderState = orderState,
                Instrument = instrument ?? "",
                IeaInstanceId = ieaInstanceId ?? "",
                FillQty = fillQty,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            lock (IoLock)
            {
                File.AppendAllText(EnsurePath(), json + Environment.NewLine);
            }
        }
        catch
        {
            // Observability only
        }
    }

    /// <summary>When only ids are available (e.g. exec handler after resolve).</summary>
    public static void WriteResolved(
        string phase,
        string brokerOrderId,
        string intentId,
        string instrument,
        string? ieaInstanceId,
        int fillQty)
    {
        if (!IsEnabled) return;
        try
        {
            var dto = new LatencyLineDto
            {
                TsUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Phase = phase ?? "",
                BrokerOrderId = brokerOrderId ?? "",
                IntentId = intentId ?? "",
                ExecutionId = "",
                OrderState = "",
                Instrument = instrument ?? "",
                IeaInstanceId = ieaInstanceId ?? "",
                FillQty = fillQty,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            lock (IoLock)
            {
                File.AppendAllText(EnsurePath(), json + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    private static void TryExtractIds(object? orderObj, object? execObj,
        out string brokerId, out string intentId, out string execId, out string orderState)
    {
        brokerId = "";
        intentId = "";
        execId = "";
        orderState = "";
        if (orderObj != null)
        {
            try
            {
                dynamic o = orderObj;
                try
                {
                    brokerId = (o.OrderId ?? "").ToString();
                }
                catch
                {
                    /* ignore */
                }

                try
                {
                    orderState = (o.OrderState ?? "").ToString();
                }
                catch
                {
                    /* ignore */
                }

                string? tag = null;
                try
                {
                    tag = o.Tag as string;
                }
                catch
                {
                    /* ignore */
                }

                if (string.IsNullOrEmpty(tag))
                {
                    try
                    {
                        tag = o.Name as string;
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                if (string.IsNullOrEmpty(tag))
                {
                    try
                    {
                        tag = o.FromEntrySignal as string;
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                if (string.IsNullOrEmpty(tag))
                {
                    try
                    {
                        tag = o.SignalName as string;
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                if (!string.IsNullOrEmpty(tag))
                {
                    intentId = RobotOrderIds.DecodeIntentId(tag) ?? "";
                    if (string.IsNullOrEmpty(intentId))
                        intentId = RobotOrderIds.ParseTag(tag).IntentId ?? "";
                }
            }
            catch
            {
                /* ignore */
            }
        }

        if (execObj == null) return;
        try
        {
            dynamic x = execObj;
            execId = x.ExecutionId as string ?? "";
        }
        catch
        {
            /* ignore */
        }
    }

    private sealed class LatencyLineDto
    {
        public string TsUtc { get; set; } = "";
        public string Phase { get; set; } = "";
        public string BrokerOrderId { get; set; } = "";
        public string IntentId { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public string OrderState { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string IeaInstanceId { get; set; } = "";
        public int FillQty { get; set; }
        public int ThreadId { get; set; }
    }
}
