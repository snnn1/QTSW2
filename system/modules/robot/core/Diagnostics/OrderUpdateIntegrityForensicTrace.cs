// Opt-in JSONL forensics: HandleOrderUpdateReal → mismatch hook → journal integrity → hard flatten.
// Enable: set QTSW2_ORDER_UPDATE_INTEGRITY_TRACE=1 before starting NinjaTrader / strategy.
// Output: <run artifact root>/logs/robot/order_update_integrity_forensic.jsonl (see QTSW2_ROBOT_PERSISTENCE_BASE)

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Diagnostics;

public static class OrderUpdateIntegrityForensicTrace
{
    private static readonly object IoLock = new();
    private static string? _path;

    private static readonly AsyncLocal<string?> s_correlation = new();
    private static readonly AsyncLocal<Stopwatch?> s_orderUpdateSw = new();

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("QTSW2_ORDER_UPDATE_INTEGRITY_TRACE"), "1",
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
            _path = Path.Combine(dir, "order_update_integrity_forensic.jsonl");
            return _path;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private sealed class LineDto
    {
        public string TsUtc { get; set; } = "";
        public string Phase { get; set; } = "";
        public string? CorrelationId { get; set; }
        public string BrokerOrderId { get; set; } = "";
        public string IntentId { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string OrderState { get; set; } = "";
        public int ThreadId { get; set; }
        /// <summary>Elapsed ms since HandleOrderUpdateReal scope start (when in scope).</summary>
        public long? ElapsedMsSinceOrderUpdateStart { get; set; }
        /// <summary>Elapsed ms for this phase only (e.g. single call).</summary>
        public long? SectionElapsedMs { get; set; }
        public string? Detail { get; set; }
    }

    private static void Append(LineDto dto)
    {
        if (!IsEnabled) return;
        try
        {
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            lock (IoLock)
            {
                File.AppendAllText(EnsurePath(), json + Environment.NewLine);
            }
        }
        catch
        {
            /* observability only */
        }
    }

    public static long ElapsedMsSinceOrderUpdateStart()
    {
        var sw = s_orderUpdateSw.Value;
        return sw == null ? 0 : sw.ElapsedMilliseconds;
    }

    public static string? CurrentCorrelation => s_correlation.Value;

    /// <summary>Begins correlation + stopwatch for HandleOrderUpdateReal (after robot intentId validated).</summary>
    public static IDisposable BeginHandleOrderUpdateScope(string brokerOrderId, string intentId, string instrument,
        string orderState)
    {
        if (!IsEnabled)
            return NoopDisposable.Instance;

        var cid = Guid.NewGuid().ToString("N");
        s_correlation.Value = cid;
        var sw = Stopwatch.StartNew();
        s_orderUpdateSw.Value = sw;
        Append(new LineDto
        {
            TsUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Phase = "HANDLE_ORDER_UPDATE_REAL_ENTER",
            CorrelationId = cid,
            BrokerOrderId = brokerOrderId ?? "",
            IntentId = intentId ?? "",
            Instrument = instrument ?? "",
            OrderState = orderState ?? "",
            ThreadId = Thread.CurrentThread.ManagedThreadId,
            ElapsedMsSinceOrderUpdateStart = 0
        });
        return new Scope(cid, brokerOrderId ?? "", intentId ?? "", instrument ?? "", orderState ?? "");
    }

    public static void Step(string phase, string? detail = null, long? sectionElapsedMs = null)
    {
        if (!IsEnabled) return;
        var cid = s_correlation.Value;
        if (cid == null) return;
        Append(new LineDto
        {
            TsUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Phase = phase,
            CorrelationId = cid,
            BrokerOrderId = "",
            IntentId = "",
            Instrument = "",
            OrderState = "",
            ThreadId = Thread.CurrentThread.ManagedThreadId,
            ElapsedMsSinceOrderUpdateStart = ElapsedMsSinceOrderUpdateStart(),
            SectionElapsedMs = sectionElapsedMs,
            Detail = detail
        });
    }

    /// <summary>Rocket-engine path: optional broker/intent for lines emitted outside HandleOrderUpdate scope.</summary>
    public static void StepEngine(string phase, string instrument, long? sectionElapsedMs = null, string? detail = null)
    {
        if (!IsEnabled) return;
        Append(new LineDto
        {
            TsUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Phase = phase,
            CorrelationId = s_correlation.Value,
            BrokerOrderId = "",
            IntentId = "",
            Instrument = instrument ?? "",
            OrderState = "",
            ThreadId = Thread.CurrentThread.ManagedThreadId,
            ElapsedMsSinceOrderUpdateStart = s_orderUpdateSw.Value != null ? ElapsedMsSinceOrderUpdateStart() : null,
            SectionElapsedMs = sectionElapsedMs,
            Detail = detail
        });
    }

    private sealed class Scope : IDisposable
    {
        private readonly string _cid;
        private readonly string _brokerOrderId;
        private readonly string _intentId;
        private readonly string _instrument;
        private readonly string _orderState;

        public Scope(string cid, string brokerOrderId, string intentId, string instrument, string orderState)
        {
            _cid = cid;
            _brokerOrderId = brokerOrderId;
            _intentId = intentId;
            _instrument = instrument;
            _orderState = orderState;
        }

        public void Dispose()
        {
            var sw = s_orderUpdateSw.Value;
            var total = sw?.ElapsedMilliseconds ?? 0;
            Append(new LineDto
            {
                TsUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Phase = "HANDLE_ORDER_UPDATE_REAL_EXIT",
                CorrelationId = _cid,
                BrokerOrderId = _brokerOrderId,
                IntentId = _intentId,
                Instrument = _instrument,
                OrderState = _orderState,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                ElapsedMsSinceOrderUpdateStart = total,
                SectionElapsedMs = total,
                Detail = "total_ms_in_handle_order_update_real"
            });
            s_orderUpdateSw.Value = null;
            s_correlation.Value = null;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
