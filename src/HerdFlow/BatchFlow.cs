// Ported from PocketFlow (https://github.com/The-Pocket/PocketFlow), Copyright (c) 2024 Zachary Huang, MIT; see THIRD_PARTY_NOTICES.md.
namespace HerdFlow;

public class BatchFlow : Flow
{
    public BatchFlow(BaseNode? start = null) : base(start) { }

    internal override object? InternalRun(object shared)
    {
        var pr = Prepare(shared) as IEnumerable<Dictionary<string, object>> ?? Array.Empty<Dictionary<string, object>>();
        foreach (var bp in pr)
        {
            var p = new Dictionary<string, object>(Params);
            foreach (var kvp in bp) p[kvp.Key] = kvp.Value;
            _Orch(shared, p);
        }
        return Post(shared, pr, null);
    }
}