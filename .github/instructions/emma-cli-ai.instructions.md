---
description: "Use when working on EMMA CLI plugin-dev workflows, commands, session/bootstrap behavior, watch/build/sync flows, or agent-driven CLI tasks."
applyTo: "src/EMMA.Cli/**"
---

- Treat `session` and `doctor` as the first inspection commands before changing plugin-dev behavior, build flows, or runtime selection.
- Keep `EMMA_PLUGIN_PROFILE` explicit when suggesting or running CLI workflows for plugin development.
- Remember that `plugin.dev.json` is auto-discovered, but sample or bespoke config files require `EMMA_PLUGIN_DEV_CONFIG`.
- Prefer the normalized CLI commands (`build`, `pack`, `watch`, `serve`, `scenario`) over bespoke scripts unless the task explicitly targets script parity or packaging behavior.
- When changing native profile build behavior, keep the CLI publish settings aligned with the proven ASP.NET packaging flow so Linux and Windows dev builds stay close to pack output shape.
- When changing watch or sync behavior, verify both manual `build` and watch-triggered rebuild flows because they share the same session backend but have different entry points.