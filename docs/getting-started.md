# Első lépések

## 1. Telepítés

Kell hozzá a .NET 10 SDK. A motorokhoz csak az kell, amit használsz:

| Engine | Előfeltétel | Ellenőrzés |
|---|---|---|
| `claude-code` | [Claude Code](https://claude.com/claude-code) telepítve és bejelentkezve | `claude -p "hi"` |
| `codex` | [Codex CLI](https://github.com/openai/codex) telepítve és bejelentkezve (ChatGPT-fiók vagy API kulcs) | `codex login status` |
| `anthropic` | `ANTHROPIC_API_KEY`, vagy `ant auth login` profil | – |
| `openai` | OpenAI kulcs (`OPENAI_API_KEY`) vagy helyi szerver: [Ollama](https://ollama.com), LM Studio, vLLM | `curl localhost:11434/v1/models` |

A legegyszerűbb a dotnet tool. A `racka` parancs a `~/.dotnet/tools` alá kerül, ezt tedd a PATH-ra, ha még
nincs rajta:

```bash
dotnet tool install -g Racka          # frissítés: dotnet tool update -g Racka
racka --help
```

Forrásból is telepítheted, dotnet toolként vagy sima publish-sal:

```bash
git clone … Racka && cd Racka
dotnet pack src/Racka.Host -c Release -o ./nupkg
dotnet tool install -g Racka --add-source ./nupkg
# vagy:
dotnet publish src/Racka.Host -c Release -o ~/.local/share/racka
ln -sf ~/.local/share/racka/racka ~/.local/bin/racka
```

A csomag a program mellé nem hozza a példa-agenteket, a közös `skills/` mappát és a `samples/` szkriptjeit. A saját
projektedben a `racka new` létrehozza az `agents/<név>/` mappát; a közös skilleket az `agents/` melletti `skills/`
mappába tedd (`sharedSkillsDir`, alapból `../../skills`).

Fejlesztés közben a Debug build is megfelel: `dotnet build src/Racka.slnx`, a futtatható fájl pedig
`src/Racka.Host/bin/Debug/net10.0/racka`. A `samples/scripts/herd.sh` ezt is megtalálja.

## 2. Az első agent

```bash
racka new bob --engine anthropic     # vagy claude-code, codex, openai
```

Ez létrehozza az `agents/bob/` mappát:

```
agents/bob/
  agent.yaml     konfiguráció (engine, modell, toolok, MCP, IRC, memória)
  soul.md        a személyiség, a system prompt eleje
  skills/        saját skillek
  .gitignore     state/, workspace/, memory/
```

A `soul.md` a legfontosabb fájl. Írd le benne, ki az agent, miben jó, hogyan beszél, és kivel dolgozik együtt.
Az agent ebből tudja, ki ő:

```markdown
# Bob

You are Bob, the flock's data analyst. You answer questions about CSV and JSON files
with numbers you have actually computed.

## Style
- Result first, then how you got it.
- Say "I don't know" instead of guessing.
```

Indítsd el előtérben:

```bash
racka run agents/bob
```

```
14:02:11 info: racka.bob[0] Agent bob (anthropic) listening on /run/user/1000/racka/bob.sock
```

## 3. Feladat küldése

Egy másik terminálból:

```bash
racka list                                   # ki fut?
racka send bob "Mutatkozz be!" --wait        # vár a válaszra
racka send bob "Nézd át a riportot" -c riport --wait   # -c: saját beszélgetés-kulcs
racka send bob "Ezt csak sorba állítom"      # --wait nélkül: azonnal visszatér
echo "hosszú szöveg…" | racka send bob - --wait        # stdinről
racka status bob                             # állapot JSON-ban
racka cancel bob                             # a futó feladat megszakítása
```

Az azonos beszélgetés-kulcsú (`-c`) feladatok folytatják egymást. Kulcs nélkül a kulcs `cli:<feladó>`,
vagyis egy felhasználó parancssori feladatai egy beszélgetésben futnak.

Szkriptből hívva a `--json` kapcsolóval géppel olvasható választ kapsz:

```bash
$ racka send bob "2+2?" --wait --json
{"id":"5f0c…","success":true,"output":"4","error":null,"queued":false}
```

## 4. A példa-csorda

Az `agents/` mappában négy agent van, mindegyik más motoron fut. Együtt egy kis fejlesztőcsapatot alkotnak:

| Agent | Engine | Szerep | Workspace |
|---|---|---|---|
| `shaun` | `anthropic` | koordinátor: lebontja a kérést, delegál, összefoglal | – |
| `merino` | `codex` | fejlesztő: kódot és tesztet ír, futtatja a teszteket | `workspace/` (közös) |
| `dolly` | `claude-code` | reviewer: olvas, tesztet futtat, véleményez | `workspace/` (közös) |
| `lamb` | `openai` → Ollama | író: changelog, összefoglaló, fordítás | – |

Merino és Dolly ugyanabban a `workspace/` mappában dolgozik, így Dolly azt nézi át, amit Merino írt.

```bash
ollama pull qwen3:8b            # Lamb modellje (tool calling kell); más: LAMB_MODEL=…
samples/scripts/herd.sh start   # mind a négy háttérben, naplók: .herd/<név>.log
racka list
```

```
NAME             ENGINE       MODEL                STATE  QUEUE  DONE  FAIL IRC  PID
dolly            claude-code  claude-opus-5-5      idle       0     0     0 -    81721
lamb             openai       qwen3:8b             idle       0     0     0 -    81722
merino           codex        -                    idle       0     0     0 -    81723
shaun            anthropic    claude-opus-5-5      idle       0     0     0 -    81724
```

Adj munkát Shaunnak, és figyeld a naplókat (`samples/scripts/herd.sh logs merino`):

```bash
racka send shaun "Készíts a workspace-ben egy wordcount.py-t, ami soronként megszámolja a szavakat \
a stdin-en, pytest tesztekkel. Merino írja meg, Dolly nézze át, Lamb írjon hozzá changelogot." --wait
```

Ez a folyamat zajlik le:

1. **Shaun** (Claude API) a `list_agents` toollal megnézi, ki fut, majd `send_message`-dzsel kiadja a feladatot Merinónak, és vár a válaszra.
2. **Merino** (Codex) megírja a kódot és a teszteket, majd lefuttatja őket. A soul-ja alapján `send_message`-dzsel review-t kér Dollytól.
3. **Dolly** (Claude Code) a `code-review` skill szerint átnézi a változást, és `VERDICT: APPROVED` vagy `VERDICT: CHANGES` sorral zár.
4. Merino kijavítja, amit Dolly talált, és jelent Shaunnak.
5. **Shaun** a jegyzetekkel együtt elküldi a feladatot **Lambnak** (helyi modell), aki megírja a changelogot.
6. Shaun összefoglalja az eredményt, és ez lesz a `racka send … --wait` kimenete.

Ha Dolly véletlenül Merinótól kérdezne vissza, miközben Merino rá vár, azonnal hibaüzenetet kap. A csorda nem
akad el holtpontban (lásd [Együttműködés](collaboration.md#holtpont-védelem)).

Ha ugyanezt a munkamenetet minden alkalommal azonos lépésekben akarod futtatni, használd a
[ReviewPipeline](../samples/dotnet/ReviewPipeline/Program.cs) mintát: ott a sorrendet kód rögzíti, nem Shaun dönti el.

```bash
samples/scripts/herd.sh stop
```

## Következő lépések

- [Konfiguráció](configuration.md): az `agent.yaml` minden mezője.
- [Engine-ek](engines.md): mit enged meg és mennyibe kerül egy-egy motor.
- [Toolok, skillek, MCP](tools-skills-mcp.md): hogyan kap az agent új képességet.
- [Üzemeltetés](operations.md): futtatás systemd-vel, biztonság.
