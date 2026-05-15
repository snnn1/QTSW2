using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class AuthorityFrameTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = Case_CleanFlat_AllPredicatesPass();
        if (e != null) return (false, e);
        e = Case_TrackedExposure_ExplainedByRobotEvidence();
        if (e != null) return (false, e);
        e = Case_UntrackedBrokerExposure_FailsClosed();
        if (e != null) return (false, e);
        e = Case_BrokerNetFlatGrossOpen_IsContradiction();
        if (e != null) return (false, e);
        e = Case_BrokerWorkingOrdersWithoutIeaRegistry_IsContradiction();
        if (e != null) return (false, e);
        e = Case_PlaybackScenarioContext_PreservedInFrame();
        if (e != null) return (false, e);
        e = Case_LogPayload_SerializesListsAsText();
        if (e != null) return (false, e);
        e = Case_RuntimeProofFields_DefaultFromLoadedAssembly();
        if (e != null) return (false, e);
        return (true, null);
    }

    private static string? Case_CleanFlat_AllPredicatesPass()
    {
        var utc = DateTimeOffset.UtcNow;
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            CanonicalInstrument = "MES",
            DecisionUtc = utc,
            FrameCreatedUtc = utc,
            BrokerSnapshotCapturedUtc = utc
        });
        if (!frame.IsCleanFlat) return "expected clean-flat frame";
        if (frame.HasTrackedExposure) return "clean flat should not have tracked exposure";
        if (frame.HasUntrackedExposure) return "clean flat should not have untracked exposure";
        if (frame.HasContradiction) return "clean flat should not have contradiction";
        if (frame.FailedPredicates.Count != 0) return "clean flat should not fail predicates";
        return null;
    }

    private static string? Case_TrackedExposure_ExplainedByRobotEvidence()
    {
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MYM",
            BrokerPositionQty = 2,
            BrokerStopQty = 2,
            JournalOpenQty = 2,
            OwnershipOpenQty = 2,
            IeaOwnedPlusAdoptedWorking = 2,
            UseInstrumentExecutionAuthority = true,
            DecisionUtc = DateTimeOffset.UtcNow
        });
        if (frame.IsCleanFlat) return "tracked exposure should not be clean flat";
        if (!frame.HasTrackedExposure) return "expected tracked exposure";
        if (frame.HasUntrackedExposure) return "tracked exposure should not be untracked";
        if (!frame.HasProtectedExposure) return "expected exposure protected by stop qty";
        if (!Contains(frame, "BROKER_POSITION_OPEN")) return "expected broker open predicate";
        return null;
    }

    private static string? Case_UntrackedBrokerExposure_FailsClosed()
    {
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MNG",
            BrokerPositionQty = -1,
            DecisionUtc = DateTimeOffset.UtcNow
        });
        if (!frame.HasUntrackedExposure) return "expected untracked broker exposure";
        if (!frame.HasContradiction) return "expected contradiction for untracked broker exposure";
        if (!Contains(frame, "UNTRACKED_BROKER_EXPOSURE")) return "expected untracked predicate";
        if (!Contains(frame, "UNPROTECTED_BROKER_EXPOSURE")) return "expected unprotected predicate";
        return null;
    }

    private static string? Case_BrokerNetFlatGrossOpen_IsContradiction()
    {
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "NG",
            BrokerPositionQty = 0,
            JournalOpenQty = 3,
            OwnershipOpenQty = 3,
            DecisionUtc = DateTimeOffset.UtcNow
        });
        if (!frame.HasContradiction) return "expected gross-open contradiction while broker net flat";
        if (!Contains(frame, "BROKER_NET_FLAT_BUT_ROBOT_OPEN")) return "expected broker-net-flat gross-open predicate";
        return null;
    }

    private static string? Case_BrokerWorkingOrdersWithoutIeaRegistry_IsContradiction()
    {
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "M2K",
            BrokerWorkingOrdersCount = 2,
            UseInstrumentExecutionAuthority = true,
            DecisionUtc = DateTimeOffset.UtcNow
        });
        if (!frame.HasContradiction) return "expected registry contradiction";
        if (!Contains(frame, "BROKER_WORKING_WITH_EMPTY_IEA_REGISTRY"))
            return "expected broker-working-empty-IEA predicate";
        return null;
    }

    private static string? Case_PlaybackScenarioContext_PreservedInFrame()
    {
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "M2K",
            ExecutionMode = ExecutionMode.SIM.ToString(),
            IsPlayback = true,
            IsMultiDayScenario = true,
            PlaybackScenarioId = "scenario-20260512-20260513",
            DecisionUtc = DateTimeOffset.UtcNow
        });
        if (!frame.IsPlayback) return "expected playback context";
        if (!frame.IsMultiDayScenario) return "expected multi-day scenario context";
        if (frame.ExecutionMode != "SIM") return "expected SIM execution mode";
        if (frame.PlaybackScenarioId != "scenario-20260512-20260513") return "expected scenario id";
        return null;
    }

    private static string? Case_LogPayload_SerializesListsAsText()
    {
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MNQ",
            BrokerPositionQty = 1,
            BrokerOrderIds = new[] { "order-1", "order-2" },
            BrokerOrderTags = new[] { "tag-1", "tag-2" },
            ActiveIntentIds = new[] { "intent-a", "intent-b" },
            DecisionUtc = DateTimeOffset.UtcNow
        });
        var payload = ExecutionAuthorityFrameBuilder.ToLogPayload(frame);
        if (!string.Equals(payload["active_intent_ids"] as string, "intent-a,intent-b", StringComparison.Ordinal))
            return "expected active intent ids to serialize as comma-separated text";
        if (!string.Equals(payload["broker_order_ids"] as string, "order-1,order-2", StringComparison.Ordinal))
            return "expected broker order ids to serialize as comma-separated text";
        if (!string.Equals(payload["broker_order_tags"] as string, "tag-1,tag-2", StringComparison.Ordinal))
            return "expected broker order tags to serialize as comma-separated text";
        var failed = payload["failed_predicates"] as string;
        if (string.IsNullOrWhiteSpace(failed) ||
            failed.Contains("System.", StringComparison.OrdinalIgnoreCase))
            return "expected failed predicates to serialize as readable text";
        return null;
    }

    private static string? Case_RuntimeProofFields_DefaultFromLoadedAssembly()
    {
        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = "test",
            Instrument = "MES",
            DecisionUtc = DateTimeOffset.UtcNow
        });
        if (string.IsNullOrWhiteSpace(frame.ProofLevel))
            return "expected default proof level";
        if (string.IsNullOrWhiteSpace(frame.RuntimeSignatureHash))
            return "expected default runtime signature hash";
        var payload = ExecutionAuthorityFrameBuilder.ToLogPayload(frame);
        if (string.IsNullOrWhiteSpace(payload["proof_level"] as string))
            return "expected proof level in log payload";
        if (string.IsNullOrWhiteSpace(payload["runtime_signature_hash"] as string))
            return "expected runtime signature hash in log payload";
        return null;
    }

    private static bool Contains(ExecutionAuthorityFrame frame, string predicate)
    {
        foreach (var p in frame.FailedPredicates)
        {
            if (string.Equals(p, predicate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
