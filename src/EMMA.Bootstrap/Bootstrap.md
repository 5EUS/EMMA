# Bootstrap (EMMA)

Bootstrap is the composition root for the runtime. It wires Application
pipelines to Infrastructure adapters without adding any behavior of its own.
This keeps the core orchestration deterministic and lets different hosts
(daemon, embedded app, CLI) decide which implementations to use.
