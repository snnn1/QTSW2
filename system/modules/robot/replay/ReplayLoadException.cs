namespace QTSW2.Robot.Replay;

/// <summary>Thrown when replay file validation fails. Fail-fast semantics.</summary>
public sealed class ReplayLoadException : Exception
{
    public int LineNumber { get; }
    public string? EventType { get; }
    public string ValidationError { get; }

    public ReplayLoadException(string message, int lineNumber = 0, string? eventType = null)
        : base(message)
    {
        LineNumber = lineNumber;
        EventType = eventType;
        ValidationError = message;
    }

    public ReplayLoadException(string message, Exception inner)
        : base(message, inner)
    {
        LineNumber = 0;
        EventType = null;
        ValidationError = message;
    }
}
