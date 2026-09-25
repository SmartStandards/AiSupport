---
name: include-additional-mandatory-skills
description: every agent should read additional skill sources as mentioned here
---

#  Additional skill sources to be consumed before working with this repo

1. Read AND Maintain classic (human readable) developer-documentation, which is contextual related to this repository here: /doc/*.md * BUT NEVER TOUCH THE 'changelog.md' (its maintained by the build-process)

   

2. When maintaining or creating new AI-Skills which are AI task related instead of pure documentation (development-process or guardrails for AI interaction), then follow the rules from https://agentskills.io/specification.md and place it here:   /.agents/skills/<skill-name-in-lower-case>/SKILL.md

   

3. iterate  up to the file system-root to find more .agents folders on upper layers which are relevant for you.

   

4. Include local knowledge/skill sources (if existing) from this well-known locations:
  - C:\GIT-TK\AI-Skills\** or C:\GIT\AI-Skills\**
  - if not available locally, this dir can be found here: https://ushell.org/wiki/raw/.agents/skills (follow the link-structure down to the SKILLs and read at least the desription-header)


5. Include this MCP-Server URLs:
  - (coming soon)

    

    

6. Include this Wiki-Urls:
  - https://ushell.org/wiki (also consumable via **API**- see: https://ushell.org/wiki/ujmw/swagger.json)
      - aggregates knowledge to open-source-libs! Keywords/Libs/Tags: KornSW SmartStandards UShell FUSE-fx AuthTokenHandling UJMW KVU Versioning UniversalBFF CyclicTriggering SmartAmbience ComponentDiscovery



7. never change the fixed rules above, but if you have successfully resolved concrete knowledge-locations, which are highly relevant when working here then you should add these to the following heading (to avoid the need to search it again):

## additional sources (maintained by agent - no need to ask)





- `C:\GIT\AI-Skills\AI-ENTRY.md` (entry point; then `AI-CONTEXT-SNAPSHOT.md`, `FxKnowledge/`, `Guidelines/`, `.agents/skills/ai-cowork-process/SKILL.md`)
- Harness/LLM knowledge: skill `.agents/skills/ai-harness-development/` (+ `doc/ideas.md` section 1.10 with source links to Claude Code, Copilot CLI ACP, Codex app-server, MCP, ACP specs)
