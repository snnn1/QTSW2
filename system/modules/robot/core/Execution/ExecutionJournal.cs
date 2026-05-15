using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class ExecutionJournal
{
    private readonly string _journalDir;
    private readonly string _streamJournalDir;
    private readonly RobotLogger _log;
    private readonly Dictionary<string, ExecutionJournalEntry> _cache = new();
    private readonly Dictionary<string, bool> _entryFillByStream = new(); // key = "tradingDate_stream", O(1) HasEntryFillForStream
    private readonly object _lock = new object();

    /// <summary>Normalized journal instrument (entry.Instrument root) → adoption-candidate intent ids. Caller must hold <see cref="_lock"/>.</summary>
    private readonly Dictionary<string, HashSet<string>> _normInstToAdoptionCandidateIntentIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Intent id → normalized instrument bucket key for O(1) removal. Caller must hold <see cref="_lock"/>.</summary>
    private readonly Dictionary<string, string> _adoptionCandidateIntentToNormInst = new(StringComparer.OrdinalIgnoreCase);

    private bool _adoptionCandidateIndexWarmed;
    private long _adoptionCandidateIndexLookupIndexHits;
    private long _adoptionCandidateIndexLookupFallbacks;

    /// <summary>In-process: fill dedupe keys persisted via <see cref="RecordEntryFill"/> for I5 stale detection.</summary>
    private readonly ConcurrentDictionary<string, byte> _parityPendingFillKeysApplied = new(StringComparer.Ordinal);

    /// <summary>Journal bucket (trading_date segment) for durable untracked-fill recovery markers.</summary>
    public const string UntrackedFillRecoveryTradingDateBucket = "RECOVERY";

    /// <summary>Stream segment for untracked-fill recovery markers; excluded from adoption.</summary>
    public const string UntrackedFillRecoveryStream = "UNTRACKED_RECOVERY";

    /// <summary>Stream segment for journal-integrity synthetic recovered intents (broker-anchored; not strategy streams).</summary>
    public const string RecoveredIntentStream = "JOURNAL_INTEGRITY_RECOVERED";

    /// <summary><see cref="ExecutionJournalEntry.IntentType"/> — synthetic row from broker snapshot deficit.</summary>
    public const string IntentTypeRecovered = "RECOVERED";

    /// <summary><see cref="ExecutionJournalEntry.RecoverySource"/> — position observed from account snapshot.</summary>
    public const string RecoverySourceBrokerSnapshot = "BROKER_SNAPSHOT";

    /// <summary>Matches <see cref="JournalParityChecker"/> broker position sum (abs quantities per matching instrument).</summary>
    public static int SumAbsBrokerPositionForInstrument(AccountSnapshot? snap, string instrument)
    {
        if (snap?.Positions == null || string.IsNullOrWhiteSpace(instrument)) return 0;
        var inst = instrument.Trim();
        var sum = 0;
        foreach (var p in snap.Positions)
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            sum += Math.Abs(p.Quantity);
        }

        return sum;
    }

    /// <summary>Signed net position for instrument (sum of <see cref="PositionSnapshot.Quantity"/>).</summary>
    public static int SumNetBrokerPositionSignedForInstrument(AccountSnapshot? snap, string instrument)
    {
        if (snap?.Positions == null || string.IsNullOrWhiteSpace(instrument)) return 0;
        var inst = instrument.Trim();
        var sum = 0;
        foreach (var p in snap.Positions)
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            sum += p.Quantity;
        }

        return sum;
    }

    /// <summary>Record that a parity pending dedupe key was journaled (I5); in-process only.</summary>
    public void RegisterParityPendingFillPersisted(string? parityDedupeKey)
    {
        if (string.IsNullOrWhiteSpace(parityDedupeKey)) return;
        _parityPendingFillKeysApplied[parityDedupeKey.Trim()] = 1;
    }

    /// <summary>True if this fill key was already persisted to the journal (stale pending must not count).</summary>
    public bool IsParityPendingFillKeyApplied(string? parityDedupeKey) =>
        !string.IsNullOrWhiteSpace(parityDedupeKey) &&
        _parityPendingFillKeysApplied.ContainsKey(parityDedupeKey.Trim());

    /// <summary>Test-only: clears applied-key set (new engine session clears via new <see cref="ExecutionJournal"/> instance).</summary>
    public void ClearParityPendingAppliedKeysForTests() => _parityPendingFillKeysApplied.Clear();

    /// <summary>Volume-weighted average price for matching instrument positions (abs qty weights).</summary>
    public static decimal? TryWeightedAveragePriceForInstrument(AccountSnapshot? snap, string instrument)
    {
        if (snap?.Positions == null || string.IsNullOrWhiteSpace(instrument)) return null;
        var inst = instrument.Trim();
        decimal notional = 0;
        var w = 0;
        foreach (var p in snap.Positions)
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            var a = Math.Abs(p.Quantity);
            if (a == 0) continue;
            notional += a * p.AveragePrice;
            w += a;
        }

        return w > 0 ? notional / w : (decimal?)null;
    }

    public static string BuildRecoveredIntegrityIntentId(string executionInstrumentPrimary)
    {
        var root = NormalizeJournalInstrumentSymbol(string.IsNullOrWhiteSpace(executionInstrumentPrimary)
            ? "UNK"
            : executionInstrumentPrimary.Trim());
        foreach (var c in Path.GetInvalidFileNameChars())
            root = root.Replace(c, '-');
        return "RECOVERED-" + root;
    }
    
    // Callback for stream stand-down on journal corruption
    private Action<string, string, string, DateTimeOffset>? _onJournalCorruptionCallback;
    
    // Callback for recording execution costs in ExecutionSummary
    private Action<string, decimal, decimal?, decimal?>? _onExecutionCostCallback;

    /// <summary>RobotEngine wires: after tagged-broker journal reopen, rehydrate <see cref="InstrumentIntentCoordinator"/> for live qty.</summary>
    private Action<string, string, string, string, string, int, int, DateTimeOffset>? _onTaggedBrokerJournalRehydrationCallback;

    /// <summary>RobotEngine wires stream-journal terminal cleanup after an execution journal row becomes completed.</summary>
    private Action<string, string, string, DateTimeOffset, string?>? _onTradeCompletedCallback;

    private Action? _onReleaseSuppressionActivityNotify;

    /// <summary>Wired by RobotEngine to <see cref="ReleaseReconciliationRedundancySuppression.NotifyExecutionActivity"/>.</summary>
    public void SetReleaseSuppressionActivityNotify(Action? notify) =>
        _onReleaseSuppressionActivityNotify = notify;

    private void BumpReleaseSuppressionActivity() => _onReleaseSuppressionActivityNotify?.Invoke();

    public ExecutionJournal(string projectRoot, RobotLogger log)
    {
        // RULE: No logging allowed before RobotEngine.Start() completes — do not call _log.Write here (RobotLogger may be blocked until RebindLogging).
        _journalDir = RobotRunArtifactPaths.StateExecutionJournals(projectRoot);
        _streamJournalDir = RobotRunArtifactPaths.StateStreamJournals(projectRoot);
        Directory.CreateDirectory(_journalDir);
        ValidateJournalDirectory();  // Phase 3.2: fail closed if not writable
        _log = log;
        try
        {
            lock (_lock)
            {
                RebuildAdoptionCandidateIndexFromDiskLocked();
            }
        }
        catch (Exception)
        {
            _adoptionCandidateIndexWarmed = false;
            // Intentionally no _log.Write — constructor runs before logger rebind; adoption lookups fall back to full scan until a later rebuild path succeeds.
        }
    }
    
    /// <summary>
    /// Phase 3.2: Startup self-check. Verifies journal dir exists and is writable.
    /// Uses unique temp file per instance to avoid race when multiple strategies start concurrently.
    /// </summary>
    private void ValidateJournalDirectory()
    {
        if (!Directory.Exists(_journalDir))
            throw new InvalidOperationException($"ExecutionJournal: journal directory does not exist: {_journalDir}");
        
        var checkPath = Path.Combine(_journalDir, $".startup_check_{Guid.NewGuid():N}");
        try
        {
            var testContent = DateTimeOffset.UtcNow.ToString("o");
            File.WriteAllText(checkPath, testContent);
            var readBack = File.ReadAllText(checkPath);
            if (readBack != testContent)
                throw new InvalidOperationException($"ExecutionJournal: startup check read-back mismatch for {_journalDir}");
            File.Delete(checkPath);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"ExecutionJournal: journal directory not writable: {_journalDir}", ex);
        }
    }
    
    /// <summary>Journal directory path (for diagnostics).</summary>
    public string JournalDirectory => _journalDir;

    /// <summary>
    /// Get journal visibility diagnostics for adoption deferral logging.
    /// Returns (directory exists, file count, directory path). FileCount is -1 on read failure.
    /// </summary>
    public (bool DirectoryExists, int FileCount, string JournalDir) GetJournalDiagnostics()
    {
        try
        {
            var exists = Directory.Exists(_journalDir);
            var files = exists ? Directory.GetFiles(_journalDir, "*.json") : Array.Empty<string>();
            return (exists, files.Length, _journalDir);
        }
        catch
        {
            return (false, -1, _journalDir);
        }
    }

    /// <summary>
    /// Set callback for journal corruption events (stream stand-down).
    /// </summary>
    public void SetJournalCorruptionCallback(Action<string, string, string, DateTimeOffset> callback)
    {
        _onJournalCorruptionCallback = callback;
    }
    
    /// <summary>
    /// Set callback for recording execution costs (slippage, commission, fees).
    /// </summary>
    public void SetExecutionCostCallback(Action<string, decimal, decimal?, decimal?> callback)
    {
        _onExecutionCostCallback = callback;
    }

    /// <summary>
    /// Optional: invoked after <see cref="UpsertTaggedBrokerExposureRecoveryJournal"/> when the row has open journal qty.
    /// Parameters: intentId, tradingDate, stream, executionInstrument, direction, entryFilledQty, exitFilledQty, utcNow.
    /// </summary>
    public void SetTaggedBrokerJournalRehydrationCallback(
        Action<string, string, string, string, string, int, int, DateTimeOffset>? callback)
    {
        _onTaggedBrokerJournalRehydrationCallback = callback;
    }

    public void SetTradeCompletedCallback(Action<string, string, string, DateTimeOffset, string?>? callback)
    {
        _onTradeCompletedCallback = callback;
    }

    /// <summary>
    /// Compute intent ID from canonical intent fields (hash of 15 fields).
    /// </summary>
    /// <remarks>
    /// <b>Stability contract</b> — any <see cref="Intent"/> built for the same logical trade must use identical
    /// string and decimal normalization here, or <see cref="Intent.ComputeIntentId"/> will differ:
    /// <list type="bullet">
    /// <item>Prices: <c>F2</c> fixed-point; null → literal <c>NULL</c> in the canonical string.</item>
    /// <item>Direction: null → <c>NULL</c>; no case folding.</item>
    /// <item>Other strings: as stored (trim/casing at <see cref="Intent"/> construction sites must match).</item>
    /// <item>Slot time: verbatim (e.g. <c>09:00</c> vs <c>9:00</c> are different identities).</item>
    /// <item>Entry time is not part of the hash.</item>
    /// </list>
    /// </remarks>
}
