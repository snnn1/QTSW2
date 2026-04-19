using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Emits periodic and event-driven authoritative state snapshots as JSONL.
/// Dual trigger model: periodic (heartbeat) + event-driven (fill, mismatch, supervisor).
/// Critical triggers (orphan fill, FAIL_CLOSED, conflict rejected) emit immediately without coalescing.
/// </summary>
public sealed class AuthoritativeStateEmitter : IDisposable
{
    private readonly InstrumentOwnershipLedger _ledger;
    private readonly Func<AccountSnapshot> _getAccountSnapshot;
    private readonly Func<IReadOnlyList<string>> _getActiveInstruments;
    private readonly Func<string, string?>? _getSupervisoryState;
    private readonly Func<string, string?>? _getMismatchEscalationState;
    private readonly Func<string, bool>? _isInstrumentFrozen;
    private readonly Func<bool>? _isKillSwitchActive;
    private readonly string _baseDir;
    private readonly RobotLogger _log;
    private readonly string _account;
    private readonly Func<string>? _getAccountName;
    private readonly Func<string?>? _getTradingDate;

    private long _snapshotSequence;
    private Timer? _periodicTimer;
    private DateTimeOffset _lastCoalescedEmitUtc = DateTimeOffset.MinValue;
    private readonly object _emitLock = new();
    private static readonly ConcurrentDictionary<string, object> SnapshotFileLocks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private const int CoalesceWindowMs = 500;
    private const int DefaultPeriodicIntervalMs = 30000;

    public AuthoritativeStateEmitter(
        InstrumentOwnershipLedger ledger,
        Func<AccountSnapshot> getAccountSnapshot,
        Func<IReadOnlyList<string>> getActiveInstruments,
        string persistenceBase,
        RobotLogger log,
        string account = "default",
        Func<string, string?>? getSupervisoryState = null,
        Func<string, string?>? getMismatchEscalationState = null,
        Func<string, bool>? isInstrumentFrozen = null,
        Func<bool>? isKillSwitchActive = null,
        Func<string>? getAccountName = null,
        Func<string?>? getTradingDate = null)
    {
        _ledger = ledger;
        _getAccountSnapshot = getAccountSnapshot;
        _getActiveInstruments = getActiveInstruments;
        _baseDir = Path.Combine(persistenceBase ?? "", "events", "ownership_snapshots");
        _log = log;
        _account = account;
        _getAccountName = getAccountName;
        _getTradingDate = getTradingDate;
        _getSupervisoryState = getSupervisoryState;
        _getMismatchEscalationState = getMismatchEscalationState;
        _isInstrumentFrozen = isInstrumentFrozen;
        _isKillSwitchActive = isKillSwitchActive;
    }

    public void StartPeriodicTimer(int intervalMs = DefaultPeriodicIntervalMs)
    {
        _periodicTimer = new Timer(_ => EmitSnapshot(SnapshotTrigger.Periodic), null, intervalMs, intervalMs);
    }

    /// <summary>
    /// Notify a coalesced event trigger (mapped fills, freeze/unfreeze).
    /// Coalesces within 500ms to avoid flood.
    /// </summary>
    public void NotifyCoalescedTrigger(SnapshotTrigger trigger)
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastCoalescedEmitUtc).TotalMilliseconds < CoalesceWindowMs)
            return;
        EmitSnapshot(trigger);
    }

    /// <summary>
    /// Notify an immediate trigger (orphan fill, FAIL_CLOSED, conflict rejected, ledger errors).
    /// Never coalesced — always emits immediately.
    /// </summary>
    public void NotifyImmediateTrigger(SnapshotTrigger trigger)
    {
        EmitSnapshot(trigger);
    }

    /// <summary>
    /// Emit a RESTORE_BASELINE snapshot immediately after RestoreFromJournal completes.
    /// </summary>
    public void EmitRestoreBaseline()
    {
        EmitSnapshot(SnapshotTrigger.RestoreBaseline);
    }

    private void EmitSnapshot(SnapshotTrigger trigger)
    {
        if (_disposed) return;

        lock (_emitLock)
        {
            try
            {
                var utcNow = DateTimeOffset.UtcNow;
                var seq = Interlocked.Increment(ref _snapshotSequence);
                var account = ResolveAccount();

                AccountSnapshot? accountSnapshot = null;
                try { accountSnapshot = _getAccountSnapshot(); } catch { /* best effort */ }

                var instruments = _getActiveInstruments();
                var brokerQtyByInstrument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var brokerWorkingByInstrument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (accountSnapshot?.Positions != null)
                {
                    foreach (var pos in accountSnapshot.Positions)
                    {
                        var k = pos.Instrument?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(k))
                            brokerQtyByInstrument[k] = pos.Quantity;
                    }
                }
                if (accountSnapshot?.WorkingOrders != null)
                {
                    foreach (var wo in accountSnapshot.WorkingOrders)
                    {
                        var k = wo.Instrument?.Trim() ?? "";
                        if (string.IsNullOrEmpty(k)) continue;
                        brokerWorkingByInstrument.TryGetValue(k, out var c);
                        brokerWorkingByInstrument[k] = c + 1;
                    }
                }

                var allInstruments = new HashSet<string>(instruments, StringComparer.OrdinalIgnoreCase);

                var instrumentSnapshots = new List<AuthoritativeInstrumentSnapshot>();
                foreach (var inst in allInstruments)
                {
                    var ownerSnap = _ledger.GetOwnershipSnapshot(account, inst);
                    brokerQtyByInstrument.TryGetValue(inst, out var brokerQty);
                    brokerWorkingByInstrument.TryGetValue(inst, out var workingCount);

                    var brokerSignedQty = NormalizeBrokerSignedQty(brokerQty, ownerSnap);
                    var unexplained = ownerSnap.ComputeUnexplainedQty(brokerSignedQty);

                    instrumentSnapshots.Add(new AuthoritativeInstrumentSnapshot
                    {
                        Account = account,
                        Instrument = inst,
                        OwnershipVersion = ownerSnap.OwnershipVersion,
                        LedgerSignedNetQty = ownerSnap.LedgerSignedNetQty,
                        ActiveSlotCount = ownerSnap.ActiveSlotCount,
                        OrphanSlotCount = ownerSnap.OrphanSlotCount,
                        Slots = ownerSnap.Slots.Where(s => s.State != SlotState.Closed).Select(s => new SlotSummary
                        {
                            IntentId = s.IntentId,
                            StreamId = s.StreamId,
                            Direction = s.Direction.ToString(),
                            Remaining = s.Remaining,
                            State = s.State.ToString()
                        }).ToList(),
                        BrokerPositionQty = brokerQty,
                        BrokerWorkingOrderCount = workingCount,
                        JournalOpenQty = ownerSnap.Slots.Where(s => s.State != SlotState.Closed).Sum(s => s.Remaining),
                        JournalRowCount = ownerSnap.Slots.Count,
                        UnexplainedQty = unexplained,
                        IsExplainable = unexplained == 0,
                        DerivedAuthority = unexplained == 0 ? "REAL" : "UNKNOWN",
                        SupervisoryState = _getSupervisoryState?.Invoke(inst),
                        MismatchEscalationState = _getMismatchEscalationState?.Invoke(inst),
                        IsFrozen = _isInstrumentFrozen?.Invoke(inst) ?? false,
                        IsKillSwitchActive = _isKillSwitchActive?.Invoke() ?? false,
                        SnapshotTrigger = trigger,
                        SnapshotSequence = seq,
                        SnapshotUtc = utcNow.ToString("o")
                    });
                }

                var envelope = new AuthoritativeStateSnapshotEnvelope
                {
                    Account = account,
                    SnapshotSequence = seq,
                    Trigger = trigger,
                    EmittedUtc = utcNow.ToString("o"),
                    Instruments = instrumentSnapshots
                };

                var tradingDate = ResolveTradingDate(utcNow);
                var dir = Path.Combine(_baseDir, tradingDate);
                Directory.CreateDirectory(dir);
                var filePath = Path.Combine(dir, "ownership_snapshots.jsonl");
                var json = JsonSerializer.Serialize(envelope);
                AppendSnapshotLine(filePath, json);

                _lastCoalescedEmitUtc = utcNow;
            }
            catch (Exception ex)
            {
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "SNAPSHOT_EMITTER_ERROR", "ENGINE",
                    new { error = ex.Message, trigger = trigger.ToString() }));
            }
        }
    }

    private static void AppendSnapshotLine(string filePath, string json)
    {
        var fullPath = Path.GetFullPath(filePath);
        var fileLock = SnapshotFileLocks.GetOrAdd(fullPath, _ => new object());

        lock (fileLock)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    writer.WriteLine(json);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }
    }

    private string ResolveAccount()
    {
        try
        {
            var account = _getAccountName?.Invoke();
            if (!string.IsNullOrWhiteSpace(account))
                return account.Trim();
        }
        catch { }

        return string.IsNullOrWhiteSpace(_account) ? "default" : _account.Trim();
    }

    private static int NormalizeBrokerSignedQty(int brokerQty, InstrumentOwnershipSnapshot ownerSnap)
    {
        if (brokerQty <= 0) return brokerQty;

        if (ownerSnap.LedgerSignedNetQty < 0)
            return -Math.Abs(brokerQty);

        if (ownerSnap.LedgerSignedNetQty > 0)
            return Math.Abs(brokerQty);

        var directions = ownerSnap.Slots
            .Where(s => s.State != SlotState.Closed && s.Remaining > 0)
            .Select(s => s.Direction)
            .Distinct()
            .ToList();

        return directions.Count == 1 && directions[0] == SlotDirection.Short
            ? -Math.Abs(brokerQty)
            : brokerQty;
    }

    private string ResolveTradingDate(DateTimeOffset utcNow)
    {
        try
        {
            var tradingDate = _getTradingDate?.Invoke();
            if (!string.IsNullOrWhiteSpace(tradingDate))
                return tradingDate.Trim();
        }
        catch { }

        return utcNow.ToString("yyyy-MM-dd");
    }

    public void Dispose()
    {
        _disposed = true;
        _periodicTimer?.Dispose();
    }
}
