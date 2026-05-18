# Ports (EMMA)

Ports are narrow interfaces defined in the Application layer that describe
what the core runtime needs without binding to any specific implementation.

Adapters live in Infrastructure or external hosts and implement these ports to
provide search, page retrieval, caching, policy evaluation, time sources, and
derived artifact generation.

Compression is modeled as a media-type-routed adapter seam. The Application
layer resolves an adapter for the current media type, asks it to build a plan
for a canonical source asset, and persists any generated artifacts through a
store port that is independent from raw asset caches.
