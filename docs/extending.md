# Bővítés

A Racka könyvtárként is használható. A `racka` CLI csak egy vékony réteg az `AgentHost`, a `BusClient` és az
engine-ek fölött.

## Saját tool

Egy tool egy névből, egy leírásból (ezt olvassa a modell), egy JSON schemából és egy `InvokeAsync` metódusból áll.
A `RackaTool` alaposztály parse-olja a schemát, és argumentum-segédeket ad:

```csharp
public sealed class DiceTool() : RackaTool(
    "roll_dice",
    "Roll dice, e.g. to pick a random reviewer fairly.",
    """{"type":"object","properties":{"sides":{"type":"integer","description":"Number of sides (default 6)."}}}""")
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var sides = OptionalInt(arguments, "sides", 6);
        if (sides < 2)
            return Task.FromResult(ToolResult.Error("sides must be at least 2"));   // a modell látja és javíthat
        return Task.FromResult(ToolResult.Ok(Random.Shared.Next(1, sides + 1).ToString()));
    }
}
```

- `RequireString`, `OptionalString`, `OptionalBool`, `OptionalInt`: argumentum-olvasók. Ha egy kötelező argumentum
  hiányzik, a hibát a modell kapja vissza.
- `ToolResult.Error(...)`: olyan hiba, amelyet a modellnek látnia kell, és javíthat rajta. Kivételt csak valódi
  hibánál dobj; a Racka azt is hibás eredménnyé alakítja.
- `ToolContext`: az agent definíciója (`Agent`), az aktuális feladat (`Task.From`, `Task.Source`, `Task.Chain`) és
  az eseménybusz (`Events`, például `Progress` eseményhez).
- A toolok egy körön belül **párhuzamosan** futhatnak. Ha állapotot tartanak, zárolniuk kell.

A teljes minta, egy JSON-ban tárolt ticket-tábla három toollal: [`samples/dotnet/CustomTool`](../samples/dotnet/CustomTool/TicketTools.cs).

## Saját host

Az `AgentHost.RunAsync` ugyanazt az agentet indítja, mint a `racka run`, de a hívó további toolokat adhat meg:

```csharp
var settings = UserSettings.Load();   // ~/.config/racka/config.yaml: chat-kapcsolatok, runtimeDir
var agent = AgentDefinition.Load("agents/shepherd", layers: ConfigLayers.From(settings));
await AgentHost.RunAsync(agent, BusPaths.Resolve(null, settings.RuntimeDir), AgentEngineFactory.Create, ct,
    extraTools: [new DiceTool(), new TicketCreateTool(board)]);
```

A `layers` nélkül csak az `agent.yaml` számít, a felhasználói beállítások nem.

Az `extraTools` **mindegyik engine-nel működik**. Az API engine-ek közvetlenül hívják őket, a Claude Code és a
Codex pedig a bridge-en át. A bridge-et a CLI engine a `racka` paranccsal indítja. Saját hostnál ez a projektre
hivatkozó `Racka.Host` mellé másolt `racka` fájl lesz, amelyet a `RACKA_EXECUTABLE` változó felülírhat. A
bridge a saját hostod socketjéhez kapcsolódik, így a saját toolokat is látja.

Projekt-hivatkozás: `<ProjectReference Include="…/src/Racka.Host/Racka.Host.csproj" />`
(ez behúzza a `Racka.Core`, `Racka.Providers` és `Racka.Chat` projekteket is).

Saját engine-factoryval a toolok engine-enként is szűrhetők, vagy saját `IAgentEngine` adható vissza:

```csharp
EngineFactory factory = (agent, tools, events, prompt, bridge) =>
    agent.Config.EngineKind == EngineKind.OpenAI
        ? new ApiLoopEngine(agent, new MyProvider(), tools, events, prompt)
        : AgentEngineFactory.Create(agent, tools, events, prompt, bridge);
```

## Agentek hívása kódból

```csharp
using var bus = new BusClient(new BusPaths());
var reply = await bus.SendAsync(new BusMessage(
    Id: Guid.NewGuid().ToString("n")[..12], From: "my-app", To: "shaun",
    Kind: MessageKinds.Ask, Text: "Mi a státusz?", ConversationKey: "my-app", TimeoutSeconds: 300), ct);
Console.WriteLine(reply.Success ? reply.Output : reply.Error);
```

`ListAgentsAsync`, `GetStatusAsync`, `CancelAsync`, `ListToolsAsync`, `InvokeToolAsync`: a teljes minta az
[ExternalClient](../samples/dotnet/ExternalClient/Program.cs).

## HerdFlow pipeline agentekből

Ha egy munkamenet lépései rögzítettek, a sorrendet egy HerdFlow flow adja meg, minden node pedig egy agentnek
küldött ask. A flow elágazik az eredmény alapján, és hurkot is tartalmazhat:

```csharp
implement.Then(review);
review.On("changes").Then(fix);
fix.Then(review);
review.On("approved").Then(changelog);
await new AsyncFlow(implement).RunAsync(state);
```

A HerdFlow a node-okat futás közben klónozza (`MemberwiseClone`), ezért a változó állapot a `shared` objektumba
kerüljön, a node mezői pedig maradjanak readonly függőségek. A teljes, futtatható minta:
[ReviewPipeline](../samples/dotnet/ReviewPipeline/Nodes.cs).

## Új LLM provider

Az API engine bármely chat-modellel működik, amelyhez van `ILlmProvider`:

```csharp
public interface ILlmProvider
{
    IConversation CreateConversation(string systemPrompt);
    Task<LlmTurn> NextTurnAsync(IConversation conversation, IReadOnlyList<IRackaTool> tools, CancellationToken ct);
}
```

- Az `IConversation` a provider natív üzenettípusaiban tárolja a beszélgetést, csak hozzáfűzéssel
  (`AddUserText`, `AddToolResults`). A `NextTurnAsync` sikeres kör után maga fűzi hozzá az asszisztens válaszát.
- Az `LlmTurn` a szövegből, a tool-hívásokból (`ToolCall(Id, Name, JsonElement Arguments)`), a stop reasonből és a
  tokenhasználatból áll. Ha nincs tool-hívás, a feladat kész.
- A tool-hurkot, a párhuzamos tool-futtatást, a hibakezelést és a `maxIterations` korlátot az `ApiLoopEngine` adja.

Minta: `src/Racka.Providers/OpenAI/OpenAIProvider.cs` (kb. 80 sor). A tesztekben a `FakeLlmProvider` mutatja a
legkisebb működő változatot.

## Új CLI engine

Egy új agent-CLI-t (például egy másik kódoló agentet) egy `IAgentEngine` implementáció köt be. A
`CodexEngine` jó kiindulópont:

1. A system promptot (`PromptBuilder.BuildSystemPrompt`) a CLI saját utasítás-mechanizmusával add át.
2. A Racka toolokat a bridge-dzsel add át MCP szerverként (`BridgeCommand`).
3. A CLI gépi kimenetét (JSON-sorok) parse-old eseményekké és végeredménnyé.
4. A session-id-t a `SessionStore` tárolja beszélgetés-kulcsonként.
5. Az `EngineKind` enumba, az `IsCli()` kiterjesztésbe és az `AgentEngineFactory`-be vedd fel az új engine-t.
