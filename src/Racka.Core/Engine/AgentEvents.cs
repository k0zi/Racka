namespace Racka.Core.Engine;

public enum AgentEventType
{
    AgentStarted,
    AgentStopping,
    TaskQueued,
    TaskStarted,
    ToolCalled,
    Progress,
    TaskCompleted,
    TaskFailed,
    Report
}

public sealed record AgentEvent(AgentEventType Type, string Agent, string? TaskId, string Message)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public interface IAgentEvents
{
    void Publish(AgentEvent e);
}

/// <summary>Fan-out of agent events to subscribers (logging, IRC reporter, status endpoint).</summary>
public sealed class AgentEventHub : IAgentEvents
{
    private readonly object _gate = new();
    private readonly List<Action<AgentEvent>> _subscribers = new();

    public IDisposable Subscribe(Action<AgentEvent> handler)
    {
        lock (_gate) _subscribers.Add(handler);
        return new Subscription(() => { lock (_gate) _subscribers.Remove(handler); });
    }

    public void Publish(AgentEvent e)
    {
        Action<AgentEvent>[] snapshot;
        lock (_gate) snapshot = _subscribers.ToArray();
        foreach (var subscriber in snapshot)
        {
            try { subscriber(e); }
            catch { /* a failing subscriber must not break the agent */ }
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class NullAgentEvents : IAgentEvents
{
    public static readonly NullAgentEvents Instance = new();
    public void Publish(AgentEvent e) { }
}
