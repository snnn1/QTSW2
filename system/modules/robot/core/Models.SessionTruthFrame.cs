using System;

namespace QTSW2.Robot.Core;

/// <summary>
/// Immutable authority frame for session decisions. Consumers should use this
/// instead of reading NT cache, internal calendar, or fallback results directly.
/// </summary>
public sealed class SessionTruthFrame
{
    public string TradingDay { get; init; } = "";
    public string SessionClass { get; init; } = "";
    public bool IsResolved { get; init; }
    public bool HasSession { get; init; }
    public string Source { get; init; } = "";
    public int AuthorityRank { get; init; }
    public bool UsedFallback { get; init; }
    public bool ConflictDetected { get; init; }
    public string? FailureReason { get; init; }
    public SessionCloseResult? Result { get; init; }
    public SessionCloseResult? NtCacheResult { get; init; }
    public SessionCloseResult? InternalResult { get; init; }

    public DateTimeOffset? FlattenTriggerUtc => Result?.FlattenTriggerUtc;
    public DateTimeOffset? ResolvedSessionCloseUtc => Result?.ResolvedSessionCloseUtc;
    public DateTimeOffset? NextSessionBeginUtc => Result?.NextSessionBeginUtc;
    public int BufferSeconds => Result?.BufferSeconds ?? 0;

    public static SessionTruthFrame Unresolved(string tradingDay, string sessionClass, string failureReason) =>
        new()
        {
            TradingDay = tradingDay ?? "",
            SessionClass = sessionClass ?? "",
            IsResolved = false,
            HasSession = false,
            Source = "UNRESOLVED",
            AuthorityRank = 0,
            FailureReason = failureReason
        };
}
