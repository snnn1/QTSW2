using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Contracts
{
    /// <summary>
    /// Deterministic IEA state snapshot for replay hash. Keys sorted for canonical serialization.
    /// Reference: REPLAY_PHASE_0_TARGET.md
    /// </summary>
    public sealed class IEASnapshot
    {
        public Dictionary<string, OrderInfoSnapshot> OrderMap { get; set; } = new();
        public Dictionary<string, IntentSnapshot> IntentMap { get; set; } = new();
        public Dictionary<string, IntentPolicySnapshot> IntentPolicy { get; set; } = new();
        public Dictionary<string, string> DedupState { get; set; } = new();
        public Dictionary<string, string> BeState { get; set; } = new();
        /// <summary>BE diagnostics per intent: price crossed, triggered, trigger/entry prices.</summary>
        public Dictionary<string, BEDiagnosticSnapshot> BeDiagnostics { get; set; } = new();
        public bool InstrumentBlocked { get; set; }
    }

    /// <summary>Per-intent BE diagnostic state for invariant checks.</summary>
    public sealed class BEDiagnosticSnapshot
    {
        public string IntentId { get; set; } = "";
        public decimal BeTriggerPrice { get; set; }
        public decimal EntryPrice { get; set; }
        public string Direction { get; set; } = "";
        /// <summary>True when tick price crossed BE trigger (Long: price>=trigger, Short: price&lt;=trigger).</summary>
        public bool PriceCrossed { get; set; }
        /// <summary>True when ModifyStopToBreakEven succeeded.</summary>
        public bool BeTriggered { get; set; }
    }

    public sealed class IntentPolicySnapshot
    {
        public string IntentId { get; set; } = "";
        public int ExpectedQuantity { get; set; }
        public int MaxQuantity { get; set; }
    }

    public sealed class OrderInfoSnapshot
    {
        public string IntentId { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string OrderType { get; set; } = "";
        public string State { get; set; } = "";
        public int FilledQuantity { get; set; }
        public DateTimeOffset? EntryFillTime { get; set; }
        public bool IsEntryOrder { get; set; }
        public bool ProtectiveStopAcknowledged { get; set; }
        public bool ProtectiveTargetAcknowledged { get; set; }
    }

    public sealed class IntentSnapshot
    {
        public string IntentId { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string? Direction { get; set; }
        public decimal? StopPrice { get; set; }
        public decimal? TargetPrice { get; set; }
        public decimal? BeTrigger { get; set; }
        public decimal? EntryPrice { get; set; }
    }
}
