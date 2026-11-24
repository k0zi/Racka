using System.Text;
using Racka.Core.Agents;

namespace Racka.Core.Engine;

public sealed record PromptOptions(bool IncludeSkillIndex, bool CanMessageAgents, bool HasIrc);

/// <summary>
/// Builds the system prompt and the per-task user message. The system prompt only changes when the memory index
/// changes, so it stays cacheable; engines rebuild it for each new conversation to pick up new memories.
/// </summary>
public static class PromptBuilder
{
    public static string BuildSystemPrompt(AgentDefinition agent, PromptOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine(agent.Soul);
        sb.AppendLine();
        sb.AppendLine("# Racka runtime");
        sb.AppendLine($"You are the agent named \"{agent.Name}\" in the Racka multi-agent framework.");
        sb.AppendLine("Each message you receive starts with a header naming where the task came from (cli, bus or irc) and who sent it.");
        sb.AppendLine("Your final answer is delivered back to that sender, so finish with a clear, self-contained result.");
        if (options.CanMessageAgents)
        {
            sb.AppendLine("You can reach other agents with the send_message tool and discover them with list_agents.");
            sb.AppendLine("Delegate only when another agent is better suited; never forward a task back to the agent that sent it.");
        }
        if (options.HasIrc)
            sb.AppendLine("You are connected to IRC. Use irc_report for short status updates that humans should see; keep them to one line.");

        if (options.IncludeSkillIndex && agent.Skills.All.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Skills");
            sb.AppendLine("Skills are packaged instructions. When a task matches a skill, call load_skill with its name before you start, then follow it.");
            foreach (var skill in agent.Skills.All.OrderBy(s => s.Name, StringComparer.Ordinal))
                sb.AppendLine($"- {skill.Name}: {skill.Description}");
        }

        if (agent.Memory is { } memory)
        {
            sb.AppendLine();
            sb.AppendLine("# Memory");
            sb.AppendLine("You have a persistent memory that survives restarts and conversations: memory_save, memory_read, memory_search, memory_delete.");
            sb.AppendLine("Save a memory when you learn something you will want in a later conversation: who you work with and what they prefer, " +
                "corrections you received, decisions and their reasons, where things are. Do not save what only matters for the current task " +
                "or what you can easily look up again. Keep one memory per topic: update it instead of adding a near-duplicate, " +
                "and delete memories that turn out to be wrong.");
            sb.AppendLine("Memories reflect what was true when they were written; read one with memory_read before relying on it, and check it against the present when it matters.");
            var index = memory.BuildIndex(agent.Config.Memory.MaxIndexChars);
            sb.AppendLine(index.Length > 0 ? "Your memories:\n" + index : "You have no memories yet.");
        }
        return sb.ToString().TrimEnd();
    }

    public static string BuildUserMessage(AgentTask task)
    {
        var from = task.From is null ? task.Source : $"{task.Source}:{task.From}";
        return $"[task {task.Id} from {from}]\n{task.Text}";
    }
}
