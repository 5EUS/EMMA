# Pipelines (EMMA)

Pipelines in EMMA are lightweight orchestration flows in the Application layer
that coordinate ports (search, page retrieval, caching, policy checks) without
owning any infrastructure or IO details.

The goal is to keep the core runtime deterministic and testable while allowing
adapters to plug in behind ports for storage, plugins, and transport.
