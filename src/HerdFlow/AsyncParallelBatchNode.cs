// Ported from PocketFlow (https://github.com/The-Pocket/PocketFlow), Copyright (c) 2024 Zachary Huang, MIT; see THIRD_PARTY_NOTICES.md.
namespace HerdFlow;

public class AsyncParallelBatchNode : AsyncBatchNode
{
    public AsyncParallelBatchNode(int maxRetries = 1, int wait = 0) : base(maxRetries, wait) { }

    internal override async Task<object?> _ExecAsync(object? items)
    {
        var enumerable = items as System.Collections.IEnumerable;
        if (enumerable == null) return new List<object?>();
        
        var tasks = new List<Task<object?>>();
        foreach (var item in enumerable)
        {
            tasks.Add(_ExecSingleAsync(item));
        }
        return (await Task.WhenAll(tasks)).ToList();
    }
}