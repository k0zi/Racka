---
name: changelog
description: Write a changelog entry in Keep a Changelog style from implementation and review notes. Use when asked for a changelog, release note or "what changed".
---

# Changelog entry

Write Markdown in this shape:

```markdown
### Added
- <user-visible addition, one line each>

### Changed
- <behaviour change>

### Fixed
- <bug fix>
```

Rules:
- Only sections that have entries. Each entry is one line, written for users, not developers.
- Describe the change from the user's point of view, e.g. `- wordcount.py prints the word count of each input line`.
- No file names unless the user runs them (e.g. a new script).
- Never invent features that are not in the notes.
