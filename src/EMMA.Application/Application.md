# Application Layer (EMMA)

The Application layer orchestrates core runtime behavior without owning any
infrastructure. It expresses use cases as pipelines and uses ports to call out
to search providers, page/video providers, caching, policy evaluation, and
system time. This keeps the runtime deterministic and easy to test while still
allowing adapters to evolve independently.

In practice, the Application layer is where scheduling, retry logic, and policy
checks are coordinated. The concrete implementations live elsewhere, but the
application code decides when to call them and how to compose the results into
domain models. This is also the layer where cross-cutting concerns like
backpressure, timeouts, and caching boundaries are enforced before data enters
the Domain.
