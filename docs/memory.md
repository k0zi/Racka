# Memória

Minden agentnek saját memóriája van, amely túléli az újraindítást és a beszélgetésváltást. A helye az
`agents/<név>/memory/` mappa, minden memória egy Markdown fájl:

```markdown
---
name: owner-language
description: "A gazda magyarul kér választ, a kód angol marad"
updated: 2026-11-23T10:15:00Z
---

Magyarul válaszolj; a kódban, kommentekben és commit üzenetekben angol a nyelv.
```

## Hogyan látja az agent

- **Index a promptban.** A memóriák `név: leírás` listája a system prompt végére kerül. Az API engine-ek minden új
  beszélgetésnél, a CLI engine-ek minden futásnál újraépítik, így a közben mentett memóriák is látszanak.
  Ha az index nem fér bele a `maxIndexChars` korlátba, egy „…and N more (use memory_search)” sor zárja.
- **Toolok** (mindegyik engine-nél, a CLI engine-eknél a bridge-en át):

| Tool | Leírás |
|---|---|
| `memory_save(name, description, content)` | létrehoz, vagy azonos névvel felülír egy memóriát |
| `memory_read(name)` | egy memória teljes tartalma |
| `memory_search(query?, limit?)` | szavas keresés névben, leírásban és tartalomban; lekérdezés nélkül mindent listáz |
| `memory_delete(name)` | törlés |

## Mit ment el

A system prompt arra utasítja az agentet, hogy azt mentse el, ami egy későbbi beszélgetésben is kell:

- kivel dolgozik, és mit szeretnek,
- milyen javításokat kapott,
- milyen döntések születtek, és miért,
- hol mi található.

Ami csak az aktuális feladathoz kell, vagy könnyen újra kideríthető, az nem kerül a memóriába. Egy témához
egy memória tartozik, és a tévesnek bizonyult memóriákat az agent törli.

A soul-ban ezt tovább lehet hangolni. Merino soul-ja például azt kéri, hogy jegyezze meg a projekt
konvencióit (nyelv, tesztkeret, mappaszerkezet).

## Kézi szerkesztés

A fájlok sima szövegek, szerkesztheted, törölheted vagy előre feltöltheted őket:

- Frontmatter nélküli `.md` fájl is memória: a neve a fájlnév, a leírása az első sor.
- A fájlnév csak kisbetűt, számot és `-` jelet tartalmazhat, legfeljebb 64 karakterig. Más nevű fájlokat az agent nem lát.
- Egy memória legfeljebb 20 000 karakter, a leírás legfeljebb 300.

## Beállítás

```yaml
memory:
  enabled: true          # false: nincs memória, sem tool, sem index
  dir: ./memory          # több agent közös mappát is használhat
  maxIndexChars: 8000
```

A `memory/` mappák gitignore-ban vannak. Ha verziózni akarod őket, töröld a megfelelő sort a `.gitignore` fájlból.

**Biztonság:** egy IRC-ről érkező feladat is írhat memóriát, és az a későbbi promptokba is bekerül. Ezért az
`operators` hostmaszkot itt is szűkre kell venni ([IRC](irc.md#jogosultság)).
