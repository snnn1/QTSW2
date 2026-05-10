using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class SessionCloseInstrumentOwnerRoutingTests
{
    public static (bool Pass, string? Error) RunSessionCloseInstrumentOwnerRoutingTests()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SessionCloseRoute_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var log = new RobotLogger(root);
            var account = $"PlaybackRoute{Guid.NewGuid():N}";
            var utcNow = new DateTimeOffset(2026, 4, 27, 20, 55, 0, TimeSpan.Zero);

            var targetExecutor = new RecordingExecutor();
            var targetIea = InstrumentExecutionAuthorityRegistry.GetOrCreate(
                account,
                "MNG",
                () => new InstrumentExecutionAuthority(account, "MNG", targetExecutor, log));
            targetIea.RebindExecutor(targetExecutor, "route-test-owner");

            var caller = new NinjaTraderSimAdapter(root, root, log, new ExecutionJournal(root, log));
            caller.SetUseInstrumentExecutionAuthority(true);
            SetPrivateField(caller, "_ieaAccountName", account);

            var result = caller.RequestSessionCloseFlattenImmediate("intent-route", "MNG", utcNow);
            if (result == null)
                return (false, "Session-close route returned null even though a target instrument owner was registered");

            var actions = targetExecutor.Actions;
            if (actions.Count != 2)
                return (false, $"Expected target owner to receive cancel+flatten actions, got {actions.Count}");

            if (actions[0].ActionType != "CANCEL_ORDERS")
                return (false, $"Expected first routed action to cancel working orders, got {actions[0].ActionType}");

            if (actions[1].ActionType != "FLATTEN_INSTRUMENT")
                return (false, $"Expected second routed action to flatten instrument, got {actions[1].ActionType}");

            if (actions.Any(a => !string.Equals(a.InstrumentKey, "MNG", StringComparison.OrdinalIgnoreCase)))
                return (false, "Routed session-close actions did not preserve target instrument key MNG");

            var cancelOnlyCount = caller.RequestSessionCloseCancelIntents(
                new[] { "long-entry-route", "short-entry-route" },
                "MNG",
                utcNow.AddSeconds(1));
            if (cancelOnlyCount != 2)
                return (false, $"Expected cancel-only session-close routing to accept 2 intents, got {cancelOnlyCount}");

            actions = targetExecutor.Actions;
            if (actions.Count != 4)
                return (false, $"Expected target owner to receive 2 additional cancel-only actions, got total {actions.Count}");

            if (actions[2].ActionType != "CANCEL_ORDERS" || actions[3].ActionType != "CANCEL_ORDERS")
                return (false, "Cancel-only session-close cleanup did not route as cancel commands");

            if (actions[2].IntentId != "long-entry-route" || actions[3].IntentId != "short-entry-route")
                return (false, "Cancel-only session-close cleanup did not preserve intent ordering");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
            throw new InvalidOperationException($"Missing field {fieldName}");
        field.SetValue(target, value);
    }

    private sealed class RecordingExecutor : IIEAOrderExecutor
    {
        private readonly object _lock = new();
        private readonly object _entryLock = new();
        private readonly List<INtAction> _actions = new();

        public IReadOnlyList<INtAction> Actions
        {
            get { lock (_lock) return _actions.ToArray(); }
        }

        public bool EnqueueNtAction(INtAction action)
        {
            lock (_lock) _actions.Add(action);
            return true;
        }

        public object? GetEntrySubmissionLock() => _entryLock;
        public void DrainNtActions() { }
        public void EnterStrategyThreadContext() { }
        public void ExitStrategyThreadContext() { }
        public void CancelOrders(IReadOnlyList<object> orders) { }
        public void SubmitOrders(IReadOnlyList<object> orders) { }
        public void SetOrderTag(object order, string tag) { }
        public string GetOrderTag(object order) => "";
        public string GetOrderId(object order) => "";
        public void RecordSubmission(string intentId, string tradingDate, string stream, string instrument, string orderType, string brokerOrderId, DateTimeOffset utcNow) { }
        public (string tradingDate, string stream, decimal? entryPrice, decimal? stopPrice, decimal? targetPrice, string? direction, string? ocoGroup) GetIntentInfo(string intentId)
            => ("", "", null, null, null, null, null);
        public object GetInstrument() => new object();
        public object GetAccount() => new object();
        public bool CanSubmitExit(string intentId, int quantity) => true;
        public bool HasWorkingProtectivesForIntent(string intentId) => false;
        public (decimal? stopPrice, decimal? targetPrice) GetWorkingProtectivePrices(string intentId) => (null, null);
        public (decimal? stopPrice, decimal? targetPrice, int? stopQty, int? targetQty) GetWorkingProtectiveState(string intentId) => (null, null, null, null);
        public bool IsExecutionAllowed() => true;
        public IReadOnlyList<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> GetActiveIntentsForBEMonitoring(string? executionInstrument)
            => Array.Empty<(string, Intent, decimal, decimal, decimal?, string)>();
        public IReadOnlyCollection<string> GetAdoptionCandidateIntentIds(string? executionInstrument) => Array.Empty<string>();
        public int ReopenBrokerFlatCompletedJournalsForCarryover(string? executionInstrument, IReadOnlyDictionary<string, int> workingIntentOpenQtyByIntent, DateTimeOffset utcNow, string triggerSource) => 0;
        public (string JournalDir, int FileCount, bool DirectoryExists) GetJournalDiagnostics(string? executionInstrument) => ("", 0, false);
        public void EmitRecoveryAdoptionZeroDeltaDiagnostics(string executionInstrumentKey, string adoptionScanEpisodeId, int adoptedDelta, bool isRecoveryAdoptionScan, IReadOnlyCollection<string>? registryMismatchTrustedIntentIds) { }
        public void EvaluateBreakEvenCore(decimal tickPrice, DateTimeOffset eventTime, string executionInstrument) { }
        public decimal GetTickSize() => 0.01m;
        public object CreateStopMarketOrder(string instrument, string direction, int quantity, decimal stopPrice, string tag, string? ocoGroup) => new object();
        public OrderSubmissionResult SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
        public OrderSubmissionResult SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => OrderSubmissionResult.SuccessResult(null, utcNow, utcNow);
        public OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow)
            => OrderModificationResult.SuccessResult(utcNow);
        public FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow) => FlattenResult.SuccessResult(utcNow);
        public FlattenResult EmergencyFlatten(string instrument, DateTimeOffset utcNow) => FlattenResult.SuccessResult(utcNow);
        public (int quantity, string direction) GetAccountPositionForInstrument(string instrument) => (0, "");
        public BrokerCanonicalExposure GetBrokerCanonicalExposure(string instrument) => BrokerCanonicalExposure.Empty(instrument);
        public OrderSubmissionResult SubmitFlattenOrder(string instrument, string side, int quantity, FlattenDecisionSnapshot snapshot, DateTimeOffset utcNow, object? nativeInstrumentForBrokerOrder = null)
            => OrderSubmissionResult.SuccessResult("flatten-order", utcNow, utcNow);
        public void StandDownStream(string streamId, DateTimeOffset utcNow, string reason) { }
        public void FailClosed(string intentId, Intent intent, string failureReason, string eventType, string notificationKey, string notificationTitle, string notificationMessage, OrderSubmissionResult? stopResult, OrderSubmissionResult? targetResult, object? additionalData, DateTimeOffset utcNow) { }
        public bool TryQueueProtectiveForRecovery(string intentId, Intent intent, int totalFilledQuantity, DateTimeOffset utcNow) => false;
        public void ProcessExecutionUpdate(object execution, object order) { }
        public void ProcessOrderUpdate(object order, object orderUpdate) { }
        public void SetProtectionStateWorkingForAdoptedStop(string intentId) { }
    }
}
