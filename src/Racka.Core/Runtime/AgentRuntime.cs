using System.Threading.Channels;
using Racka.Core.Agents;
using Racka.Core.Engine;
using Racka.Core.Messaging;

namespace Racka.Core.Runtime;

/// <summary>
/// The agent's inbox: tasks from the bus, IRC and the CLI are queued and run one at a time by the engine.
/// </summary>
public sealed class AgentRuntime
{
    private readonly Channel<QueuedTask> _inbox = Channel.CreateUnbounded<QueuedTask>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private int _queueLength;
    private QueuedTask? _current;
    private int _completed;
    private int _failed;

    public AgentRuntime(AgentDefinition agent, IAgentEngine engine, IAgentEvents events)
    {
        Agent = agent;
        Engine = engine;
        Events = events;
    }

    public AgentDefinition Agent { get; }
    public IAgentEngine Engine { get; }
    public IAgentEvents Events { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public Func<bool> IrcConnected { get; set; } = () => false;

    public AgentTask? CurrentTask
    {
        get { lock (_gate) return _current?.Task; }
    }

    /// <summary>Queues a task; the returned task completes with its result.</summary>
    public Task<AgentResult> SubmitAsync(AgentTask task)
    {
        var queued = new QueuedTask(task);
        Interlocked.Increment(ref _queueLength);
        if (!_inbox.Writer.TryWrite(queued))
        {
            Interlocked.Decrement(ref _queueLength);
            return Task.FromResult(AgentResult.Failed(task.Id, "agent is shutting down"));
        }
        Events.Publish(new AgentEvent(AgentEventType.TaskQueued, Agent.Name, task.Id, $"from {task.Source}:{task.From}"));
        return queued.Completion.Task;
    }

    /// <summary>Cancels the running task. Returns false when idle.</summary>
    public bool CancelCurrent()
    {
        lock (_gate)
        {
            if (_current == null)
                return false;
            _current.Cancellation.Cancel();
            return true;
        }
    }

    /// <summary>
    /// An ask whose chain already contains this agent would wait on itself: the agent is
    /// blocked in a task that is waiting (directly or transitively) for the asker.
    /// </summary>
    public bool WouldDeadlock(BusMessage message) =>
        message.Kind == MessageKinds.Ask && (message.Chain ?? []).Contains(Agent.Name);

    public AgentStatus GetStatus()
    {
        lock (_gate)
        {
            return new AgentStatus(
                Agent.Name,
                Agent.Config.Engine,
                Agent.Config.Model,
                _current == null ? "idle" : "busy",
                _current?.Task.Id,
                _current?.Task.From,
                Math.Max(0, Volatile.Read(ref _queueLength) - (_current == null ? 0 : 1)),
                StartedAt,
                Environment.ProcessId,
                IrcConnected(),
                Volatile.Read(ref _completed),
                Volatile.Read(ref _failed));
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        Events.Publish(new AgentEvent(AgentEventType.AgentStarted, Agent.Name, null, $"{Agent.Config.Engine} engine ready"));
        try
        {
            await foreach (var queued in _inbox.Reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(queued, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _inbox.Writer.TryComplete();
            while (_inbox.Reader.TryRead(out var pending))
                pending.Completion.TrySetResult(AgentResult.Failed(pending.Task.Id, "agent stopped"));
            Events.Publish(new AgentEvent(AgentEventType.AgentStopping, Agent.Name, null, "stopping"));
        }
    }

    private async Task ProcessAsync(QueuedTask queued, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, queued.Cancellation.Token);
        lock (_gate) _current = queued;
        var task = queued.Task;
        Events.Publish(new AgentEvent(AgentEventType.TaskStarted, Agent.Name, task.Id, Summarize(task.Text)));

        AgentResult result;
        try
        {
            result = await Engine.RunAsync(task, linked.Token);
        }
        catch (Exception e)
        {
            result = AgentResult.Failed(task.Id, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            lock (_gate) _current = null;
            Interlocked.Decrement(ref _queueLength);
            queued.Cancellation.Dispose();
        }

        if (result.Success)
        {
            Interlocked.Increment(ref _completed);
            Events.Publish(new AgentEvent(AgentEventType.TaskCompleted, Agent.Name, task.Id, Summarize(result.Output)));
        }
        else
        {
            Interlocked.Increment(ref _failed);
            Events.Publish(new AgentEvent(AgentEventType.TaskFailed, Agent.Name, task.Id, result.Error ?? "failed"));
        }
        queued.Completion.TrySetResult(result);
    }

    internal static string Summarize(string text, int max = 120)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }

    private sealed class QueuedTask(AgentTask task)
    {
        public AgentTask Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<AgentResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
