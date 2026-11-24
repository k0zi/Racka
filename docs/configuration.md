# Konfiguráció

## Könyvtárszerkezet

```
agents/<név>/
  agent.yaml               konfiguráció (kötelező)
  soul.md                  személyiség, a system prompt eleje (kötelező)
  skills/<skill>/SKILL.md  saját skillek, mindig betöltődnek
  memory/<memória>.md      hosszú távú memória (futás közben jön létre)
  state/                   session-id-k, generált mcp.json, saját toolok állapota
  workspace/               alapértelmezett munkakönyvtár
skills/<skill>/SKILL.md    közös skillek; az agent.yaml `skills` listájával kérhetők
```

Az agentet a könyvtára azonosítja: `racka run agents/<név>`. A relatív utak (`workdir`, `sharedSkillsDir`,
`memory.dir`) az agent könyvtárához képest értendők.

## Környezeti változók a configban

Minden szöveges mezőben használható `${VAR}` és `${VAR:-alapérték}`. Ha egy változó hiányzik, és nincs
alapértéke, az agent el sem indul, hanem érthető hibát ad. A titkok csak így kerüljenek a configba:

```yaml
mcpServers:
  github: { command: github-mcp, env: { GITHUB_TOKEN: "${GITHUB_TOKEN}" } }
model: ${LAMB_MODEL:-qwen3:8b}
```

## agent.yaml referencia

### Általános

| Mező | Alapérték | Leírás |
|---|---|---|
| `name` | a könyvtár neve | kisbetű, szám, `-`, `_`; legfeljebb 32 karakter. Ezen a néven éri el a busz. |
| `engine` | `anthropic` | `claude-code`, `codex`, `anthropic` vagy `openai` ([Engine-ek](engines.md)) |
| `model` | engine-függő | `anthropic`: `claude-opus-5-5`; `openai`: kötelező; a CLI engine-eknél a CLI saját alapértéke |
| `effort` | – | `low`, `medium`, `high`, `xhigh`, `max` (Codexnél a `max` értékből `xhigh` lesz) |
| `maxTokens` | `16000` | az API engine-ek válaszhossza |
| `maxIterations` | `20` | az API engine-ek tool-loop korlátja feladatonként |
| `workdir` | `./workspace` | munkakönyvtár; a CLI engine-ek itt dolgoznak, az MCP stdio szerverek itt indulnak |
| `skills` | `[]` | közös skillek nevei a `sharedSkillsDir` alól |
| `sharedSkillsDir` | `../../skills` | a közös skillek mappája |
| `mcpServers` | `{}` | MCP szerverek, lásd lent |
| `memory` | bekapcsolva | lásd lent |
| `irc` | nincs | lásd [IRC](irc.md) |

### `mcpServers`

Minden bejegyzéshez pontosan egy `command` vagy `url` kell.

```yaml
mcpServers:
  fs:                                   # stdio szerver
    command: npx
    args: ["-y", "@modelcontextprotocol/server-filesystem", "."]
    env: { DEBUG: "0" }
  docs:                                 # streamable HTTP szerver
    url: https://example.com/mcp
    headers: { Authorization: "Bearer ${DOCS_TOKEN}" }
```

A toolok neve az API engine-eknél `<szerver>__<tool>`, a Claude Code-nál `mcp__<szerver>__<tool>`. A Codex a
saját elnevezését használja. A Codex agentek szervernevében csak betű, szám, `-` és `_` lehet.

### `memory`

```yaml
memory:
  enabled: true          # alapból be van kapcsolva
  dir: ./memory
  maxIndexChars: 8000    # ennyi fér a system promptba kerülő indexből
```

Részletek: [Memória](memory.md).

### `anthropic`

```yaml
anthropic:
  baseUrl: null                # pl. proxy vagy gateway
  apiKeyEnv: ANTHROPIC_API_KEY # ha üres, az SDK saját feloldása (ant auth login profil)
  serverSideFallbacks: true
```

A `serverSideFallbacks` az Opus 5.5, Opus 5, Fable 5.1 és Sonnet 5.5 modelleknél működik. Ha a modell
biztonsági okból visszautasít egy kérést, az API ugyanabban a hívásban egy másik modellel válaszol.

### `openai`

```yaml
openai:
  baseUrl: http://localhost:11434/v1   # Ollama; LM Studio: :1234/v1; vLLM: :8000/v1; üres = api.openai.com
  apiKeyEnv: OPENAI_API_KEY            # baseUrl mellett elhagyható, a helyi szerverek nem kérik
```

### `claudeCode`

```yaml
claudeCode:
  executable: claude
  permissionMode: dontAsk      # acceptEdits | auto | bypassPermissions | manual | dontAsk | plan
  allowedTools: [Read, Grep, "Bash(git diff:*)"]
  disallowedTools: []
  maxTurns: 30
  maxBudgetUsd: 2.0            # feladatonkénti költségkorlát
  strictMcpConfig: true        # csak az itt megadott MCP szerverek, a globálisak nem
  extraArgs: []                # további `claude` kapcsolók
```

A Racka bridge (`mcp__racka`) és az agent saját MCP szerverei automatikusan engedélyezettek.

### `codex`

```yaml
codex:
  executable: codex
  sandbox: workspace-write     # read-only | workspace-write | danger-full-access
  approvalPolicy: never        # untrusted | on-failure | on-request | never
  trustWorkdir: true           # a workdir megbízható projekt erre a futásra (a Codex configba nem ír)
  skipGitRepoCheck: true       # git repón kívül is fusson
  ignoreUserConfig: false      # true: a ~/.codex/config.toml-t (és MCP szervereit) kihagyja
  config:                      # tetszőleges `-c kulcs=érték`; az érték TOML
    model_verbosity: '"low"'
  extraArgs: []                # további `codex exec` kapcsolók
```

### `irc`

Lásd [IRC](irc.md#konfiguráció).

## Futásidejű környezeti változók

| Változó | Hatás |
|---|---|
| `RACKA_RUNTIME_DIR` | a socketek könyvtára (alapértelmezés: `$XDG_RUNTIME_DIR/racka`, ennek hiányában `/tmp/racka-$USER`) |
| `RACKA_EXECUTABLE` | saját hostból futó CLI engine-ek bridge-parancsa (lásd [Bővítés](extending.md#saját-host)) |
| `RACKA_AGENT`, `RACKA_TASK_ID` | a Racka beállítja a `claude` és `codex` processznek; hookok és szkriptek olvashatják |

A Unix socket útvonala legfeljebb 107 bájt lehet. Ha túl hosszú, a hibaüzenet megmondja, mire állítsd a
`RACKA_RUNTIME_DIR` változót.
