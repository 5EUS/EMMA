//! The wasmtime engine, plugin loading and the per-call dispatch.
//!
//! Every call gets a fresh store and instance: no state leaks between calls, a trap cannot leave
//! a half-broken instance behind, and concurrent calls never contend on one store. Anything a
//! plugin needs to remember lives host-side (cache, settings).

use std::hash::{Hash, Hasher};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use serde::Serialize;
use wasmtime::component::{Component, HasSelf, Linker};
use wasmtime::error::Context;
use wasmtime::{
    Config, Engine, Error, Result, Store, StoreLimitsBuilder, Trap, UpdateDeadline, format_err,
};

use crate::config::PluginConfig;
use crate::generated::contract::emma::core::types;
use crate::generated::contract::{Plugin as Bindings, PluginPre};
use crate::host::{HostState, Shared, plugin_error};
use crate::network::{FetchLimits, HostPolicy, Network};
use crate::wire::{ChildrenRequest, Envelope, IdsRequest, ReadRequest, SuggestRequest, WireAsset};

/// How often the epoch advances, in milliseconds. The CPU budget is measured in these ticks.
pub const TICK_MS: u64 = 10;

/// How often the epoch advances. The CPU budget is measured in these ticks.
pub const TICK: Duration = Duration::from_millis(TICK_MS);

/// The most core instances one component may create. A component is several core modules (the
/// program, the WASI adapter, shims), so this is a sanity bound, not a count of one.
const MAX_CORE_INSTANCES: usize = 64;

/// The interface id of the optional search-assist extension.
const SEARCH_ASSIST: &str = "emma:core/search-assist@1.0.0";

/// Options for the engine.
#[derive(Debug, Clone, Default)]
pub struct RuntimeOptions {
    /// The compilation target, for example `pulley64` where the platform forbids JIT (iOS).
    /// `None` means the host's own architecture.
    pub target: Option<String>,
}

/// The thread that drives the engine's epoch clock. It stops when the last owner is dropped, and
/// every [`Runtime`] and [`Plugin`] owns it, so closing the runtime first cannot leave a plugin
/// without a CPU budget.
struct Ticker {
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl Ticker {
    fn start(engine: Engine, ticks: Arc<AtomicU64>) -> std::io::Result<Self> {
        let stop = Arc::new(AtomicBool::new(false));
        let flag = Arc::clone(&stop);
        let thread = std::thread::Builder::new()
            .name("emma-epoch".to_owned())
            .spawn(move || {
                while !flag.load(Ordering::Relaxed) {
                    std::thread::sleep(TICK);
                    ticks.fetch_add(1, Ordering::Relaxed);
                    engine.increment_epoch();
                }
            })?;
        Ok(Self {
            stop,
            thread: Some(thread),
        })
    }
}

impl Drop for Ticker {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

/// One engine shared by every plugin.
pub struct Runtime {
    engine: Engine,
    ticks: Arc<AtomicU64>,
    ticker: Arc<Ticker>,
}

impl Runtime {
    /// Creates the engine and starts the epoch clock.
    ///
    /// # Errors
    /// The configuration was rejected, for example an unknown compilation target.
    pub fn new(options: &RuntimeOptions) -> Result<Self> {
        let mut config = Config::new();
        config
            .wasm_component_model(true)
            .epoch_interruption(true)
            .consume_fuel(false)
            .max_wasm_stack(512 * 1024)
            .wasm_backtrace_max_frames(None);
        if let Some(target) = &options.target {
            config
                .target(target)
                .with_context(|| format!("unsupported target '{target}'"))?;
        }
        let engine = Engine::new(&config)?;

        let ticks = Arc::new(AtomicU64::new(0));
        let ticker = Arc::new(Ticker::start(engine.clone(), Arc::clone(&ticks))?);
        Ok(Self {
            engine,
            ticks,
            ticker,
        })
    }

    /// A key that changes whenever precompiled output from one engine could not be loaded by
    /// another (different wasmtime version, target or relevant settings). Use it in cache file names.
    #[must_use]
    pub fn compatibility_key(&self) -> String {
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        self.engine
            .precompile_compatibility_hash()
            .hash(&mut hasher);
        format!("{:016x}", hasher.finish())
    }

    /// Compiles a component ahead of time. The result loads only on a runtime with the same
    /// [`compatibility_key`](Self::compatibility_key) and skips the compiler at load time, which
    /// matters where JIT is forbidden.
    ///
    /// # Errors
    /// The bytes are not a valid component.
    pub fn precompile(&self, component: &[u8]) -> Result<Vec<u8>> {
        self.engine.precompile_component(component)
    }

    /// Opens a plugin: compiles (or loads precompiled) its component, links the host imports and
    /// checks it implements the base contract.
    ///
    /// # Errors
    /// The configuration is out of range, the file cannot be read, or the component does not
    /// implement `emma:core`.
    pub fn open(&self, config: &PluginConfig) -> Result<Plugin> {
        config
            .validate()
            .map_err(|message| format_err!("{message}"))?;
        let component = if config.precompiled {
            // SAFETY: deserializing precompiled code is only sound for bytes this host produced with
            // `precompile` on a compatible engine. The .NET side only passes cache files it wrote
            // itself, named by `compatibility_key`, after verifying the signed source component.
            unsafe { Component::deserialize_file(&self.engine, &config.component_path) }
        } else {
            Component::from_file(&self.engine, &config.component_path)
        }
        .with_context(|| format!("could not load '{}'", config.component_path.display()))?;

        let mut linker: Linker<HostState> = Linker::new(&self.engine);
        wasmtime_wasi::p2::add_to_linker_sync(&mut linker)?;
        Bindings::add_to_linker::<_, HasSelf<_>>(&mut linker, |state| state)?;
        let pre = PluginPre::new(linker.instantiate_pre(&component)?)
            .context("the component does not implement the emma:core plugin contract")?;
        let has_search_assist = component.get_export_index(None, SEARCH_ASSIST).is_some();

        let network = Network::new(
            HostPolicy::new(&config.permissions.network),
            FetchLimits::default(),
            config.testing.allow_private_addresses,
        );
        Ok(Plugin {
            engine: self.engine.clone(),
            ticks: Arc::clone(&self.ticks),
            _ticker: Arc::clone(&self.ticker),
            component,
            pre,
            shared: Arc::new(Shared::new(config, network)),
            budget_ticks: u64::from(config.limits.cpu_budget_ms)
                .div_ceil(TICK_MS)
                .max(1),
            memory_bytes: u64::from(config.limits.memory_mb) * 1024 * 1024,
            call_timeout: Duration::from_millis(u64::from(config.limits.call_timeout_ms)),
            has_search_assist,
        })
    }
}

/// The result of one call: a JSON envelope and, for `read`, the bytes as a separate buffer.
#[derive(Debug)]
pub struct CallOutput {
    /// The [`Envelope`] as JSON.
    pub json: Vec<u8>,
    /// Binary payload (the bytes of a `read`), empty otherwise.
    pub blob: Vec<u8>,
}

/// An opened plugin.
pub struct Plugin {
    engine: Engine,
    ticks: Arc<AtomicU64>,
    _ticker: Arc<Ticker>,
    component: Component,
    pre: PluginPre<HostState>,
    shared: Arc<Shared>,
    budget_ticks: u64,
    memory_bytes: u64,
    call_timeout: Duration,
    has_search_assist: bool,
}

/// Why a call produced no result.
enum Failure {
    /// The plugin returned an error.
    Plugin(types::PluginError),
    /// The plugin crashed or was stopped.
    Trapped(String),
    /// The request itself was malformed or names an operation that does not exist.
    BadRequest(String),
}

struct Dispatched {
    value: serde_json::Value,
    blob: Vec<u8>,
}

impl Plugin {
    /// Whether the component implements the optional search-assist extension.
    #[must_use]
    pub fn has_search_assist(&self) -> bool {
        self.has_search_assist
    }

    /// Runs one operation in a fresh instance and returns its envelope.
    #[must_use]
    pub fn call(&self, operation: &str, request: &[u8]) -> CallOutput {
        let mut store = self.new_store();
        let result = self.dispatch(&mut store, operation, request);
        let logs = store.data_mut().take_logs();

        let (ok, error, trapped, blob) = match result {
            Ok(Dispatched { value, blob }) => (Some(value), None, false, blob),
            Err(Failure::Plugin(error)) => (None, Some(error), false, Vec::new()),
            Err(Failure::BadRequest(message)) => (
                None,
                Some(plugin_error(types::ErrorCode::InvalidArgument, &message)),
                false,
                Vec::new(),
            ),
            Err(Failure::Trapped(message)) => (
                None,
                Some(plugin_error(types::ErrorCode::Internal, &message)),
                true,
                Vec::new(),
            ),
        };
        let envelope = Envelope {
            ok,
            error,
            trapped,
            logs,
        };
        let json = serde_json::to_vec(&envelope)
            .unwrap_or_else(|_| br#"{"trapped":false,"logs":[]}"#.to_vec());
        CallOutput { json, blob }
    }

    fn new_store(&self) -> Store<HostState> {
        let limits = StoreLimitsBuilder::new()
            .memory_size(usize::try_from(self.memory_bytes).unwrap_or(usize::MAX))
            .memories(8)
            .tables(16)
            .instances(MAX_CORE_INSTANCES)
            .trap_on_grow_failure(true)
            .build();
        let state = HostState::new(
            Arc::clone(&self.shared),
            Arc::clone(&self.ticks),
            limits,
            Instant::now() + self.call_timeout,
        );
        let mut store = Store::new(&self.engine, state);
        store.limiter(|state| &mut state.limits);

        // The CPU budget. Time the plugin spent waiting inside host calls (the network) is added back
        // when the deadline is reached, so only the plugin's own computation is charged.
        store.set_epoch_deadline(self.budget_ticks);
        store.epoch_deadline_callback(|mut context| {
            let host_ticks = std::mem::take(&mut context.data_mut().pending_host_ticks);
            if host_ticks > 0 {
                Ok(UpdateDeadline::Continue(host_ticks))
            } else {
                Err(format_err!(
                    "the plugin used more CPU time than its manifest allows"
                ))
            }
        });
        store
    }

    fn dispatch(
        &self,
        store: &mut Store<HostState>,
        operation: &str,
        request: &[u8],
    ) -> Result<Dispatched, Failure> {
        let instance = self
            .pre
            .instance_pre()
            .instantiate(&mut *store)
            .map_err(|error| trapped(&error))?;
        let plugin = Bindings::new(&mut *store, &instance).map_err(|error| trapped(&error))?;
        let meta = plugin.emma_core_meta();
        let catalog = plugin.emma_core_catalog();
        let assets = plugin.emma_core_assets();

        match operation {
            "describe" => json(
                meta.call_describe(&mut *store)
                    .map_err(|error| trapped(&error))?,
            ),
            "health" => json(Ok::<_, types::PluginError>(
                meta.call_health(&mut *store)
                    .map_err(|error| trapped(&error))?,
            )),
            "search" => {
                let query: types::Query = parse(request)?;
                json(
                    catalog
                        .call_search(&mut *store, &query)
                        .map_err(|error| trapped(&error))?,
                )
            }
            "children" => {
                let children: ChildrenRequest = parse(request)?;
                json(
                    catalog
                        .call_children(&mut *store, children.parent.as_deref(), &children.request)
                        .map_err(|error| trapped(&error))?,
                )
            }
            "get" => {
                let ids: IdsRequest = parse(request)?;
                json(
                    catalog
                        .call_get(&mut *store, &ids.ids)
                        .map_err(|error| trapped(&error))?,
                )
            }
            "resolve" => {
                let resolve = parse(request)?;
                let assets = assets
                    .call_resolve(&mut *store, &resolve)
                    .map_err(|error| trapped(&error))?;
                json(assets.map(|list| list.into_iter().map(WireAsset::from).collect::<Vec<_>>()))
            }
            "read" => {
                let read: ReadRequest = parse(request)?;
                let chunk = assets
                    .call_read(&mut *store, &read.token, read.range)
                    .map_err(|error| trapped(&error))?
                    .map_err(Failure::Plugin)?;
                Ok(Dispatched {
                    value: serde_json::json!({ "total": chunk.total }),
                    blob: chunk.bytes,
                })
            }
            "suggest" | "enrich" => self.search_assist(store, &instance, operation, request),
            other => Err(Failure::BadRequest(format!("Unknown operation '{other}'."))),
        }
    }

    fn search_assist(
        &self,
        store: &mut Store<HostState>,
        instance: &wasmtime::component::Instance,
        operation: &str,
        request: &[u8],
    ) -> Result<Dispatched, Failure> {
        let unsupported = || {
            Failure::Plugin(plugin_error(
                types::ErrorCode::Unsupported,
                "The plugin does not implement search-assist.",
            ))
        };
        let interface = self
            .component
            .get_export_index(None, SEARCH_ASSIST)
            .ok_or_else(unsupported)?;
        let function = self
            .component
            .get_export_index(Some(&interface), operation)
            .ok_or_else(unsupported)?;

        if operation == "suggest" {
            let suggest: SuggestRequest = parse(request)?;
            let call = instance
                .get_typed_func::<(String, u32), (Result<Vec<String>, types::PluginError>,)>(
                    &mut *store,
                    &function,
                )
                .map_err(|error| trapped(&error))?;
            let (result,) = call
                .call(&mut *store, (suggest.text, suggest.limit))
                .map_err(|error| trapped(&error))?;
            json(result)
        } else {
            let ids: IdsRequest = parse(request)?;
            let call = instance
                .get_typed_func::<(Vec<String>,), (Result<Vec<types::Node>, types::PluginError>,)>(
                    &mut *store,
                    &function,
                )
                .map_err(|error| trapped(&error))?;
            let (result,) = call
                .call(&mut *store, (ids.ids,))
                .map_err(|error| trapped(&error))?;
            json(result)
        }
    }
}

fn parse<T: serde::de::DeserializeOwned>(request: &[u8]) -> Result<T, Failure> {
    serde_json::from_slice(request)
        .map_err(|error| Failure::BadRequest(format!("The request is not valid: {error}.")))
}

fn json<T: Serialize>(result: Result<T, types::PluginError>) -> Result<Dispatched, Failure> {
    let value = result.map_err(Failure::Plugin)?;
    serde_json::to_value(value)
        .map(|value| Dispatched {
            value,
            blob: Vec::new(),
        })
        .map_err(|error| Failure::Trapped(format!("The result could not be serialized: {error}.")))
}

/// Turns a wasmtime error (a trap, a limit, a link failure) into a plain-language failure.
fn trapped(error: &Error) -> Failure {
    let message = match error.downcast_ref::<Trap>() {
        Some(Trap::UnreachableCodeReached) => "the plugin crashed".to_owned(),
        Some(Trap::Interrupt) => {
            "the plugin used more CPU time than its manifest allows".to_owned()
        }
        Some(Trap::OutOfFuel) => "the plugin ran out of fuel".to_owned(),
        Some(other) => format!("the plugin trapped: {other}"),
        None => error.to_string(),
    };
    Failure::Trapped(format!("Plugin call failed: {message}."))
}
