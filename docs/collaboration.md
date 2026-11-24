# Együttműködés

## A busz

Minden agent egy saját Unix socketen hallgat: `$RACKA_RUNTIME_DIR/<név>.sock`. Ha a változó nincs beállítva,
a hely `$XDG_RUNTIME_DIR/racka`. Nincs központi broker. Az agentek a socket-könyvtár listázásával találják meg
egymást, és HTTP/JSON-ban beszélnek. A könyvtár `0700`, a socketek `0600` jogúak, így csak a saját
felhasználód érheti el őket.

Minden agentnek egy inboxa van, és egyszerre egy feladatot futtat; a többi sorba áll. A feladat forrása a
CLI (`racka send`), egy másik agent (`send_message`) vagy IRC lehet. Mindhárom ugyanabba a sorba kerül.

## `send_message`

Ezt a toolt minden engine-nél minden agent megkapja:

| Argumentum | Alapérték | Leírás |
|---|---|---|
| `to` | – | a címzett agent neve |
| `text` | – | a feladat vagy kérdés, önmagában érthetően |
| `wait_for_reply` | `true` | `true` = **ask**: megvárja a választ, és az lesz a tool eredménye; `false` = **tell**: csak sorba állítja |
| `timeout_s` | `600` | ask esetén a várakozás felső korlátja (5–3600 másodperc) |

Mellette a `list_agents` tool kiírja, ki fut, milyen motoron és milyen állapotban.

Egy agent minden üzenete a címzettnél egy `bus:<feladó>` kulcsú beszélgetésben fut. Ha tehát Shaun ötször
kérdezi Merinót, Merino emlékszik az előző négy kérdésre. Ehhez a Claude Code és a Codex session-t folytat,
az API engine-ek pedig a beszélgetést tartják meg.

### Ask vagy tell?

- **Ask:** akkor kell, ha a válasz nélkül nem tudsz továbblépni („írd meg, és mondd meg, melyik paranccsal futnak a tesztek”).
  A kérdező agent addig vár, a saját inboxa közben sorban áll.
- **Tell:** akkor kell, ha a munka eredménye nem a feladóhoz jut vissza, hanem máshová („írd meg a changelogot, és küldd el Dollynak”,
  „riportolj az IRC-re, ha kész”). A címzett a végén maga küld üzenetet a megfelelő agentnek.

### Holtpont-védelem

Minden ask magával viszi a várakozási láncot (`chain`): kik várnak éppen a válaszára. Ha egy agent olyan
agentet kérdezne, amely már vár rá, például

```
shaun → merino (ask) → dolly (ask) → merino (ask)   ✖
```

akkor a kérdés nem kerül sorba, hanem azonnal visszajön egy hibával: `merino is busy waiting for your own answer
(ask cycle: shaun → merino → dolly → merino). Answer without asking merino.` A modell ezt látja, és a kérdés
nélkül folytatja. A tell üzenetekre ez nem vonatkozik, mert azok nem várnak semmire.

## Delegálási minták

**Koordinátor és specialisták.** Ezt mutatja a példa-csorda. Egy agent (Shaun) érti a kérést, és a soul-jában
le van írva, ki miben jó. A specialisták soul-ja megmondja, kitől kapnak munkát, és kinek adják tovább.
Ez rugalmas: a koordinátor menet közben dönti el a lépéseket.

**Peer review.** A fejlesztő maga kér review-t (Merino → Dolly). Legyen a fejlesztő és a reviewer különböző
motoron, így két különböző modell nézi meg ugyanazt a kódot.

**Futószalag (tell-lánc).** Az A agent tell-lel továbbadja a munkát B-nek, aki C-nek, és az utolsó jelent IRC-re vagy
egy riport-agentnek. Senki nem vár, és minden lépés a saját sorában fut.

**Rögzített pipeline kódból.** Ha minden alkalommal ugyanazok a lépések kellenek, a sorrendet ne a modell
döntse el, hanem HerdFlow-flow. Lásd a [ReviewPipeline](../samples/dotnet/ReviewPipeline/Program.cs) mintát:
`Implement → Review ─changes→ Fix → Review …, ─approved→ Changelog`.

**Közös munkaterület.** Ha két agent ugyanazokon a fájlokon dolgozik, a `workdir` mutasson ugyanoda
(`agents/merino` és `agents/dolly`: `workdir: ../../workspace`). A csak szöveggel dolgozó agentnek (Lamb)
a kérésbe kell beletenni, amit látnia kell.

## Kívülről

| Honnan | Hogyan |
|---|---|
| shell | `racka send <agent> "…" --wait --json` vagy [`samples/scripts/ask.sh`](../samples/scripts/ask.sh) |
| Python | [`samples/scripts/ask.py`](../samples/scripts/ask.py), csak a standard könyvtárral |
| .NET | `BusClient` a `Racka.Core`-ból, lásd [ExternalClient](../samples/dotnet/ExternalClient/Program.cs) |
| bármi más | HTTP a Unix socketen: `curl --unix-socket $XDG_RUNTIME_DIR/racka/shaun.sock http://shaun/status` |

## HTTP API

Minden agent socketje ugyanezt az API-t adja. A JSON mezőnevek camelCase-ek, a host név tetszőleges
(a `BusClient` az agent nevét használja).

### `POST /messages`

```json
{
  "id": "a1b2c3d4e5f6",
  "from": "cli:k0zi",
  "to": "shaun",
  "kind": "ask",
  "text": "Mi a státusz?",
  "conversationKey": "cli:k0zi",
  "timeoutSeconds": 600,
  "chain": [],
  "source": "cli"
}
```

| Mező | Leírás |
|---|---|
| `kind` | `ask` (vár) vagy `tell` (sorba állít) |
| `conversationKey` | elhagyható; alapértéke `bus:<from>` |
| `timeoutSeconds` | ask esetén; alapértéke 600 |
| `chain` | a várakozó agentek, a holtpont-védelemhez; külső hívónál üres |
| `source` | `cli`, `bus` vagy `irc`; az agent a feladat fejlécében látja |

Válasz (`200`):

```json
{"id":"a1b2c3d4e5f6","success":true,"output":"…","error":null,"queued":false}
```

Tell esetén `queued: true` és üres `output`. Ha az ask időtúllépéssel jár le, `success: false` jön, a feladat
viszont tovább fut. Hibás kérésre (rossz címzett, üres szöveg) `400` a válasz.

### További végpontok

| Végpont | Leírás |
|---|---|
| `GET /status` | `name, engine, model, state, currentTaskId, currentTaskFrom, queueLength, startedAt, processId, ircConnected, completedTasks, failedTasks` |
| `POST /cancel` | megszakítja a futó feladatot (a CLI engine-eknél a teljes processzfát leállítja) |
| `GET /tools` | a bridge-en elérhető toolok: `[{name, description, inputSchema}]` |
| `POST /tools/invoke` | `{"name":"memory_search","arguments":{"query":"x"}}` → `{"content","isError"}` |

A `/tools/invoke` végpontot a bridge használja. A tool a futó feladat kontextusában fut, így a `send_message`
viszi a várakozási láncot.
