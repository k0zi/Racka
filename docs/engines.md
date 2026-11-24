# Engine-ek

A négy engine kívülről egyforma: ugyanúgy kapnak feladatot, és ugyanúgy beszélnek a többi agenttel.
Belül kétféle a működésük:

- Az **API engine-ek** (`anthropic`, `openai`) tool-hurkát a Racka futtatja HerdFlow node-okkal. A toolokat
  (MCP, skillek, Racka toolok) közvetlenül a Racka hívja meg.
- A **CLI engine-ek** (`claude-code`, `codex`) saját tool-hurkot futtatnak saját toolokkal: fájlkezelés, shell,
  webes keresés. A Racka toolokat a `racka bridge` stdio MCP szerveren át érik el, amely az agent socketjére továbbít.

| | `claude-code` | `codex` | `anthropic` | `openai` |
|---|---|---|---|---|
| Hurok | Claude Code | Codex | HerdFlow | HerdFlow |
| Fájl, shell | igen (`allowedTools`) | igen (sandbox) | MCP-n át | MCP-n át |
| Racka toolok | bridge | bridge | közvetlenül | közvetlenül |
| Skillek | natívan (symlink) | `load_skill` (bridge) | `load_skill` | `load_skill` |
| Beszélgetés megőrzése | session-id, újraindítás után is | thread-id, újraindítás után is | memóriában (újraindításkor elvész) | memóriában |
| Költség | Claude előfizetés/API | ChatGPT-fiók/API | API (token) | API vagy ingyen (helyi) |

## `claude-code`

Feladatonként egy `claude -p --output-format stream-json` processz indul a `workdir` könyvtárban:

- a soul és a runtime-utasítás a `--append-system-prompt` kapcsolóval megy át, a feladat stdinre kerül;
- a `stream-json` eseményekből lesz a napló (tool-hívások, közbenső szöveg) és a végeredmény (szöveg, tokenek, költség);
- a session-id beszélgetés-kulcsonként a `state/sessions.json` fájlba kerül, így a következő feladat `--resume`-mal folytatja; ha a session már nem létezik, egyszer újrakezdi nélküle;
- a skillek symlinkként kerülnek a `workspace/.claude/skills` alá, ezeket a Claude Code natívan felfedezi;
- az MCP szerverek és a Racka bridge a generált `state/mcp.json`-ból jönnek. `strictMcpConfig: true` mellett a felhasználó globális MCP szerverei nem töltődnek be.

**Jogosultság.** Print módban senki nem tud engedélyt adni, ezért az számít, mi szerepel az `allowedTools` listán
és mi a `permissionMode` értéke. Reviewernek a `dontAsk` mód és egy szűk lista ajánlott (lásd `agents/dolly`),
fejlesztőnek az `acceptEdits` mód. A `maxBudgetUsd` feladatonkénti költségplafon.

## `codex`

Feladatonként egy `codex exec --json` processz indul a `workdir` könyvtárban:

- a soul és a runtime-utasítás `-c developer_instructions="…"` formában megy át (TOML-escape-elve), a feladat stdinre kerül;
- a JSONL eseményekből lesz a napló (`command_execution`, `mcp_tool_call`, `file_change`), a végeredmény pedig az utolsó `agent_message`, a tokenekkel együtt;
- a thread-id beszélgetés-kulcsonként a `state/codex-sessions.json` fájlba kerül, a folytatás `codex exec resume <id>`; ha a thread nem tölthető be, egyszer újrakezdi nélküle;
- az MCP szerverek és a bridge `-c mcp_servers.<név>={…}` felülírásként mennek át, `default_tools_approval_mode = "approve"` beállítással, hogy a toolok jóváhagyás nélkül fussanak;
- a skillek listája az utasításokba kerül, a tartalmukat a `load_skill` és `read_skill_file` tool tölti be a bridge-en át.

**Jogosultság.** A határt a `sandbox` húzza meg. A `workspace-write` beállítás csak a `workdir`-be enged írni.
Az `approvalPolicy: never` azért az alapérték, mert exec módban nincs, aki jóváhagyjon. A `trustWorkdir`
erre a futásra megbízhatónak jelöli a munkakönyvtárat, a felhasználó Codex configjába nem ír.
A `ignoreUserConfig: true` kihagyja a `~/.codex/config.toml` fájlt a benne lévő MCP szerverekkel együtt;
a bejelentkezés ekkor is működik.

A Codex helyi modellel is futhat (`--oss`, Ollama vagy LM Studio). Ezt a saját Codex configodban állítsd be,
vagy a `codex.config` / `extraArgs` mezőkkel.

## `anthropic`

A Claude Messages API-t hívja a hivatalos C# SDK-val. Alapértelmezett modell: `claude-opus-5-5`, adaptive thinkinggel.

- Az `effort` a válasz alaposságát állítja (`output_config.effort`).
- A system prompt és a beszélgetés prompt-cache-elve megy, így egy hosszú beszélgetés folytatása olcsóbb.
- A thinking blokkok változatlanul mennek vissza a következő körben.
- Biztonsági visszautasítás esetén a szerveroldali fallback másik modellel válaszol (`serverSideFallbacks`).
- A beszélgetés kulcsonként a memóriában él; hibánál vagy megszakításnál tisztán újrakezdődik.

Hitelesítés: az `apiKeyEnv` változó (alapból `ANTHROPIC_API_KEY`). Ha ez üres, az SDK saját feloldása
(`ant auth login` profil) dönt. A `baseUrl` proxyhoz vagy gatewayhez használható.

## `openai`

Bármely OpenAI Chat Completions-kompatibilis szervert hív:

| Szerver | `baseUrl` | Megjegyzés |
|---|---|---|
| OpenAI | (üres) | `OPENAI_API_KEY` kell |
| Azure OpenAI | az Azure végpont `/openai/v1` útvonala | kulcs az `apiKeyEnv` változóban |
| Ollama | `http://localhost:11434/v1` | `ollama pull <modell>`; kulcs nem kell |
| LM Studio | `http://localhost:1234/v1` | a betöltött modell neve a `model` |
| vLLM | `http://localhost:8000/v1` | a toolokhoz `--enable-auto-tool-choice --tool-call-parser <parser>` kell |

A helyi modellnek **támogatnia kell a tool callingot**, különben nem tud üzenni a többieknek, és memóriát sem
tud menteni. Jól működő választás például a `qwen3`, a `llama3.1` vagy a `mistral-nemo`. Ha a modell érvénytelen
JSON argumentumot küld, a Racka üres objektummal hívja a toolt, és a hibát visszaadja a modellnek.

## Melyiket válaszd?

- **Koordinátor, tervező, ügyfélszolgálat jellegű agent:** `anthropic`. Pontos tool-használat, nincs saját fájlhozzáférése, MCP-n át bővíthető.
- **Kódot író agent:** `codex` vagy `claude-code`. Mindkettő saját fájl- és shell-tooljaival dolgozik; a Codexnél sandbox, a Claude Code-nál tool-allowlist a határ.
- **Reviewer:** egy *másik* modellcsalád, mint a fejlesztőé. Ha Merino Codex, legyen Dolly Claude Code, így a két szem tényleg két nézőpont.
- **Szövegmunka, összefoglalás, érzékeny adat:** `openai` helyi modellel. Ingyenes, és az adat a gépen marad.
