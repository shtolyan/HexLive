# Agent prompts

These are editable text files, not model credentials or executable permissions.
There is one set, not separate Russian and English modes.

- `language.md`: follow the latest interlocutor message; retain its language on
  autonomous turns. This is appended after authored examples.
- `rules.md`: shared role and decision instructions.
- `assessment.md`: relationship assessment instructions.
- `masha-style.md`: Masha's authored manner, not her personal memories.
- `world-guidance.md`: interpretation of retrieved world rules.
- `soul.md`: template for a new personality document; existing SOUL.md is preserved.
- `text.json`: examples, memory headings, seed memories and runtime wording.
  Keys identify the source component and entry. Change values, not keys.

Sources live in `Server/HexLive.AgentHost/Prompts`. The application copies them
to `AgentPrompts` beside the executable. No translation service is called.
Language changes do not translate, sanitize or overwrite autobiographical memory.
JSON field names, action validation, permissions and relationship limits stay in code.

Only brief public intent summaries are requested; hidden reasoning is not exposed.
