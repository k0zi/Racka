# Üzemeltetés

## Futtatás háttérben

**Fejlesztéshez.** A [`samples/scripts/herd.sh`](../samples/scripts/herd.sh) `nohup`-pal indítja az agenteket. A
pid- és naplófájlok a `.herd/` mappába kerülnek:

```bash
samples/scripts/herd.sh start              # az agents/ alatti összes agent
samples/scripts/herd.sh start shaun lamb   # csak ezek
samples/scripts/herd.sh logs merino        # napló követése
samples/scripts/herd.sh stop
```

**Tartós futtatáshoz.** A [`samples/systemd/racka@.service`](../samples/systemd/racka@.service) user unitból agentenként
egy példány fut, hiba esetén újraindul. A titkok a `~/.config/racka/<agent>.env` fájlba kerülnek:

```bash
cp samples/systemd/racka@.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now racka@shaun racka@merino racka@dolly racka@lamb
journalctl --user -u racka@merino -f
loginctl enable-linger $USER     # hogy kijelentkezés után is fussanak
```

Egy leállított agent (`SIGINT`/`SIGTERM`) befejezi a HTTP-kéréseit, és törli a socketjét. Ha egy processz
összeomlik, és ott marad a socket, a következő indítás észreveszi, hogy senki nem válaszol rajta, és felülírja.
Ha a régi példány még él, az új nem indul el (`already running`).

## Naplók

Minden agent a stdout-ra naplóz, soronként:

```
14:03:10 info: racka.merino[0] [TaskStarted] 5f0c1a2b3c4d from bus:shaun
14:03:12 info: racka.merino[0] [ToolCalled] 5f0c1a2b3c4d shell: bash -lc 'ls'
14:03:40 info: racka.merino[0] [ToolCalled] 5f0c1a2b3c4d racka.send_message
14:04:02 info: racka.merino[0] [TaskCompleted] 5f0c1a2b3c4d …
```

Az események: `AgentStarted`, `TaskQueued`, `TaskStarted`, `ToolCalled`, `Progress`, `TaskCompleted`, `TaskFailed`,
`Report`, `AgentStopping`. Az indulás, a kész, a hiba és a riport a chat-platformok riport-csatornájára is kimegy, ha be van állítva.

## Biztonság

| Határ | Mit véd | Mire figyelj |
|---|---|---|
| Unix socketek (`0700` könyvtár, `0600` socket) | a busz csak a saját felhasználódé | ne állíts közös `RACKA_RUNTIME_DIR`-t más felhasználókkal |
| Titkok | csak környezeti változóban vannak | `${VAR}` a configban; az `.env` fájl `chmod 600` |
| `claudeCode.allowedTools`, `permissionMode` | mit futtathat a Claude Code agent | reviewernek `dontAsk` mód és szűk lista; `bypassPermissions` csak izolált gépen |
| `codex.sandbox` | hová írhat a Codex agent | `workspace-write`; `danger-full-access` csak izolált gépen |
| `maxBudgetUsd`, `maxIterations` | a költség és a hurkok | minden fizetős agentre érdemes korlátot tenni |
| chat `operators` | ki adhat feladatot IRC-n, Telegramon, Discordon, Slacken, Teamsen | IRC-n szűk hostmaszk és SASL, máshol felhasználói id; részletek: [Chat](chat.md#jogosultság) |
| Teams végpont | a nyilvános `/api/messages` | csak a Bot Framework aláírt, az app id-re szóló tokenje megy át; a listener alapból `127.0.0.1`-en van |

Ne feledd, hogy a delegálás a bizalmat is továbbadja: ha Shaun bárkitől fogad feladatot, akkor rajta keresztül
bárki dolgoztathatja Merinót is. A külső csatornák (chat-platformok, saját toolok, MCP szerverek) bemenete egy agenten
át eljuthat a többihez, sőt a memóriába is bekerülhet.

## Hibakeresés

| Tünet | Ok és megoldás |
|---|---|
| `Agent 'x' is not running.` | nincs socket; fut az agent? Ugyanaz a `RACKA_RUNTIME_DIR` a küldőnél és az agentnél? |
| `socket path … is too long` | a Unix socket útvonala legfeljebb 107 bájt; állíts rövidebb `RACKA_RUNTIME_DIR`-t |
| `already running (pid …)` | ugyanaz az agent már fut; `racka list` |
| Claude Code agent nem tud üzenni | a `state/mcp.json`-ban van `racka` szerver? A `claude --debug` kimenete megmutatja az MCP-hibákat |
| Codex agent nem tud üzenni | `codex mcp list` mutatja a globális szervereket; a Racka bridge csak futás közben, `-c`-vel jön létre |
| Codex: `exited with code 1 without finishing the turn` | a napló stderr része mutatja az okot; gyakran bejelentkezés (`codex login status`) vagy használati korlát |
| Helyi modell nem hív toolt | a modell nem támogatja a tool callingot; válassz másikat (`qwen3`, `llama3.1`) |
| `ask cycle: …` | két agent egymásra várna; ez szándékos védelem, a modell a kérdés nélkül folytatja |
| `timed out after 600s; the task keeps running` | hosszú feladat; nagyobb `--timeout` / `timeout_s`, vagy tell és visszajelzés |
| Egy beszélgetés „megbolondult” | `racka send … -c új-kulcs`, chatben `!reset`; a CLI engine-eknél a `state/*sessions.json` törlése |

A bridge kézzel is kipróbálható: `racka bridge --agent merino` egy stdio MCP szerver, amely az agent socketjére továbbít.
