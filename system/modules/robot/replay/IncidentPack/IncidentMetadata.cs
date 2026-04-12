using System.Collections.Generic;

namespace QTSW2.Robot.Replay.IncidentPack;

public sealed class IncidentMetadata
{
    public string IncidentId { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string SourceLog { get; set; } = "";
    public string? Account { get; set; }
    public string ExecutionInstrumentKey { get; set; } = "";
    public SliceBound? Start { get; set; }
    public SliceBound? End { get; set; }
    public SelectorInfo? Selector { get; set; }
    public ErrorSignature? ErrorSignature { get; set; }
    public string CanonicalSha256 { get; set; } = "";
    public string? HostVersion { get; set; }
}

public sealed class SliceBound
{
    public long Sequence { get; set; }
    public string TsUtc { get; set; } = "";
}

public sealed class SelectorInfo
{
    public string? ErrorEventType { get; set; }
    public string? MessageContains { get; set; }
    public string? Instrument { get; set; }
    public string? Account { get; set; }
    public int PreEvents { get; set; }
    public int PostEvents { get; set; }
}

public sealed class ErrorSignature
{
    public string? EventType { get; set; }
    public string? MessageContains { get; set; }
    public string? Code { get; set; }
}
