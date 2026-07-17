# Jukebox Instructions

## Product Boundary

Jukebox is both an independently runnable, general-purpose media player and an assembly consumed by Gamelist Manager for media playback.

- Preserve standalone operation.
- Preserve the general-purpose design.
- Do not add a dependency from Jukebox to Gamelist Manager.
- Do not specialize Jukebox APIs or behavior solely around Gamelist Manager.
- Treat plugin boundaries according to current evidence; do not assume every workspace plugin folder is a separate repository.

Leave `.agents\backups` unchanged. Any backup-policy change requires a separate decision and explicit approval. Do not install custom `.codex` agents without explicit approval.

## Model Policy

Do not use Max, switch model, or change reasoning tier without explicit approval. If escalation is recommended, stop and request approval first.
