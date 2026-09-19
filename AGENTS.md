# Repository guidance

## Diagnostic log analysis

Use [.agents/skills/analyze-lockscreen-logs/SKILL.md](.agents/skills/analyze-lockscreen-logs/SKILL.md) when investigating LockscreenGif logs, diagnostic reports, misleading findings, or collector behavior.

Continuously update and iterate on this skill as investigations reveal new issues, useful checks, or improvements to log analysis. Update its guidance, references, or analysis script in the same change that establishes the lesson. Keep observations distinct from hypotheses and add focused regression coverage when script behavior changes.

Keep the skill and its supporting files committed in this repository. Do not commit user reports, logs, personal paths, or exported diagnostic ZIPs; use small synthetic fixtures for tests.
