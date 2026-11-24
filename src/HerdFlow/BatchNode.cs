// Ported from PocketFlow (https://github.com/The-Pocket/PocketFlow), Copyright (c) 2024 Zachary Huang, MIT; see THIRD_PARTY_NOTICES.md.
namespace HerdFlow;

public class BatchNode : Node
{
    public BatchNode(int maxRetries = 1, int wait = 0) : base(maxRetries, wait) { }

    internal override object? InternalExecute(object? items)
    {
        var enumerable = items as System.Collections.IEnumerable;
        if (enumerable == null) return new List<object?>();
        var results = new List<object?>();
        foreach (var item in enumerable)
        {
            results.Add(base.InternalExecute(item));
        }
        return results;
    }
}