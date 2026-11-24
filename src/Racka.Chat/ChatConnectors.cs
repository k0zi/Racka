using Microsoft.Extensions.Logging;
using Racka.Chat.Discord;
using Racka.Chat.Irc;
using Racka.Chat.Slack;
using Racka.Chat.Teams;
using Racka.Chat.Telegram;
using Racka.Core.Agents;
using Racka.Core.Config;

namespace Racka.Chat;

public static class ChatConnectors
{
    /// <summary>One connector per enabled chat section of the agent. Missing secrets fail here, before the agent starts.</summary>
    public static IReadOnlyList<IChatConnector> Create(AgentDefinition agent, ILoggerFactory loggerFactory)
    {
        var connectors = new List<IChatConnector>();
        try
        {
            foreach (var config in agent.Config.ChatConnections)
            {
                connectors.Add(config switch
                {
                    IrcConfig irc => new IrcConnector(irc, agent.Name, loggerFactory),
                    DiscordConfig discord => new DiscordConnector(discord, agent.Name, loggerFactory),
                    SlackConfig slack => new SlackConnector(slack, agent.Name, loggerFactory),
                    TeamsConfig teams => new TeamsConnector(teams, agent.Name, agent.StateDirectory, loggerFactory),
                    TelegramConfig telegram => new TelegramConnector(telegram, agent.Name, loggerFactory),
                    _ => throw new ConfigException($"Unsupported chat platform '{config.Platform}'.")
                });
            }
        }
        catch
        {
            foreach (var connector in connectors)
                connector.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        return connectors;
    }
}
