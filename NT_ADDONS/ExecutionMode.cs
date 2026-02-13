namespace QTSW2.Robot.Core;

/// <summary>
/// Execution mode determines where orders are placed.
/// Must be explicit and mutually exclusive.
/// Default is DRYRUN unless explicitly set.
/// </summary>
public enum ExecutionMode
{
    /// <summary>
    /// DRYRUN: No orders placed. Only intent logging.
    /// </summary>
    DRYRUN = 0,

    /// <summary>
    /// SIM: Places orders in NinjaTrader Sim account only.
    /// </summary>
    SIM = 1,

    /// <summary>
    /// LIVE: Places orders in real brokerage account.
    /// Requires explicit two-key enable (CLI flag + config).
    /// </summary>
    LIVE = 2
}
