using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Racka.Core.Config;

namespace Racka.Chat.Teams;

/// <summary>
/// Microsoft Teams through an Azure Bot registration: the Bot Connector service posts activities to
/// /api/messages on <see cref="TeamsConfig.Port"/>, which a tunnel or HTTPS reverse proxy must make public.
/// Replies go out proactively, so a task can run longer than the inbound HTTP request.
/// </summary>
public sealed class TeamsConnector : ChatConnectorBase
{
    private const int MaxMessageLength = 20000;
    private const string BotFrameworkIssuer = "https://api.botframework.com";
    private const string BotFrameworkOpenIdMetadata = "https://login.botframework.com/v1/.well-known/openidconfiguration";
    private readonly TeamsConfig _config;
    private readonly string _agentName;
    private readonly string _clientSecret;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _referencesPath;
    private readonly ConcurrentDictionary<string, ConversationReference> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveGate = new();
    private IChannelAdapter? _adapter;
    private volatile bool _connected;

    public TeamsConnector(TeamsConfig config, string agentName, string stateDirectory, ILoggerFactory loggerFactory)
    {
        _config = config;
        _agentName = agentName;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger($"racka.{agentName}.teams");
        _clientSecret = Environment.GetEnvironmentVariable(config.ClientSecretEnv) is { Length: > 0 } secret
            ? secret
            : throw new ConfigException($"Environment variable '{config.ClientSecretEnv}' (teams.clientSecretEnv) is not set.");
        _referencesPath = Path.Combine(stateDirectory, "teams-conversations.json");
        LoadReferences();
    }

    public override ChatConfig Config => _config;
    public override bool IsConnected => _connected;
    public override string Name => _agentName;

    public override async Task RunAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_loggerFactory);
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Parse(_config.BindAddress), _config.Port));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Connections:ServiceConnection:Settings:AuthType"] = "ClientSecret",
            ["Connections:ServiceConnection:Settings:ClientId"] = _config.AppId,
            ["Connections:ServiceConnection:Settings:ClientSecret"] = _clientSecret,
            ["Connections:ServiceConnection:Settings:AuthorityEndpoint"] = $"https://login.microsoftonline.com/{_config.TenantId}",
            ["Connections:ServiceConnection:Settings:Scopes:0"] = "https://api.botframework.com/.default",
            ["ConnectionsMap:0:ServiceUrl"] = "*",
            ["ConnectionsMap:0:Connection"] = "ServiceConnection"
        });
        builder.Services.AddHttpClient();
        builder.AddAgent(_ => new TeamsAgent(this));
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(ConfigureTokenValidation);
        builder.Services.AddAuthorization();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPost("/api/messages", (HttpRequest request, HttpResponse response, IAgentHttpAdapter adapter, IAgent agent, CancellationToken c) =>
            adapter.ProcessAsync(request, response, agent, c)).RequireAuthorization();

        await app.StartAsync(ct);
        _adapter = (IChannelAdapter)app.Services.GetRequiredService<IAgentHttpAdapter>();
        _connected = true;
        _logger.LogInformation("Teams endpoint listening on http://{Address}:{Port}/api/messages", _config.BindAddress, _config.Port);
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            _connected = false;
            await app.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Only tokens the Bot Connector service issued for this bot (audience = app id) get through.</summary>
    private void ConfigureTokenValidation(JwtBearerOptions options)
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [BotFrameworkIssuer],
            ValidateAudience = true,
            ValidAudiences = [_config.AppId],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true
        };
        options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            BotFrameworkOpenIdMetadata, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                _logger.LogWarning("Rejected a Teams request: {Message}", context.Exception.Message);
                return Task.CompletedTask;
            }
        };
    }

    private void OnActivity(IActivity activity)
    {
        if (activity.Conversation?.Id is not { } conversationId)
            return;
        Remember(conversationId, activity.GetConversationReference());
        if (activity.Type != ActivityTypes.Message || activity.From is null)
            return;

        var isPrivate = activity.Conversation.ConversationType == "personal";
        // A channel thread's conversation id is "<channel>;messageid=<root>"; the channel is the shared history.
        var channelId = conversationId.Split(";messageid=")[0];
        if (!isPrivate && !IsListenedChannel(channelId) && !IsListenedChannel(conversationId))
            return;
        var text = activity.RemoveRecipientMention() ?? activity.Text ?? "";
        var senderId = activity.From.AadObjectId ?? activity.From.Id;
        Raise(new ChatMessage(
            new ChatAddress(conversationId),
            senderId,
            activity.From.Name ?? senderId,
            text.Trim(),
            isPrivate,
            isPrivate ? senderId : channelId,
            // In channels and group chats Teams only delivers messages that mention the bot.
            MentionsMe: !isPrivate));
    }

    public override async Task SendAsync(ChatAddress to, string text, CancellationToken ct)
    {
        var adapter = _adapter ?? throw new InvalidOperationException("The Teams endpoint is not running.");
        if (!_references.TryGetValue(to.ChannelId, out var reference))
            throw new InvalidOperationException(
                $"Teams conversation '{to.ChannelId}' is unknown; the bot can only post where it has received a message or been added.");
        await adapter.ContinueConversationAsync(AgentClaims.CreateIdentity(_config.AppId), reference, async (turn, c) =>
        {
            foreach (var chunk in ChatText.Chunk(text, MaxMessageLength))
            {
                var activity = MessageFactory.Text(chunk);
                activity.TextFormat = TextFormatTypes.Markdown;
                await turn.SendActivityAsync(activity, c);
            }
        }, ct);
    }

    private void Remember(string conversationId, ConversationReference reference)
    {
        _references[conversationId] = reference;
        lock (_saveGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_referencesPath)!);
                var data = _references.ToDictionary(p => p.Key, p => ProtocolJsonSerializer.ToJsonElement(p.Value));
                File.WriteAllText(_referencesPath, JsonSerializer.Serialize(data));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not save Teams conversations: {Message}", e.Message);
            }
        }
    }

    private void LoadReferences()
    {
        if (!File.Exists(_referencesPath))
            return;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_referencesPath)) ?? [];
            foreach (var (id, element) in data)
                _references[id] = ProtocolJsonSerializer.ToObject<ConversationReference>(element);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            _logger.LogWarning("Ignoring unreadable {Path}: {Message}", _referencesPath, e.Message);
        }
    }

    private sealed class TeamsAgent(TeamsConnector connector) : IAgent
    {
        public Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken)
        {
            connector.OnActivity(turnContext.Activity);
            return Task.CompletedTask;
        }
    }
}
