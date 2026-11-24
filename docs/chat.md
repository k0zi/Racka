# Chat: IRC, Telegram, Discord, Slack, Teams

Bármelyik agent csatlakozhat egy vagy több chat-platformhoz. Ott feladatot fogad, státuszt mutat, és egy
riport-csatornára írja az eseményeit. Ez a motortól független: egy Codex-es agent ugyanúgy chatel, mint egy helyi
modell, és a viselkedés minden platformon ugyanaz.

| Platform | Kapcsolat | Kell nyilvános URL? | Operátor-azonosító | Egy bot több agentnek? |
|---|---|---|---|---|
| IRC | TCP/TLS, saját kliens | nem | `nick!user@host` maszk | – (agentenként saját nick) |
| Telegram | long polling | nem | Telegram user id | **nem**: agentenként saját bot |
| Discord | gateway websocket | nem | Discord user id | nem ajánlott: mindegyik megkapná az üzeneteket |
| Slack | Socket Mode websocket | nem | Slack member id (`U…`) | **nem**: a Slack szétosztja az eseményeket |
| Teams | Azure Bot, `POST /api/messages` | **igen**, HTTPS (tunnel vagy reverse proxy) | Entra object id | nem: agentenként saját bot és port |

## Hol állítható be

A chat-szekciók (`irc`, `telegram`, `discord`, `slack`, `teams`) három helyen adhatók meg; az erősebb mezőnként
felülírja a gyengébbet:

1. **Felhasználói beállítások**: `~/.config/racka/config.yaml` (Windowson `%APPDATA%\racka\config.yaml`,
   `$RACKA_CONFIG_DIR` felülírja). Az első `racka` futás létrehozza egy kommentezett sablonnal. Ami itt van,
   az **minden agentre** érvényes.
2. **`agent.yaml`**: az adott agent saját beállításai. `enabled: false`-szal egy agent kimarad a közös kapcsolatból.
3. **`racka run` kapcsolók**: egyszeri felülírás, csak IRC-re: `--irc-server`, `--irc-port`, `--irc-tls`,
   `--irc-channel` (ismételhető), `--irc-nick`; `--no-chat` minden platformot kikapcsol.

A listák (pl. `channels`) nem összeadódnak, hanem lecserélődnek. A `{agent}` helyére az agent neve, a `{AGENT}`
helyére a nagybetűs neve kerül (`-` helyett `_`). Ezt a nickben és a `*Env` mezőkben használhatod, így egy közös
beállítás agentenként mást jelent.

```bash
racka config set irc.server irc.libera.chat
racka config set irc.channels "#racka,#racka-status"     # lista: vesszővel
racka config set irc.nick "{agent}-racka"
racka config set irc.operators 'k0zi!*@my.host'
racka config get irc.channels
racka config show                                        # az egész fájl
racka config set --agent agents/lamb irc.enabled false   # egy agent kimarad
racka run agents/shaun --irc-server irc.example.org --irc-channel "#teszt"
```

A `racka config set` ellenőrzi a kulcsot és az érték típusát, de újraírja a fájlt, így a kommentek elvesznek. Titok
(token, jelszó) soha nem kerül a configba: a `*Env` mezők csak a környezeti változó **nevét** tartalmazzák. A
systemd unit ezeket a `~/.config/racka/<agent>.env` fájlból tölti be ([Üzemeltetés](operations.md)).

## Közös mezők

| Mező | Alapérték | Leírás |
|---|---|---|
| `enabled` | `true` | `false`: a kapcsolat nem indul el (pl. egy közös beállítás egy agentnél) |
| `channels` | `[]` | IRC: ezekre a csatornákra lép be. Máshol: csak ezekben a csatornákban (id) figyel; üresen mindenhol |
| `reportChannel` | – | ide mennek az események; ha nincs megadva, a `chat_report` az első csatornába ír |
| `operators` | `[]` | csak ők adhatnak feladatot és `stop`/`reset` parancsot |
| `commandPrefix` | `!` | a parancsok előtagja |
| `maxReplyLines` | IRC: `8`, máshol: `40` | ennyi sor után a válasz egy „… N more lines” sorral zárul |

## Parancsok

| Csatornában | Privát üzenetben | Ki adhatja | Hatás |
|---|---|---|---|
| `dolly: <feladat>`, `dolly, <feladat>`, `!dolly <feladat>`, a bot megemlítése (`@dolly …`) vagy válasz az üzenetére | `<feladat>` | operátor | feladat; a válasz oda megy, ahonnan jött |
| `!status [név]` | `!status` | bárki | állapot: idle vagy busy, sorhossz |
| `!stop [név]` | `!stop` | operátor | a futó feladat megszakítása |
| `!reset [név]` | `!reset` | operátor | a beszélgetés újrakezdése |
| `!help` | `!help` | bárki | súgó |

A `név` IRC-n a nick, máshol az agent neve. Telegramon a `/status`, `/stop`, `/reset`, `/help` és `/start` is
működik (`/status@botneve` alakban is). A név nélküli `!status`, `!stop` és `!reset` a csatornán lévő összes Racka
agentnek szól.

A beszélgetés-kulcs `<platform>:<csatorna>`, privátban `<platform>:<felhasználó>`, így egy csatorna közös
előzményt lát. Ha az agent épp dolgozik, a feladat sorba kerül, és erről válasz is jön („queued (N ahead of you)”).

## Riport-csatorna

A `reportChannel` csatornára ezek kerülnek:

- `online` az induláskor,
- `▶` egy feladat indulásakor (a feladat eleje),
- `✔` sikeres befejezéskor,
- `✖` hibakor,
- a `chat_report` tool üzenetei: az agent maga dönti el, mit érdemes embernek látnia.

## IRC

```yaml
irc:
  server: irc.libera.chat
  port: 6697
  tls: true
  nick: dolly-racka                     # alapból az agent neve
  user: dolly                           # elhagyható
  realName: "Dolly, the reviewer"       # elhagyható
  channels: ["#racka-farm"]
  reportChannel: "#racka-farm-status"
  operators: ["yournick!*@your.host"]
  saslPasswordEnv: DOLLY_IRC_PASSWORD   # SASL PLAIN; a felhasználónév a nick
```

Az üzenetek UTF-8-helyesen, 400 bájtos sorokra tördelve, flood-védelemmel (token bucket) mennek ki. Ha a kapcsolat
megszakad, az agent exponenciális backoffal (2 mp-től 5 percig) csatlakozik újra. Ha a nick foglalt, `_` kerül a
végére. Nem operátor feladatára az agent NOTICE-ban válaszol.

## Telegram

1. A [@BotFather](https://t.me/BotFather)-nél `/newbot`, a kapott token egy környezeti változóba kerül
   (alapból `<AGENT>_TELEGRAM_TOKEN`, pl. `SHAUN_TELEGRAM_TOKEN`).
2. A saját user id-det pl. a [@userinfobot](https://t.me/userinfobot) mondja meg; ez kerül az `operators`-ba.
3. Csoportban a bot alapból (privacy mode) csak a parancsokat, a megemlítéseket és a neki szóló válaszokat látja,
   és ez elég is.

```yaml
telegram:
  tokenEnv: SHAUN_TELEGRAM_TOKEN
  operators: ["123456789"]
  channels: []                  # chat id-k (csoportnál negatív szám); üresen minden chat
  reportChannel: "-1001234567890"
```

Egy bot tokenjével csak egy processz kérdezhet le üzeneteket. Ha két agent ugyanazt a tokent használja, vagy a botnak
webhookja van, a napló ezt jelzi („another process is polling this bot”).

## Discord

1. [Developer Portal](https://discord.com/developers/applications) → *New Application* → *Bot*: a token egy
   környezeti változóba kerül (alapból `<AGENT>_DISCORD_TOKEN`), és kapcsold be a **Message Content Intent**-et.
2. *OAuth2 → URL Generator*: `bot` scope, *Send Messages* és *Read Message History* jog; a linkkel hívd meg a botot
   a szerverre.
3. A Discordban *Developer Mode*-ot bekapcsolva jobb klikkel másolható a user és a csatorna id-je.

```yaml
discord:
  tokenEnv: DOLLY_DISCORD_TOKEN
  operators: ["123456789012345678"]
  channels: ["987654321098765432"]       # csatorna id vagy név; a szálak a szülőcsatornájukat követik
  reportChannel: "987654321098765433"
```

A válasz megemlíti a feladót (`@k0zi`), és 2000 karakteres üzenetekre tördelődik.

## Slack

1. [api.slack.com/apps](https://api.slack.com/apps) → *Create New App* → *From scratch*.
2. *Socket Mode*: kapcsold be, és hozz létre egy app-level tokent `connections:write` scope-pal (`xapp-…`,
   alapból `<AGENT>_SLACK_APP_TOKEN`).
3. *OAuth & Permissions* → bot scope-ok: `app_mentions:read`, `chat:write`, `channels:history`, `groups:history`,
   `im:history`. Telepítsd a workspace-be; a bot token (`xoxb-…`) alapból `<AGENT>_SLACK_BOT_TOKEN`.
4. *Event Subscriptions* → bot események: `app_mention`, `message.channels`, `message.groups`, `message.im`.
5. *App Home* → *Messages Tab*: engedélyezd, hogy privátban is lehessen írni neki. Hívd meg a csatornába (`/invite`).

```yaml
slack:
  botTokenEnv: MERINO_SLACK_BOT_TOKEN
  appTokenEnv: MERINO_SLACK_APP_TOKEN
  operators: ["U0123456789"]            # profil → … → Copy member ID
  channels: ["C0123456789"]
  reportChannel: "C0123456790"
```

Szálban kapott feladatra a válasz ugyanabba a szálba megy.

## Teams

A Teams az egyetlen platform, amely nem enged kifelé kapcsolódó botot: a Microsoft szolgáltatása hívja az agentet,
ezért annak HTTPS-en elérhetőnek kell lennie.

1. Azure Portal → *Azure Bot* erőforrás, *Single Tenant* típussal. Az *App ID* és a *Tenant ID* a configba kerül,
   egy új *client secret* pedig környezeti változóba (alapból `<AGENT>_TEAMS_CLIENT_SECRET`).
2. Az agent a `bindAddress:port` címen (alapból `127.0.0.1:3978`) hallgatja a `/api/messages` végpontot. Ezt tedd
   nyilvánossá egy tunnellel (pl. `devtunnel host -p 3978 --allow-anonymous`) vagy HTTPS reverse proxyval, és a bot
   *Messaging endpoint*-ja legyen `https://<nyilvános cím>/api/messages`.
3. *Channels* → *Microsoft Teams* bekapcsolása, majd a bot hozzáadása a Teamshez (app manifesttel vagy a *Open in
   Teams* linkkel).

```yaml
teams:
  appId: 00000000-0000-0000-0000-000000000000
  tenantId: 00000000-0000-0000-0000-000000000000
  clientSecretEnv: SHAUN_TEAMS_CLIENT_SECRET
  port: 3978                     # agentenként más port
  operators: ["00000000-0000-0000-0000-000000000000"]    # Entra object id
  reportChannel: "19:…@thread.tacv2"                      # beszélgetés-id
```

A végpont csak a Bot Framework által az app id-re kiállított, aláírt tokent fogadja el; minden más kérésre 401 a
válasz. A válaszok proaktív üzenetként mennek, így a feladat tovább futhat, mint a bejövő kérés. A bot csak olyan
beszélgetésbe tud írni, ahonnan már kapott üzenetet, vagy ahová hozzáadták; ezek a `state/teams-conversations.json`
fájlba kerülnek, és onnan olvasható ki a `reportChannel` beszélgetés-id-je is. Csatornában és csoportos chatben a
Teams csak a botot megemlítő üzeneteket kézbesíti.

## Jogosultság

**Ez biztonsági határ.** Egy `claude-code` vagy `codex` agent parancsot futtathat a gépeden, és bármelyik agent
memóriát írhat. Ezért:

- az `operators` lista legyen szűk; IRC-n a host résszel együtt (`nick!*@your.host`, ne `nick!*@*`), és ahol lehet,
  szerveroldali cloakkal vagy SASL-lel azonosított fiókkal;
- a többi platformon felhasználói id kell, nem név: a megjelenített név szabadon változtatható;
- egy chatről vezérelt CLI agent sandboxa (`codex.sandbox`) vagy tool-listája (`claudeCode.allowedTools`) legyen szűk;
- a tokenek és secretek csak környezeti változóban legyenek, a `.env` fájl `chmod 600`.

Nem operátor feladatára vagy parancsára az agent azt válaszolja, hogy „only operators can give me tasks”, és a
feladat nem fut le. A `status` és a `help` mindenkinek válaszol.
