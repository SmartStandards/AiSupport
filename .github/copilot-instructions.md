# Copilot Instructions

## Project Guidelines
- Chat UI design preference (AiSupport): level comboboxes for skill/tool sources belong only in the main settings; chat details show status-dependent labels/buttons instead (skills: hidden/announced/(partially) consumed with info popup; tools: hidden/announced (confirmation required)/always allowed.
- AiSupport security model: tool source detail settings (e.g. file system allow-list) are admin-only; hidden tool sources are the admin's last word; power users may use visible tools purposefully but the provider-internal settings act as a second line of defense, so globally releasing tools like the file system tools is fine when restricted to dedicated directories.