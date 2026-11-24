# IRC

Bármelyik agent csatlakozhat egy IRC szerverhez. Ott feladatot fogad, státuszt mutat, és egy riport-csatornára
írja az eseményeit. Ez a motortól független: egy Codex-es agent ugyanúgy tud IRC-zni, mint egy helyi modell.

## Konfiguráció

```yaml
irc:
  server: irc.libera.chat
  port: 6697
  tls: true
  nick: dolly-racka                     # alapból az agent neve
  user: dolly                           # elhagyható
  realName: "Dolly, the reviewer"       # elhagyható
  channels: ["#racka-farm"]
  reportChannel: "#racka-farm-status"   # ide mennek az események
  operators: ["yournick!*@your.host"]   # csak ők adhatnak feladatot
  saslPasswordEnv: DOLLY_IRC_PASSWORD   # SASL PLAIN; a felhasználónév a nick
  commandPrefix: "!"
  maxReplyLines: 8
```

## Parancsok

| Csatornában | Privát üzenetben | Ki adhatja | Hatás |
|---|---|---|---|
| `dolly: <feladat>`, `dolly, <feladat>`, `!dolly <feladat>` | `<feladat>` | operátor | feladat; a válasz oda megy, ahonnan jött |
| `!status [nick]` | `!status` | bárki | állapot: idle vagy busy, sorhossz |
| `!stop [nick]` | `!stop` | operátor | a futó feladat megszakítása |
| `!reset [nick]` | `!reset` | operátor | a beszélgetés újrakezdése |
| `!help` | `!help` | bárki | súgó |

A `[nick]` nélküli `!status`, `!stop` és `!reset` a csatornán lévő összes Racka agentnek szól, nickkel csak
annak az egynek. A beszélgetés-kulcs csatornában `irc:<csatorna>`, privátban `irc:<nick>`, így egy csatorna
közös előzményt lát.

A válasz legfeljebb `maxReplyLines` sor. Ennél hosszabb kimenetnél a vége helyett egy „… N more lines” sor jön
a feladat azonosítójával; a teljes kimenet az agent naplójában van. Az üzenetek UTF-8-helyesen, 400 bájtos sorokra tördelve, flood-védelemmel (token bucket) mennek ki.
Ha a kapcsolat megszakad, az agent exponenciális backoffal (2 mp-től 5 percig) csatlakozik újra.

## Riport-csatorna

A `reportChannel` csatornára ezek kerülnek:

- `online` az induláskor,
- `▶` egy feladat indulásakor (feladó és a feladat eleje),
- `✔` sikeres befejezéskor,
- `✖` hibakor,
- az `irc_report` tool üzenetei: az agent maga dönti el, mit érdemes embernek látnia.

## Jogosultság

**Ez biztonsági határ.** Egy `claude-code` vagy `codex` agent parancsot futtathat a gépeden, és bármelyik agent
memóriát írhat. Ezért:

- az `operators` maszk legyen szűk, a host részével együtt (`nick!*@your.host`, ne `nick!*@*`);
- ahol lehet, használj szerveroldali cloaket vagy SASL-lel azonosított fiókot;
- egy IRC-ről vezérelt CLI agent sandboxa (`codex.sandbox`) vagy tool-listája (`claudeCode.allowedTools`) legyen szűk.

Nem operátor feladatára vagy parancsára az agent NOTICE-ban válaszol („only operators can give me tasks”), és a feladat nem fut le.
