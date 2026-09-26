# Repository guidance

## C# formatting

Use the pinned CSharpier tool and the repository `.editorconfig` for C# formatting.
Run `dotnet tool restore` when needed, then `./scripts/Format-CSharp.ps1` after C#
edits and `./scripts/Format-CSharp.ps1 -Check` before finishing. The script applies
IDE0011 required braces before CSharpier's 140-column layout, including test
projects outside the solution. Missing braces are also build errors. Do not
format generated files or vendor sources, or run a competing
whitespace formatter over CSharpier's output. See [docs/code-style.md](docs/code-style.md).

## Diagnostic log analysis

Use [.agents/skills/analyze-lockscreen-logs/SKILL.md](.agents/skills/analyze-lockscreen-logs/SKILL.md) when investigating LockscreenGif logs, diagnostic reports, misleading findings, or collector behavior.

Continuously update and iterate on this skill as investigations reveal new issues, useful checks, or improvements to log analysis. Update its guidance, references, or analysis script in the same change that establishes the lesson. Keep observations distinct from hypotheses and add focused regression coverage when script behavior changes.

Keep the skill and its supporting files committed in this repository. Do not commit user reports, logs, personal paths, or exported diagnostic ZIPs; use small synthetic fixtures for tests.
