# Pipelines (EMMA)

Pipelines in EMMA are lightweight orchestration flows in the Application layer
that coordinate ports (search, page retrieval, caching, policy checks) without
owning any infrastructure or IO details.

The goal is to keep the core runtime deterministic and testable while allowing
adapters to plug in behind ports for storage, plugins, and transport.

Compression follows the same pattern: the pipeline resolves a media-type
adapter, asks it for a plan, and persists any derived artifacts through a
separate store port instead of coupling derived outputs to canonical caches.
