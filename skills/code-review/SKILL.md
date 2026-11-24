---
name: code-review
description: Review a code change for correctness, tests and readability, ending with a verdict. Use when asked to review code or a change.
---

# Code review

1. Find the change: `git status` / `git diff` when the workspace is a repository, otherwise the
   files named in the request.
2. Read every changed file completely, not just the diff hunks.
3. Run the tests when you are allowed to (`python3 -m pytest`, `dotnet test`, …) and report the result.
4. Check, in this order:
   - **Correctness** – does it do what was asked? Edge cases: empty input, large input, errors.
   - **Tests** – do they cover the request and the edge cases, and do they pass?
   - **Readability** – names, structure, needless complexity.
5. Report only concrete problems, each with `file:line`, what is wrong and how to fix it.
   Do not list style preferences as problems.
6. End with exactly one line: `VERDICT: APPROVED` or `VERDICT: CHANGES`.
