# Jukebox Project Context

Jukebox has two supported roles:

1. An independently runnable, general-purpose media player.
2. A reusable assembly consumed by Gamelist Manager for media playback.

The dependency direction is:

```text
Gamelist Manager --> Jukebox.dll
```

Jukebox does not depend on Gamelist Manager. Consumer-specific changes must preserve Jukebox's standalone operation and general-purpose behavior.

The current Jukebox folder contains the host project, `Jukebox.Plugin.Abstractions`, and a `plugins` directory used by existing organization and build behavior. Additional plugin folders exist as workspace siblings. Only `Jukebox.Plugin.RadioBrowser` was observed with folder-local Git metadata; the other sibling plugin folders must not be assumed to be independent repositories.

`Jukebox.Plugin.ProjectM` is distinct from the sibling `Avalonia.ProjectM` library. The inspected plugin project references `Jukebox.Plugin.Abstractions` but does not declare a direct project reference to `Avalonia.ProjectM`. Their runtime and deployment relationship requires a later read-only dependency audit.

