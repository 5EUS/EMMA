# Infrastructure (EMMA)

Infrastructure provides concrete adapters for Application ports. These
implementations are swappable and environment-specific, keeping the core
runtime independent of IO, storage, and external systems.

This includes lightweight in-memory implementations for tests and local flows,
plus media-type-specific compression adapters that can derive reproducible
artifacts from canonical assets without changing the Application layer.
