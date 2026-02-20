# Milestone 1 - Core runtime skeleton

## Scope

- Core projects live under /src with EMMA.* naming.
- Domain contains minimal media entities, errors, and capability policy scaffolding.
- Application defines ports and the paged media pipeline stub.
- Infrastructure provides in-memory adapters and a permissive policy evaluator.
- Bootstrap wires a minimal in-memory runtime.
- CLI runs a smoke flow for manual validation.

## Manual validation points

1) Build and run the CLI to confirm the wiring works end-to-end.
2) Inspect search results, chapter lookup, and page URI output.
3) Confirm policy evaluation can block when replaced with a stricter evaluator.

## Notes

- Pipeline caching is intentionally small and in-memory only.
- Request contracts are placeholders and will grow with IPC.
