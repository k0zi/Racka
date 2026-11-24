# Toolok, skillek, MCP

## Beépített Racka toolok

| Tool | Mikor van | Leírás |
|---|---|---|
| `send_message` | mindig | üzenet egy másik agentnek, ask vagy tell ([Együttműködés](collaboration.md#send_message)) |
| `list_agents` | mindig | a futó agentek listája motorral és állapottal |
| `memory_save`, `memory_read`, `memory_search`, `memory_delete` | ha a memória be van kapcsolva | [Memória](memory.md) |
| `load_skill`, `read_skill_file` | ha van skill, és az engine nem `claude-code` | a skill tartalmának betöltése |
| `irc_report` | ha be van állítva `irc` | egysoros státuszüzenet a riport-csatornára |
| saját toolok | ha a host megadja őket | [Bővítés](extending.md#saját-tool) |

Az API engine-ek ezeket közvetlenül hívják. A Claude Code és a Codex a `racka bridge` MCP szerveren át éri el
őket, így a Claude Code-ban `mcp__racka__send_message` lesz a nevük.

## Skillek

A skill egy mappa, benne egy `SKILL.md` fájllal és tetszőleges segédfájlokkal. A formátum megegyezik a Claude Code-éval:

```
skills/code-review/
  SKILL.md
  checklist.md      (opcionális segédfájl)
```

```markdown
---
name: code-review
description: Review a code change for correctness, tests and readability. Use when asked to review code.
---

# Code review

1. Find the change with `git diff` …
```

- A **`description`** dönti el, mikor nyúl az agent a skillhez. Írd bele, mire jó és mikor kell használni.
- **Hol keresi:** az agent saját `skills/` mappájában lévő skillek mindig betöltődnek. A közös `skills/` mappából
  az `agent.yaml` `skills: [név, …]` listája választ. Névütközéskor a saját skill nyer.
- **Hogyan éri el:**
  - **Claude Code:** a skillek symlinkként kerülnek a `workspace/.claude/skills` alá, a Claude Code natívan használja őket.
  - **Codex, Claude API, OpenAI:** a skillek neve és leírása a system promptba kerül. A tartalmat az agent a `load_skill`
    toollal tölti be, a segédfájlokat a `read_skill_file` toollal olvassa, amely a skill mappáján kívülre nem enged.

A repóban lévő skillek: [`status-report`](../skills/status-report/SKILL.md),
[`code-review`](../skills/code-review/SKILL.md), [`changelog`](../skills/changelog/SKILL.md).

## MCP szerverek

```yaml
mcpServers:
  everything: { command: npx, args: ["-y", "@modelcontextprotocol/server-everything"] }
  fs:         { command: npx, args: ["-y", "@modelcontextprotocol/server-filesystem", "."] }
  docs:       { url: https://example.com/mcp, headers: { Authorization: "Bearer ${DOCS_TOKEN}" } }
```

Engine-enként így jutnak el a szerverek az agenthez:

- **API engine-ek:** a Racka az induláskor csatlakozik a szerverekhez: a stdio szervereket a `workdir` könyvtárban
  indítja, a HTTP szerverekhez streamable HTTP-n kapcsolódik. A toolok `<szerver>__<tool>` néven jelennek meg.
  Ha egy szerver nem indul el, az agent figyelmeztetést ír a naplóba, és nélküle fut tovább. A tool-eredményből
  a szöveg és a szöveges erőforrás kerül tovább, a képek helyén csak egy `[image …]` jelölés áll; legfeljebb 100 000 karakter.
- **Claude Code:** a szerverek a generált `state/mcp.json`-ba kerülnek (`--mcp-config`), automatikusan engedélyezve.
  `strictMcpConfig: true` mellett csak ezek töltődnek be, a felhasználó globális szerverei nem.
- **Codex:** a szerverek `-c mcp_servers.<név>={…}` felülírásként mennek át, jóváhagyás nélkül futtatható toolokkal.
  A `~/.codex/config.toml` szerverei is betöltődnek, ha nem állítod be az `ignoreUserConfig: true` kapcsolót.

Egy hasznos minta: a csak szöveggel dolgozó helyi agent (Lamb) a filesystem MCP szerverrel olvasási hozzáférést
kaphat a közös workspace-hez:

```yaml
# agents/lamb/agent.yaml
mcpServers:
  workspace: { command: npx, args: ["-y", "@modelcontextprotocol/server-filesystem", "../../workspace"] }
```
