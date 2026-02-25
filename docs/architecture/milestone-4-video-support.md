# Milestone 4 - Video support

## Scope

- Add video media flow to the pipeline (search -> variants -> segments).
- Define streaming variants, segments, and playback progress models.
- Integrate bounded segment cache and buffer management.
- Ship a reference video plugin and fixtures for deterministic tests.

## Work items

1) Domain and contracts
   - Add domain models for `VideoVariant`, `VideoSegment`, `PlaybackProgress`.
   - Extend `MediaType` to include video and ensure existing rules are preserved.
   - Define normalized metadata rules for duration, codec, resolution, and bitrate.
   - Add error mapping for common stream failures (missing variant, corrupt segment, expired URL).

2) Plugin contracts
   - Introduce `IVideoProvider` (or equivalent) with flows for variants and segments.
   - Add gRPC service definitions for video variants and segment fetch.
   - Enforce request context propagation, deadlines, and cancellation.
   - Add capability flags (paged, video, streaming) to plugin manifests.

3) Pipeline integration
   - Map plugin responses to domain models and validate normalization.
   - Implement adaptive variant selection with bandwidth sampling.
   - Add per-segment timeout and retry policy with backoff.
   - Persist and restore playback progress via application ports.

4) Segment cache and buffer
   - Add segmented cache with size and time-based eviction.
   - Enforce per-title cache budget and global cache ceilings.
   - Introduce buffer manager with prefetch window and low-water mark.
   - Ensure decoded frames are not retained beyond buffer limits.

5) Reference plugin and fixtures
   - Create a sample video plugin in `EMMA.TestPlugin` with deterministic segments.
   - Add fixture data covering multiple variants and segment sizes.
   - Add a controlled bandwidth simulator for tests.

## Validation plan

- End-to-end tests: search -> variants -> segment fetch -> buffer steady state.
- Variant switch tests under simulated bandwidth changes.
- Segment cache hit and eviction tests with memory bounds.
- Playback progress persistence tests (resume from last segment).

## Dependencies

- Plugin host gRPC endpoints for video variants/segments.
- Cache service (disk spill + memory budget) available to the pipeline.

## Open questions

- Segment format: store raw bytes or containerized chunks.
- Preferred variant selection strategy (lowest latency vs highest quality).

## Status

Not started.
