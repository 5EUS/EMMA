# Media Pipeline Design

## Overview

Two pipelines are defined to keep paging and video streaming concerns isolated,
while sharing common scheduling, caching, and resilience policies.

## Paged Media Pipeline

### Flow
1) Discover media entry (search or library lookup).
2) Resolve chapters/volumes with metadata normalization.
3) Fetch pages lazily and progressively.
4) Decode and cache image assets with bounded memory.

### Caching Strategy
- Metadata cache: long TTL with ETag/Last-Modified validation.
- Page asset cache: LRU with size budget and per-title quotas.
- Prefetch: configurable window based on read position.

### Progressive Loading
- Fetch first page quickly for fast preview.
- Preload next N pages in background with low priority.
- Support partial chapter retrieval to reduce waiting time.

### Memory Management
- Strict LRU eviction and byte-budget tracking.
- Avoid decoding all pages at once; decode on demand.
- Offload large pages to disk cache; limit in-memory bitmap size.

### Error Handling
- Per-page retry with backoff.
- Chapter-level circuit breaker on repeated failures.
- Graceful degradation: skip missing pages with placeholders.

### Timeout Enforcement
- Per page request timeout with host-enforced deadline.
- Global chapter request budget to prevent stalls.

### Retry / Circuit Breaker
- Exponential backoff for transient network errors.
- Circuit breaker per plugin + media source.

## Video Media Pipeline

### Flow
1) Resolve stream metadata and available variants.
2) Select variant based on network profile and user constraints.
3) Stream segments with adaptive buffering.
4) Persist playback progress and diagnostics.

### Caching Strategy
- Segment cache with rolling window.
- Manifest cache with short TTL; revalidate on playback.
- Evict segments aggressively on low storage.

### Progressive Loading
- Start with low bitrate variant for fast startup.
- Switch to higher bitrate when buffer stabilizes.
- Maintain forward buffer target and minimal back buffer.

### Memory Management
- Buffer by time window, not by file count.
- Keep memory bounded by maximum buffer seconds.
- Avoid storing full video in memory; rely on disk-backed cache.

### Error Handling
- Retry on segment fetch with capped attempts.
- Variant fallback on repeated segment failure.
- Resume playback from last successful segment.

### Timeout Enforcement
- Segment timeout enforced at host boundary.
- Detect stalled streams and trigger variant downgrade.

### Retry / Circuit Breaker
- Circuit breaker per stream source and plugin.
- Global throttle if multiple streams from same plugin fail.

## Cross-Cutting Policies

- Unified scheduling: bounded concurrent calls per plugin.
- Backpressure: if cache budgets exceeded, halt prefetch.
- Observability: per-stage latency metrics and error rates.
- Content validation: reject malformed pages or segments before caching.
