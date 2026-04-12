namespace QTSW2.Robot.Core;

/// <summary>
/// Minimal contract for <c>data/session/session_authority.json</c> (control-plane session truth).
/// Other fields in the persisted file are ignored.
/// </summary>
public sealed class SessionAuthorityContract
{
    public string? session_trading_date { get; set; }

    /// <summary>Optional SessionAuthority mode (e.g. auto, manual, replay); echoed in engine logs for incidents.</summary>
    public string? mode { get; set; }
}
