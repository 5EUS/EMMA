//! The host side of the `emma:core` contract: what a plugin may ask of the host, and the
//! per-call state that bounds it (log buffer, request count, deadline, CPU-budget accounting).

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use serde::Serialize;
use wasmtime::StoreLimits;
use wasmtime::component::ResourceTable;
use wasmtime_wasi::p2::pipe::MemoryOutputPipe;
use wasmtime_wasi::{WasiCtx, WasiCtxView, WasiView};

use crate::cache::PluginCache;
use crate::config::{PluginConfig, Testing};
use crate::generated::contract::emma::core::{host, types};
use crate::network::{FetchError, FetchRequest, Network};

/// The most log entries kept for one call; the rest are counted, not stored.
const MAX_LOG_ENTRIES: usize = 200;
/// The longest log message kept.
const MAX_LOG_MESSAGE_BYTES: usize = 4096;
/// The most host fetches one call may make.
const MAX_REQUESTS_PER_CALL: u32 = 100;
/// How much of a plugin's standard error is kept.
const STDERR_CAPACITY: usize = 64 * 1024;

/// One line a plugin logged.
#[derive(Debug, Clone, Serialize)]
pub struct LogEntry {
    /// `trace`, `debug`, `info`, `warn`, `error`.
    pub level: &'static str,
    /// The message, truncated if very long.
    pub message: String,
}

/// What stays the same across every call to one plugin.
pub struct Shared {
    /// Outbound access, with the plugin's allow-list.
    pub network: Network,
    /// The plugin's private cache.
    pub cache: PluginCache,
    /// User settings by key.
    pub settings: HashMap<String, Vec<String>>,
    /// Whether the cache is available.
    pub cache_allowed: bool,
    /// Test-only switches.
    pub testing: Testing,
}

impl Shared {
    /// Builds the shared state from a validated configuration.
    #[must_use]
    pub fn new(config: &PluginConfig, network: Network) -> Self {
        Self {
            network,
            cache: PluginCache::default(),
            settings: config.settings.clone(),
            cache_allowed: config.permissions.cache,
            testing: config.testing.clone(),
        }
    }
}

/// The store data for one call.
pub struct HostState {
    wasi: WasiCtx,
    table: ResourceTable,
    /// Memory and table limits.
    pub limits: StoreLimits,
    shared: Arc<Shared>,
    ticks: Arc<AtomicU64>,
    deadline: Instant,
    requests: u32,
    logs: Vec<LogEntry>,
    dropped_logs: u32,
    /// Epoch ticks spent inside host calls since the CPU deadline was last extended.
    pub pending_host_ticks: u64,
    stderr: MemoryOutputPipe,
}

impl HostState {
    /// Creates the state for one call. `ticks` is the runtime's tick counter, the same clock the CPU
    /// budget is measured in. The WASI context is empty: no preopened directories, no
    /// sockets, no environment, no standard input. Standard error is captured, not inherited.
    #[must_use]
    pub fn new(
        shared: Arc<Shared>,
        ticks: Arc<AtomicU64>,
        limits: StoreLimits,
        deadline: Instant,
    ) -> Self {
        let stderr = MemoryOutputPipe::new(STDERR_CAPACITY);
        let mut builder = WasiCtx::builder();
        builder
            .stderr(stderr.clone())
            .allow_tcp(false)
            .allow_udp(false)
            .allow_ip_name_lookup(false)
            .socket_addr_check(|_, _| Box::pin(async { false }));
        Self {
            wasi: builder.build(),
            table: ResourceTable::new(),
            limits,
            shared,
            ticks,
            deadline,
            requests: 0,
            logs: Vec::new(),
            dropped_logs: 0,
            pending_host_ticks: 0,
            stderr,
        }
    }

    /// Takes the log entries, adding the plugin's captured standard error and a note when entries
    /// were dropped.
    pub fn take_logs(&mut self) -> Vec<LogEntry> {
        let mut logs = std::mem::take(&mut self.logs);
        let stderr = self.stderr.contents();
        if !stderr.is_empty() {
            logs.push(LogEntry {
                level: "error",
                message: truncate(&String::from_utf8_lossy(&stderr)),
            });
        }
        if self.dropped_logs > 0 {
            logs.push(LogEntry {
                level: "warn",
                message: format!("{} log entries were dropped.", self.dropped_logs),
            });
        }
        logs
    }

    fn charge_host_time(&mut self, started: u64) {
        self.pending_host_ticks += self.ticks.load(Ordering::Relaxed).saturating_sub(started);
    }
}

impl WasiView for HostState {
    fn ctx(&mut self) -> WasiCtxView<'_> {
        WasiCtxView {
            ctx: &mut self.wasi,
            table: &mut self.table,
        }
    }
}

impl types::Host for HostState {}

impl host::Host for HostState {
    fn log(&mut self, level: host::LogLevel, message: String) {
        if self.logs.len() >= MAX_LOG_ENTRIES {
            self.dropped_logs += 1;
            return;
        }
        let level = match level {
            host::LogLevel::Trace => "trace",
            host::LogLevel::Debug => "debug",
            host::LogLevel::Info => "info",
            host::LogLevel::Warn => "warn",
            host::LogLevel::Error => "error",
        };
        self.logs.push(LogEntry {
            level,
            message: truncate(&message),
        });
    }

    fn now_ms(&mut self) -> i64 {
        self.shared.testing.fixed_now_ms.unwrap_or_else(|| {
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map_or(0, |elapsed| {
                    i64::try_from(elapsed.as_millis()).unwrap_or(i64::MAX)
                })
        })
    }

    fn http_fetch(
        &mut self,
        request: host::HttpRequest,
    ) -> Result<host::HttpResponse, types::PluginError> {
        if self.requests >= MAX_REQUESTS_PER_CALL {
            return Err(plugin_error(
                types::ErrorCode::RateLimited,
                "This call made too many requests.",
            ));
        }
        self.requests += 1;

        let fetch = FetchRequest {
            method: request.method,
            url: request.url,
            headers: request
                .headers
                .into_iter()
                .map(|h| (h.name, h.value))
                .collect(),
            body: request.body,
            timeout_ms: request.timeout_ms,
        };
        // Time spent waiting on the network is not the plugin's CPU time: it is added back to the budget.
        let started = self.ticks.load(Ordering::Relaxed);
        let result = self.shared.network.fetch(&fetch, self.deadline);
        self.charge_host_time(started);

        match result {
            Ok(response) => Ok(host::HttpResponse {
                status: response.status,
                headers: response
                    .headers
                    .into_iter()
                    .map(|(name, value)| types::Header { name, value })
                    .collect(),
                body: response.body,
            }),
            Err(FetchError::Refused(message)) => {
                Err(plugin_error(types::ErrorCode::InvalidArgument, &message))
            }
            Err(FetchError::Unavailable(message)) => {
                Err(plugin_error(types::ErrorCode::Unavailable, &message))
            }
        }
    }

    fn cache_get(&mut self, key: String) -> Option<Vec<u8>> {
        if !self.shared.cache_allowed {
            return None;
        }
        self.shared.cache.get(&key, Instant::now())
    }

    fn cache_put(&mut self, key: String, value: Vec<u8>, ttl_ms: u32) {
        if self.shared.cache_allowed {
            self.shared.cache.put(
                &key,
                value,
                Duration::from_millis(u64::from(ttl_ms)),
                Instant::now(),
            );
        }
    }

    fn setting(&mut self, key: String) -> Option<String> {
        self.shared
            .settings
            .get(&key)
            .and_then(|values| values.first().cloned())
    }

    fn setting_values(&mut self, key: String) -> Vec<String> {
        self.shared.settings.get(&key).cloned().unwrap_or_default()
    }
}

/// Builds a contract error.
#[must_use]
pub fn plugin_error(code: types::ErrorCode, message: &str) -> types::PluginError {
    types::PluginError {
        code,
        message: message.to_owned(),
        retry_after_ms: None,
    }
}

fn truncate(message: &str) -> String {
    if message.len() <= MAX_LOG_MESSAGE_BYTES {
        return message.to_owned();
    }
    let mut end = MAX_LOG_MESSAGE_BYTES;
    while !message.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}…", &message[..end])
}
