# Minták

| Minta | Mit mutat | Futtatás |
|---|---|---|
| [`../agents`](../agents) | vegyes motorú csorda: Shaun (Claude API), Merino (Codex), Dolly (Claude Code), Lamb (Ollama) | `scripts/herd.sh start` |
| [`dotnet/ExternalClient`](dotnet/ExternalClient/Program.cs) | agentek listázása, ask, tell és státusz .NET-ből a `BusClient` osztállyal | `dotnet run --project samples/dotnet/ExternalClient -- shaun "Szia!"` |
| [`dotnet/ReviewPipeline`](dotnet/ReviewPipeline/Program.cs) | HerdFlow flow három motoron: Codex implementál → Claude Code review-hurok → helyi modell changelogot ír | `dotnet run --project samples/dotnet/ReviewPipeline -- "<feladat>"` |
| [`dotnet/CustomTool`](dotnet/CustomTool/TicketTools.cs) | saját C# toolok (ticket-tábla) egy saját hostban, amely bármelyik engine-nel működik | `cd samples/dotnet/CustomTool && dotnet run` |
| [`scripts/herd.sh`](scripts/herd.sh) | csorda indítása, leállítása, naplói | `samples/scripts/herd.sh start\|status\|logs <név>\|stop` |
| [`scripts/ask.sh`](scripts/ask.sh) | agent hívása shell-szkriptből (jq-val, kilépési kóddal) | `git diff \| samples/scripts/ask.sh dolly -` |
| [`scripts/ask.py`](scripts/ask.py) | HTTP a Unix socketen, csak a Python standard könyvtárával | `python3 samples/scripts/ask.py shaun "Mi a státusz?"` |
| [`systemd/racka@.service`](systemd/racka@.service) | agentenként egy systemd user unit | lásd a fájl fejlécét |

A .NET minták a `src/Racka.slnx` solution `samples` mappájában vannak, így a `dotnet build src/Racka.slnx` velük együtt fordít.

## Egy teljes kör a csordával

```bash
ollama pull qwen3:8b
samples/scripts/herd.sh start

# 1. Shaun koordinál: ő dönti el, kit kér meg és mire
racka send shaun "Készíts a workspace-ben egy wordcount.py-t pytest tesztekkel. \
Merino írja meg, Dolly nézze át, Lamb írjon hozzá changelogot." --wait

# 2. Ugyanez rögzített lépésekben, HerdFlow-val (Shaun nélkül)
dotnet run --project samples/dotnet/ReviewPipeline -- \
  "Add a --top N option to wordcount.py that prints the N most frequent words."

# 3. Közvetlen kérdések szkriptből
samples/scripts/ask.sh dolly "Hány teszt van a workspace-ben, és mind zöld?"
python3 samples/scripts/ask.py lamb "Fordítsd angolra: a csorda elkészült a feladattal."

# 4. A ticket-tábla agent: bárki nyithat ticketet, akár egy másik agent is
(cd samples/dotnet/CustomTool && dotnet run) &
racka send shepherd "Nyiss egy ticketet Merinónak: a wordcount.py kezelje az üres bemenetet." --wait
racka send shaun "Nézd meg Shepherdnél, milyen nyitott ticketjei vannak Merinónak, és adj ki neki egyet." --wait

samples/scripts/herd.sh stop
```

A közös workspace a repó gyökerében lévő `workspace/` mappa, Merino és Dolly ugyanazt látja. Lamb csak szöveggel
dolgozik; fájl-hozzáférést a filesystem MCP szerverrel kaphat ([Toolok, skillek, MCP](../docs/tools-skills-mcp.md#mcp-szerverek)).
