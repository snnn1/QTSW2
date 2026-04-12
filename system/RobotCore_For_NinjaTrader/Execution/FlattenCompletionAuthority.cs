using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// G4: Single normative definition of <b>flatten officially complete</b> for execution forensics.
/// <para>
/// <b>Not complete:</b> flatten requested, enqueue-only, command delegate finished, queue drained,
/// <see cref="FlattenResult.Success"/> after submit, or log lines alone.
/// </para>
/// <para>
/// <b>Complete:</b> canonical broker exposure for the instrument scope shows <b>zero</b> open quantity
/// in the same model as reconciliation — <see cref="BrokerCanonicalExposure.ReconciliationAbsQuantityTotal"/> == 0.
/// This matches <c>FLATTEN_BROKER_FLAT_CONFIRMED</c> in the adapter verify path and session-close wait when IIEA is available.
/// </para>
/// </summary>
public static class FlattenCompletionAuthority
{
    /// <summary>Authoritative flatten-complete predicate (broker / reconciliation-aligned scope).</summary>
    public static bool IsOfficialFlattenComplete(BrokerCanonicalExposure exposure) =>
        exposure.ReconciliationAbsQuantityTotal == 0;

    /// <summary>
    /// When the adapter implements <see cref="IIEAOrderExecutor"/>, returns canonical exposure for <paramref name="instrument"/>.
    /// Otherwise returns false (caller may use legacy snapshot polling — see StreamStateMachine).
    /// </summary>
    public static bool TryGetCanonicalExposure(IExecutionAdapter? adapter, string instrument, out BrokerCanonicalExposure exposure)
    {
        exposure = BrokerCanonicalExposure.Empty("");
        if (adapter is not IIEAOrderExecutor iea) return false;
        var key = (instrument ?? "").Trim();
        if (string.IsNullOrEmpty(key)) return false;
        exposure = iea.GetBrokerCanonicalExposure(key);
        return true;
    }

    /// <summary>Remaining non-flat quantity in official model (sum of abs leg qtys); zero iff complete.</summary>
    public static int OfficialRemainingAbsQuantity(BrokerCanonicalExposure exposure) =>
        exposure.ReconciliationAbsQuantityTotal;
}
