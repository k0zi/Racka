using System.Text;
using System.Text.Json;
using Racka.Core.Tools;

namespace Racka.Samples.CustomTool;

public sealed record Ticket(int Id, string Title, string Assignee, string CreatedBy, DateTimeOffset Created, bool Closed = false, string? Resolution = null);

/// <summary>A tiny ticket board persisted as JSON. Tool calls can run in parallel, so access is locked.</summary>
public sealed class TicketBoard(string path)
{
    private readonly Lock _gate = new();

    public Ticket Create(string title, string assignee, string createdBy)
    {
        lock (_gate)
        {
            var tickets = Load();
            var ticket = new Ticket(tickets.Count == 0 ? 1 : tickets.Max(t => t.Id) + 1, title, assignee, createdBy, DateTimeOffset.UtcNow);
            tickets.Add(ticket);
            Save(tickets);
            return ticket;
        }
    }

    public IReadOnlyList<Ticket> List()
    {
        lock (_gate) return Load();
    }

    public Ticket? Close(int id, string resolution)
    {
        lock (_gate)
        {
            var tickets = Load();
            var index = tickets.FindIndex(t => t.Id == id);
            if (index < 0)
                return null;
            tickets[index] = tickets[index] with { Closed = true, Resolution = resolution };
            Save(tickets);
            return tickets[index];
        }
    }

    private List<Ticket> Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText(path)) ?? [] : [];

    private void Save(List<Ticket> tickets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(tickets, new JsonSerializerOptions { WriteIndented = true }));
    }
}

// A tool is a name, a description the model reads, a JSON schema for the input and InvokeAsync.
// RackaTool parses the schema literal and offers argument helpers; return ToolResult.Error for
// problems the model should see and fix, and throw only for real failures.
public sealed class TicketCreateTool(TicketBoard board) : RackaTool(
    "ticket_create",
    "Open a ticket on the flock's board and assign it to an agent.",
    """
    {"type":"object","properties":{
      "title":{"type":"string","description":"What needs to be done, in one sentence."},
      "assignee":{"type":"string","description":"Agent name, e.g. merino."}
    },"required":["title","assignee"]}
    """)
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        // The task context says who asked: an agent over the bus, someone on IRC or the CLI.
        var createdBy = context.Task.From ?? context.Task.Source;
        var ticket = board.Create(RequireString(arguments, "title"), RequireString(arguments, "assignee"), createdBy);
        return Task.FromResult(ToolResult.Ok($"Opened ticket #{ticket.Id} for {ticket.Assignee}."));
    }
}

public sealed class TicketListTool(TicketBoard board) : RackaTool(
    "ticket_list",
    "List tickets, optionally only one agent's or only open ones.",
    """
    {"type":"object","properties":{
      "assignee":{"type":"string","description":"Only this agent's tickets."},
      "include_closed":{"type":"boolean","description":"Also list closed tickets (default false)."}
    }}
    """)
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var assignee = OptionalString(arguments, "assignee");
        var includeClosed = OptionalBool(arguments, "include_closed", false);
        var tickets = board.List()
            .Where(t => assignee == null || t.Assignee.Equals(assignee, StringComparison.OrdinalIgnoreCase))
            .Where(t => includeClosed || !t.Closed)
            .ToList();
        if (tickets.Count == 0)
            return Task.FromResult(ToolResult.Ok("No matching tickets."));

        var sb = new StringBuilder();
        foreach (var t in tickets)
            sb.AppendLine($"#{t.Id} [{(t.Closed ? "closed" : "open")}] {t.Assignee}: {t.Title} (by {t.CreatedBy}){(t.Resolution is { } r ? " – " + r : "")}");
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }
}

public sealed class TicketCloseTool(TicketBoard board) : RackaTool(
    "ticket_close",
    "Close a ticket with a short resolution.",
    """
    {"type":"object","properties":{
      "id":{"type":"integer","description":"Ticket number."},
      "resolution":{"type":"string","description":"What was done."}
    },"required":["id","resolution"]}
    """)
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var id = OptionalInt(arguments, "id", -1);
        var ticket = board.Close(id, RequireString(arguments, "resolution"));
        return Task.FromResult(ticket == null ? ToolResult.Error($"No ticket #{id}.") : ToolResult.Ok($"Closed ticket #{id}."));
    }
}
