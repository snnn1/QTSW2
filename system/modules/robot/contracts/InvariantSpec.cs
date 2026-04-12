using System.Collections.Generic;

namespace QTSW2.Robot.Contracts
{
    /// <summary>
    /// Invariant expectations for incident replay. Loaded from expected.json.
    /// </summary>
    public sealed class InvariantSpec
    {
        public List<InvariantExpectation> Invariants { get; set; } = new();
    }

    public sealed class InvariantExpectation
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public Dictionary<string, object> Params { get; set; } = new();
        public Dictionary<string, object> Expected { get; set; } = new();
    }
}
