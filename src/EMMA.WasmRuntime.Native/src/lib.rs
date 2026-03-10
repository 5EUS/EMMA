use anyhow::{anyhow, Context, Result};
use std::collections::HashMap;
use std::env;
use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};
use std::{sync::mpsc, thread};
use serde_json::json;
use tokio::runtime::{Builder as TokioRuntimeBuilder, Runtime as TokioRuntime};
use wasmtime::component::{Component, Linker, ResourceTable};
use wasmtime::{Config, Engine, OptLevel, Store, Strategy};
use wasmtime_wasi::{DirPerms, FilePerms};
use wasmtime_wasi::p2::bindings::Command;
use wasmtime_wasi::p2::pipe::MemoryOutputPipe;
use wasmtime_wasi::p2::{IoView, WasiCtx, WasiCtxBuilder, WasiView};
use wasmtime_wasi_http::{WasiHttpCtx, WasiHttpView};

wasmtime::component::bindgen!({
    world: "library",
    path: "wit/",
});

struct RunnerState {
    wasi_ctx: WasiCtx,
    http_ctx: WasiHttpCtx,
    table: ResourceTable,
}

impl WasiView for RunnerState {
    fn ctx(&mut self) -> &mut WasiCtx {
        &mut self.wasi_ctx
    }
}

impl IoView for RunnerState {
    fn table(&mut self) -> &mut ResourceTable {
        &mut self.table
    }
}

impl WasiHttpView for RunnerState {
    fn ctx(&mut self) -> &mut WasiHttpCtx {
        &mut self.http_ctx
    }
}

static ENGINE: OnceLock<Engine> = OnceLock::new();
static COMPONENT_CACHE: OnceLock<Mutex<HashMap<String, Arc<Component>>>> = OnceLock::new();
static ARG_VARIANT_CACHE: OnceLock<Mutex<HashMap<String, usize>>> = OnceLock::new();
static BRIDGE_DIR_CACHE: OnceLock<Mutex<HashMap<String, PathBuf>>> = OnceLock::new();
static MANAGED_ENTRY_CACHE: OnceLock<Mutex<HashMap<String, Vec<String>>>> = OnceLock::new();
static WORKER_CACHE: OnceLock<Mutex<HashMap<String, Arc<WorkerHandle>>>> = OnceLock::new();
static HANDLE_CACHE: OnceLock<Mutex<HashMap<u64, String>>> = OnceLock::new();
static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);
static LAST_TIMING: OnceLock<Mutex<Option<String>>> = OnceLock::new();
static ASYNC_RUNTIME: OnceLock<TokioRuntime> = OnceLock::new();

struct InvokeAttemptOutput {
    json: String,
    timing: String,
}

struct WorkerRequest {
    operation: String,
    operation_args: Vec<String>,
    timeout_ms: u32,
    response_tx: mpsc::SyncSender<Result<InvokeAttemptOutput>>,
}

struct WorkerHandle {
    sender: mpsc::SyncSender<WorkerRequest>,
}

struct WorkerContext {
    engine: &'static Engine,
    component: Arc<Component>,
    linker: Linker<RunnerState>,
    component_path: String,
    component_dir: PathBuf,
    bridge_dir: PathBuf,
    managed_entries: Vec<String>,
    variant_cache_key: String,
    preferred_variant_index: Option<usize>,
    typed_plugin: Option<TypedPlugin>,
}

struct TypedPlugin {
    store: Store<RunnerState>,
    instance: wasmtime::component::Instance,
}

fn set_last_timing(value: String) {
    if let Ok(mut guard) = LAST_TIMING
        .get_or_init(|| Mutex::new(None))
        .lock()
    {
        *guard = Some(value);
    }
}

fn take_last_timing() -> Option<String> {
    LAST_TIMING
        .get_or_init(|| Mutex::new(None))
        .lock()
        .ok()
        .and_then(|mut guard| guard.take())
}

fn get_engine() -> Result<&'static Engine> {
    if let Some(engine) = ENGINE.get() {
        return Ok(engine);
    }

    let mut config = Config::new();
    config.wasm_component_model(true);
    config.async_support(true);
    config.epoch_interruption(true);
    config.strategy(Strategy::Cranelift);
    config.cranelift_opt_level(OptLevel::Speed);

    let runtime_target = resolve_runtime_target();
    if let Some(target) = runtime_target.as_deref() {
        config
            .target(target)
            .map_err(|e| anyhow!("failed to set Wasmtime target '{target}': {e}"))?;
    }

    let engine = Engine::new(&config).context("failed to create wasmtime engine")?;
    let _ = ENGINE.set(engine);
    ENGINE
        .get()
        .ok_or_else(|| anyhow!("failed to initialize shared wasmtime engine"))
}

fn resolve_runtime_target() -> Option<String> {
    if let Ok(value) = env::var("EMMA_WASMTIME_TARGET") {
        let trimmed = value.trim();
        if !trimmed.is_empty() {
            return Some(trimmed.to_string());
        }
    }

    match env::consts::OS {
        "ios" => Some("pulley64".to_string()),
        _ => None,
    }
}

fn get_async_runtime() -> Result<&'static TokioRuntime> {
    if let Some(runtime) = ASYNC_RUNTIME.get() {
        return Ok(runtime);
    }

    let runtime = TokioRuntimeBuilder::new_current_thread()
        .enable_all()
        .build()
        .context("failed to create async runtime")?;
    let _ = ASYNC_RUNTIME.set(runtime);

    ASYNC_RUNTIME
        .get()
        .ok_or_else(|| anyhow!("failed to initialize async runtime"))
}

fn load_component(engine: &Engine, component_path: &str) -> Result<Arc<Component>> {
    let component_path_abs = std::fs::canonicalize(component_path)
        .unwrap_or_else(|_| Path::new(component_path).to_path_buf());
    let cache_key = component_path_abs.to_string_lossy().to_string();

    let cache = COMPONENT_CACHE.get_or_init(|| Mutex::new(HashMap::new()));

    {
        let guard = cache
            .lock()
            .map_err(|_| anyhow!("component cache lock poisoned"))?;
        if let Some(existing) = guard.get(&cache_key) {
            return Ok(existing.clone());
        }
    }

    let component = Arc::new(
        if component_path_abs
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| ext.eq_ignore_ascii_case("cwasm"))
        {
            unsafe { Component::deserialize_file(engine, &component_path_abs) }
                .context("failed to deserialize precompiled component")?
        }
        else
        {
            Component::from_file(engine, &component_path_abs)
                .context("failed to load component")?
        },
    );

    let mut guard = cache
        .lock()
        .map_err(|_| anyhow!("component cache lock poisoned"))?;
    let entry = guard
        .entry(cache_key)
        .or_insert_with(|| component.clone())
        .clone();

    Ok(entry)
}

fn create_linker(engine: &Engine) -> Result<Linker<RunnerState>> {
    let mut linker = Linker::new(engine);
    wasmtime_wasi::p2::add_to_linker_async(&mut linker)
        .context("failed to add WASI preview2 interfaces")?;
    wasmtime_wasi_http::add_only_http_to_linker_async(&mut linker)
        .context("failed to add wasi:http interfaces")?;

    Ok(linker)
}

fn get_or_create_worker(component_path: &str) -> Result<(String, Arc<WorkerHandle>)> {
    let engine = get_engine()?;
    let component_path_abs = std::fs::canonicalize(component_path)
        .unwrap_or_else(|_| Path::new(component_path).to_path_buf());
    let cache_key = component_path_abs.to_string_lossy().to_string();

    if let Some(existing) = WORKER_CACHE
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .ok()
        .and_then(|guard| guard.get(&cache_key).cloned())
    {
        return Ok((cache_key, existing));
    }

    let component = load_component(engine, component_path)?;
    let linker = create_linker(engine)?;
    let component_dir = component_path_abs
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."))
        .to_path_buf();
    let bridge_dir = resolve_bridge_dir(component_path, &component_path_abs)?;
    let managed_entries = resolve_managed_entry_candidates_cached(&component_path_abs, &component_dir);
    let preferred_variant_index = ARG_VARIANT_CACHE
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .ok()
        .and_then(|guard| guard.get(&cache_key).copied());

    let (tx, rx) = mpsc::sync_channel::<WorkerRequest>(64);
    let context = WorkerContext {
        engine,
        component,
        linker,
        component_path: component_path.to_string(),
        component_dir,
        bridge_dir,
        managed_entries,
        variant_cache_key: cache_key.clone(),
        preferred_variant_index,
        typed_plugin: None,
    };

    thread::spawn(move || worker_loop(context, rx));

    let handle = Arc::new(WorkerHandle { sender: tx });
    let cache = WORKER_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    let mut guard = cache
        .lock()
        .map_err(|_| anyhow!("worker cache lock poisoned"))?;
    let entry = guard
        .entry(cache_key.clone())
        .or_insert_with(|| handle.clone())
        .clone();

    Ok((cache_key, entry))
}

fn invalidate_worker(cache_key: &str) {
    if let Ok(mut guard) = WORKER_CACHE
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
    {
        guard.remove(cache_key);
    }
}

fn worker_loop(mut context: WorkerContext, rx: mpsc::Receiver<WorkerRequest>) {
    while let Ok(request) = rx.recv() {
        let start = Instant::now();
        let result = invoke_with_context(
            &mut context,
            &request.operation,
            &request.operation_args,
        )
        .map(|mut output| {
            output.timing = format!(
                "{} workerQueueMs={} workerTimeoutMs={}",
                output.timing,
                start.elapsed().as_millis(),
                request.timeout_ms
            );
            output
        });

        let _ = request.response_tx.send(result);
    }
}

fn invoke_with_context(
    context: &mut WorkerContext,
    operation: &str,
    operation_args: &[String],
) -> Result<InvokeAttemptOutput> {
    if let Some(output) = invoke_typed_operation(context, operation, operation_args)? {
        return Ok(output);
    }

    let total_start = Instant::now();
    let mut failures = Vec::new();
    let mut attempt_timings = Vec::new();
    let arg_variants = build_arg_variants(
        &context.component_path,
        operation,
        operation_args,
        &context.managed_entries,
    );

    if let Some(index) = context.preferred_variant_index {
        if index < arg_variants.len() {
            let preferred_args = &arg_variants[index];
            match invoke_component_once_inner(
                context,
                preferred_args,
            ) {
                Ok(output) => {
                    attempt_timings.push(format!("preferred#{index}:{}", output.timing));
                    return Ok(InvokeAttemptOutput {
                        json: output.json,
                        timing: format!(
                            "workerMs={} attempts={} preferred={:?}",
                            total_start.elapsed().as_millis(),
                            attempt_timings.join(" | "),
                            context.preferred_variant_index
                        ),
                    });
                }
                Err(err) => {
                    attempt_timings.push(format!(
                        "preferred#{index}:err={}ms",
                        total_start.elapsed().as_millis()
                    ));
                    failures.push(format!("args={:?}: {err:#}", preferred_args));
                }
            }
        }
    }

    for (index, wasi_args) in arg_variants.iter().enumerate() {
        if context
            .preferred_variant_index
            .is_some_and(|preferred| preferred == index)
        {
            continue;
        }

        match invoke_component_once_inner(
            context,
            wasi_args,
        ) {
            Ok(output) => {
                attempt_timings.push(format!("idx{index}:{}", output.timing));
                context.preferred_variant_index = Some(index);
                if let Ok(mut guard) = ARG_VARIANT_CACHE
                    .get_or_init(|| Mutex::new(HashMap::new()))
                    .lock()
                {
                    guard.insert(context.variant_cache_key.clone(), index);
                }

                return Ok(InvokeAttemptOutput {
                    json: output.json,
                    timing: format!(
                        "workerMs={} attempts={} preferred={:?} resolved={index}",
                        total_start.elapsed().as_millis(),
                        attempt_timings.join(" | "),
                        context.preferred_variant_index
                    ),
                });
            }
            Err(err) => {
                attempt_timings.push(format!("idx{index}:err={}ms", total_start.elapsed().as_millis()));
                failures.push(format!("args={:?}: {err:#}", wasi_args));
            }
        }
    }

    Err(anyhow!(
        "all WASM invocation argv strategies failed in worker ({}):\n{}",
        attempt_timings.join(" | "),
        failures.join("\n")
    ))
}

fn build_arg_variants(
    component_path: &str,
    operation: &str,
    operation_args: &[String],
    managed_entries: &[String],
) -> Vec<Vec<String>> {
    let mut arg_variants = Vec::new();

    if !managed_entries.is_empty() {
        for entry in managed_entries {
            let entry_rooted = if entry.starts_with('/') {
                entry.clone()
            } else {
                format!("/{entry}")
            };

            let mut variant1 = vec!["dotnet.wasm".to_string(), entry.clone(), operation.to_string()];
            variant1.extend(operation_args.iter().cloned());
            arg_variants.push(variant1);

            let mut variant2 = vec!["dotnet.wasm".to_string(), entry_rooted.clone(), operation.to_string()];
            variant2.extend(operation_args.iter().cloned());
            arg_variants.push(variant2);

            let mut variant3 = vec![component_path.to_string(), entry_rooted, operation.to_string()];
            variant3.extend(operation_args.iter().cloned());
            arg_variants.push(variant3);

            let mut variant4 = vec![component_path.to_string(), entry.clone(), operation.to_string()];
            variant4.extend(operation_args.iter().cloned());
            arg_variants.push(variant4);
        }
    } else {
        let mut args = vec![operation.to_string()];
        args.extend(operation_args.iter().cloned());
        arg_variants.push(args);

        let mut args = vec![component_path.to_string(), operation.to_string()];
        args.extend(operation_args.iter().cloned());
        arg_variants.push(args);
    }

    arg_variants
}

fn invoke_component(
    component_path: &str,
    operation: &str,
    operation_args: &[String],
    timeout_ms: u32,
) -> Result<String> {
    let total_start = Instant::now();
    let worker_start = Instant::now();
    let (cache_key, worker) = get_or_create_worker(component_path)?;
    let worker_acquire_ms = worker_start.elapsed().as_millis();

    let (response_tx, response_rx) = mpsc::sync_channel(1);
    let request = WorkerRequest {
        operation: operation.to_string(),
        operation_args: operation_args.to_vec(),
        timeout_ms,
        response_tx,
    };

    if worker.sender.send(request).is_err() {
        invalidate_worker(&cache_key);

        let (retry_cache_key, retry_worker) = get_or_create_worker(component_path)?;
        let (retry_tx, retry_rx) = mpsc::sync_channel(1);
        let retry_request = WorkerRequest {
            operation: operation.to_string(),
            operation_args: operation_args.to_vec(),
            timeout_ms,
            response_tx: retry_tx,
        };

        retry_worker
            .sender
            .send(retry_request)
            .map_err(|_| anyhow!("WASM worker channel send failed after retry"))?;

        return recv_worker_response(
            &retry_cache_key,
            operation,
            timeout_ms,
            worker_acquire_ms,
            total_start,
            retry_rx,
        );
    }

    recv_worker_response(
        &cache_key,
        operation,
        timeout_ms,
        worker_acquire_ms,
        total_start,
        response_rx,
    )
}

fn recv_worker_response(
    cache_key: &str,
    operation: &str,
    timeout_ms: u32,
    worker_acquire_ms: u128,
    total_start: Instant,
    response_rx: mpsc::Receiver<Result<InvokeAttemptOutput>>,
) -> Result<String> {
    let response = if timeout_ms == 0 {
        response_rx
            .recv()
            .map_err(|_| anyhow!("WASM worker response channel disconnected"))?
    } else {
        match response_rx.recv_timeout(Duration::from_millis(timeout_ms as u64)) {
            Ok(result) => result,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                invalidate_worker(cache_key);
                return Err(anyhow!(
                    "WASM component invocation timed out after {timeout_ms}ms"
                ));
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                invalidate_worker(cache_key);
                return Err(anyhow!("WASM worker response channel disconnected"));
            }
        }
    };

    match response {
        Ok(output) => {
            set_last_timing(format!(
                "[TEMP_TIMING_REMOVE] wasmInvoke op={operation} workerAcquireMs={worker_acquire_ms} totalMs={} detail={}",
                total_start.elapsed().as_millis(),
                output.timing
            ));

            Ok(output.json)
        }
        Err(err) => {
            set_last_timing(format!(
                "[TEMP_TIMING_REMOVE] wasmInvoke op={operation} failed workerAcquireMs={worker_acquire_ms} totalMs={} err={:#}",
                total_start.elapsed().as_millis(),
                err
            ));
            Err(err)
        }
    }
}

fn invoke_component_once_inner(
    context: &mut WorkerContext,
    wasi_args: &[String],
) -> Result<InvokeAttemptOutput> {
    let inner_start = Instant::now();
    let stdout_pipe = MemoryOutputPipe::new(2 * 1024 * 1024);
    let stderr_pipe = MemoryOutputPipe::new(2 * 1024 * 1024);

    let wasi_setup_start = Instant::now();
    let state = build_runner_state(context, wasi_args, Some(stdout_pipe.clone()), Some(stderr_pipe.clone()))?;
    let wasi_setup_ms = wasi_setup_start.elapsed().as_millis();

    let runtime = get_async_runtime()?;
    let mut instantiate_ms = 0u128;
    let mut instantiate_us = 0u128;
    let mut run_ms = 0u128;
    let mut run_us = 0u128;
    let run_result = runtime.block_on(async {
        let instantiate_start = Instant::now();
        let mut store = Store::new(context.engine, state);
        store.set_epoch_deadline(1);
        let command = Command::instantiate_async(&mut store, context.component.as_ref(), &context.linker)
            .await
            .context("failed to instantiate command component")?;
        instantiate_ms = instantiate_start.elapsed().as_millis();
        instantiate_us = instantiate_start.elapsed().as_micros();

        let run_start = Instant::now();
        let result = command
            .wasi_cli_run()
            .call_run(&mut store)
            .await;
        run_ms = run_start.elapsed().as_millis();
        run_us = run_start.elapsed().as_micros();
        Ok::<_, anyhow::Error>(result)
    })?;

    let stdout = String::from_utf8_lossy(&stdout_pipe.contents()).trim().to_string();
    let stderr = String::from_utf8_lossy(&stderr_pipe.contents()).trim().to_string();

    match run_result {
        Ok(inner) => {
            if inner.is_err() {
                let mut parts = Vec::new();
                if !stdout.is_empty() {
                    parts.push(format!("stdout: {stdout}"));
                }
                if !stderr.is_empty() {
                    parts.push(format!("stderr: {stderr}"));
                }

                let detail = if parts.is_empty() {
                    "component returned failing exit status".to_string()
                } else {
                    parts.join("\n")
                };
                return Err(anyhow!(detail));
            }
        }
        Err(err) => {
            let mut parts = Vec::new();
            if !stdout.is_empty() {
                parts.push(format!("stdout: {stdout}"));
            }
            if !stderr.is_empty() {
                parts.push(format!("stderr: {stderr}"));
            }
            parts.push(format!("wasmtime: {err:#}"));
            let detail = parts.join("\n");

            return Err(anyhow!(detail));
        }
    }

    if stdout.is_empty() {
        let detail = if stderr.is_empty() {
            "WASM component operation returned empty output".to_string()
        } else {
            format!("WASM component operation returned empty output. {stderr}")
        };
        return Err(anyhow!(detail));
    }

    if let Some(json_payload) = extract_json_payload(&stdout) {
        let timing = format!(
            "innerMs={} innerUs={} linkerMs=0 linkerUs=0 wasiSetupMs={} wasiSetupUs={} instantiateMs={} instantiateUs={} runMs={} runUs={} stdoutBytes={} stderrBytes={} linkerReuse=1",
            inner_start.elapsed().as_millis(),
            inner_start.elapsed().as_micros(),
            wasi_setup_ms,
            wasi_setup_start.elapsed().as_micros(),
            instantiate_ms,
            instantiate_us,
            run_ms,
            run_us,
            stdout.len(),
            stderr.len()
        );
        return Ok(InvokeAttemptOutput {
            json: json_payload,
            timing,
        });
    }

    let mut parts = vec!["WASM component output did not include JSON payload.".to_string()];
    parts.push(format!("stdout: {stdout}"));
    if !stderr.is_empty() {
        parts.push(format!("stderr: {stderr}"));
    }

    Err(anyhow!(parts.join("\n")))
}

fn resolve_bridge_dir(component_path_raw: &str, component_path_abs: &Path) -> Result<PathBuf> {
    let cache = BRIDGE_DIR_CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    let cache_key = component_path_abs.to_string_lossy().to_string();

    {
        let guard = cache
            .lock()
            .map_err(|_| anyhow!("bridge dir cache lock poisoned"))?;
        if let Some(existing) = guard.get(&cache_key) {
            return Ok(existing.clone());
        }
    }

    let bridge_dir = get_bridge_dir(component_path_raw)?;
    std::fs::create_dir_all(&bridge_dir)
        .with_context(|| format!("failed to create bridge directory at {}", bridge_dir.display()))?;

    let mut guard = cache
        .lock()
        .map_err(|_| anyhow!("bridge dir cache lock poisoned"))?;
    guard.insert(cache_key, bridge_dir.clone());
    Ok(bridge_dir)
}

fn build_runner_state(
    context: &WorkerContext,
    wasi_args: &[String],
    stdout_pipe: Option<MemoryOutputPipe>,
    stderr_pipe: Option<MemoryOutputPipe>,
) -> Result<RunnerState> {
    let mut builder = WasiCtxBuilder::new();
    builder
        .preopened_dir(&context.component_dir, "/", DirPerms::all(), FilePerms::all())
        .context("failed to preopen component directory")?;

    if let Err(e) = builder.preopened_dir(&context.bridge_dir, "/.hostbridge", DirPerms::all(), FilePerms::all()) {
        eprintln!("Warning: failed to mount bridge directory: {}", e);
    }

    builder.env("PWD", "/");
    builder.env("MONO_PATH", "/");
    builder.args(wasi_args);
    builder.inherit_env();

    if let Some(pipe) = stdout_pipe {
        builder.stdout(pipe);
    }
    if let Some(pipe) = stderr_pipe {
        builder.stderr(pipe);
    }

    Ok(RunnerState {
        wasi_ctx: builder.build(),
        http_ctx: WasiHttpCtx::new(),
        table: ResourceTable::new(),
    })
}

fn invoke_typed_operation(
    context: &mut WorkerContext,
    operation: &str,
    operation_args: &[String],
) -> Result<Option<InvokeAttemptOutput>> {
    let typed = match context.typed_plugin.as_mut() {
        Some(typed) => typed,
        None => return Ok(None),
    };

    let op_start = Instant::now();
    let json = match operation {
        "handshake" => {
            json!({
                "version": "1.0.0",
                "message": "WASM typed component runtime ready"
            })
            .to_string()
        }
        "capabilities" => {
            let func = typed
                .instance
                .get_func(&mut typed.store, "getcapabilities")
                .or_else(|| typed.instance.get_func(&mut typed.store, "library#getcapabilities"))
                .ok_or_else(|| anyhow!("missing typed export getcapabilities"))?;
            let typed_func = func.typed::<(), (ProviderCapabilities,)>(&typed.store)?;
            let (caps,) = typed_func.call(&mut typed.store, ())?;
            typed_func.post_return(&mut typed.store)?;

            let mut capabilities = vec!["health", "search"];
            if !caps.unit_kinds.is_empty() {
                capabilities.push("paged");
            }
            if !caps.asset_kinds.is_empty() {
                capabilities.push("pages");
            }
            serde_json::to_string(&capabilities)?
        }
        "search" => {
            let query = operation_args.first().cloned().unwrap_or_default();
            let func = typed
                .instance
                .get_func(&mut typed.store, "fetchmedialist")
                .or_else(|| typed.instance.get_func(&mut typed.store, "library#fetchmedialist"))
                .ok_or_else(|| anyhow!("missing typed export fetchmedialist"))?;
            let typed_func = func.typed::<(MediaType, String), (Vec<Media>,)>(&typed.store)?;
            let (media,) = typed_func.call(&mut typed.store, (MediaType::Manga, query))?;
            typed_func.post_return(&mut typed.store)?;

            let payload = media
                .into_iter()
                .map(|item| {
                    json!({
                        "id": item.id,
                        "source": context.component_path,
                        "title": item.title,
                        "mediaType": match item.mediatype {
                            MediaType::Anime => "video",
                            _ => "paged",
                        },
                        "thumbnailUrl": item.cover_url,
                        "description": item.description,
                    })
                })
                .collect::<Vec<_>>();

            serde_json::to_string(&payload)?
        }
        "chapters" => {
            let media_id = operation_args.first().cloned().unwrap_or_default();
            let func = typed
                .instance
                .get_func(&mut typed.store, "fetchunits")
                .or_else(|| typed.instance.get_func(&mut typed.store, "library#fetchunits"))
                .ok_or_else(|| anyhow!("missing typed export fetchunits"))?;
            let typed_func = func.typed::<(String,), (Vec<Unit>,)>(&typed.store)?;
            let (units,) = typed_func.call(&mut typed.store, (media_id,))?;
            typed_func.post_return(&mut typed.store)?;

            let payload = units
                .into_iter()
                .enumerate()
                .map(|(index, unit)| {
                    let number = unit
                        .number
                        .map(|v| v.floor() as i32)
                        .or_else(|| unit.number_text.as_ref().and_then(|t| t.parse::<f32>().ok()).map(|v| v.floor() as i32))
                        .unwrap_or((index + 1) as i32);

                    json!({
                        "id": unit.id,
                        "number": number,
                        "title": unit.title,
                    })
                })
                .collect::<Vec<_>>();

            serde_json::to_string(&payload)?
        }
        "page" => {
            if operation_args.len() < 3 {
                return Err(anyhow!("page requires [mediaId, chapterId, pageIndex]"));
            }

            let chapter_id = operation_args[1].clone();
            let page_index = operation_args[2]
                .parse::<usize>()
                .map_err(|_| anyhow!("invalid page index"))?;

            let assets = fetch_assets(typed, &chapter_id)?;
            let asset = assets.get(page_index).ok_or_else(|| anyhow!("page index out of range"))?;
            let payload = json!({
                "id": format!("{chapter_id}:{page_index}"),
                "index": page_index as i32,
                "contentUri": asset.url,
            });
            payload.to_string()
        }
        "pages" => {
            if operation_args.len() < 4 {
                return Err(anyhow!("pages requires [mediaId, chapterId, startIndex, count]"));
            }

            let chapter_id = operation_args[1].clone();
            let start_index = operation_args[2]
                .parse::<usize>()
                .map_err(|_| anyhow!("invalid start index"))?;
            let count = operation_args[3]
                .parse::<usize>()
                .map_err(|_| anyhow!("invalid count"))?;

            let assets = fetch_assets(typed, &chapter_id)?;
            let payload = assets
                .into_iter()
                .enumerate()
                .skip(start_index)
                .take(count)
                .map(|(index, asset)| {
                    json!({
                        "id": format!("{chapter_id}:{index}"),
                        "index": index as i32,
                        "contentUri": asset.url,
                    })
                })
                .collect::<Vec<_>>();
            serde_json::to_string(&payload)?
        }
        _ => return Ok(None),
    };

    Ok(Some(InvokeAttemptOutput {
        json,
        timing: format!(
            "typedExport=1 op={} opMs={} persistentStore=1",
            operation,
            op_start.elapsed().as_millis()
        ),
    }))
}

fn fetch_assets(typed: &mut TypedPlugin, chapter_id: &str) -> Result<Vec<Asset>> {
    let func = typed
        .instance
        .get_func(&mut typed.store, "fetchassets")
        .or_else(|| typed.instance.get_func(&mut typed.store, "library#fetchassets"))
        .ok_or_else(|| anyhow!("missing typed export fetchassets"))?;
    let typed_func = func.typed::<(String,), (Vec<Asset>,)>(&typed.store)?;
    let (assets,) = typed_func.call(&mut typed.store, (chapter_id.to_string(),))?;
    typed_func.post_return(&mut typed.store)?;
    Ok(assets)
}

fn extract_json_payload(stdout: &str) -> Option<String> {
    for line in stdout.lines().rev() {
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }

        if trimmed.starts_with('{')
            || trimmed.starts_with('[')
            || trimmed.starts_with('"')
            || trimmed == "null"
            || trimmed == "true"
            || trimmed == "false"
            || trimmed.chars().next().map(|ch| ch.is_ascii_digit() || ch == '-').unwrap_or(false)
        {
            return Some(trimmed.to_string());
        }
    }

    None
}

fn get_bridge_dir(component_path: &str) -> Result<PathBuf> {
    use sha2::{Sha256, Digest};
    
    // Hash the full component path to match C# implementation
    let mut hasher = Sha256::new();
    hasher.update(component_path.as_bytes());
    let hash_bytes = hasher.finalize();
    
    // Convert to hex string and take first 16 characters (like C# does)
    let hash_hex = format!("{:x}", hash_bytes);
    let hash_short = &hash_hex[..16];
    
    let temp_dir = std::env::temp_dir();
    Ok(temp_dir.join("emma-wasm-bridge").join(hash_short).join(".hostbridge"))
}

fn resolve_managed_entry_candidates(component_dir: &Path) -> Vec<String> {
    let dotnet_wasm = component_dir.join("dotnet.wasm");
    if !dotnet_wasm.is_file() {
        return vec![];
    }

    let entries = match std::fs::read_dir(component_dir) {
        Ok(entries) => entries,
        Err(_) => return vec![],
    };

    let mut runtimeconfigs = entries
        .filter_map(|entry| entry.ok())
        .filter_map(|entry| {
            let path = entry.path();
            let name = path.file_name()?.to_string_lossy();
            if !name.ends_with(".runtimeconfig.json") {
                return None;
            }

            let stem = name.strip_suffix(".runtimeconfig.json")?;
            Some(stem.to_string())
        })
        .collect::<Vec<_>>();

    runtimeconfigs.sort();
    runtimeconfigs.dedup();
    if runtimeconfigs.len() != 1 {
        return vec![];
    }

    let stem = runtimeconfigs.remove(0);
    let mut candidates = vec![];
    let dll_name = format!("{stem}.dll");
    let dll_path: PathBuf = component_dir.join(&dll_name);
    if dll_path.is_file() {
        candidates.push(dll_name);
    }
    candidates.push(stem.clone());

    candidates.sort();
    candidates.dedup();
    candidates
}

fn resolve_managed_entry_candidates_cached(
    component_path_abs: &Path,
    component_dir: &Path,
) -> Vec<String> {
    let cache_key = component_path_abs.to_string_lossy().to_string();
    let cache = MANAGED_ENTRY_CACHE.get_or_init(|| Mutex::new(HashMap::new()));

    if let Some(existing) = cache
        .lock()
        .ok()
        .and_then(|guard| guard.get(&cache_key).cloned())
    {
        return existing;
    }

    let resolved = resolve_managed_entry_candidates(component_dir);
    if let Ok(mut guard) = cache.lock() {
        guard.insert(cache_key, resolved.clone());
    }

    resolved
}

fn parse_args_json(operation_args_json: &str) -> Result<Vec<String>> {
    if operation_args_json.trim().is_empty() {
        return Ok(vec![]);
    }

    serde_json::from_str::<Vec<String>>(operation_args_json)
        .context("failed to parse operation args JSON")
}

fn to_c_string(value: String) -> *mut c_char {
    CString::new(value)
        .unwrap_or_else(|_| CString::new("invalid utf8 output").unwrap())
        .into_raw()
}

fn set_out(ptr: *mut *mut c_char, value: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            *ptr = value;
        }
    }
}

fn set_out_u64(ptr: *mut u64, value: u64) {
    if !ptr.is_null() {
        unsafe {
            *ptr = value;
        }
    }
}

#[no_mangle]
pub extern "C" fn emma_wasm_component_invoke(
    component_path: *const c_char,
    operation: *const c_char,
    operation_args_json: *const c_char,
    timeout_ms: u32,
    out_json: *mut *mut c_char,
    out_error: *mut *mut c_char,
) -> i32 {
    set_out(out_json, std::ptr::null_mut());
    set_out(out_error, std::ptr::null_mut());

    let result = std::panic::catch_unwind(|| -> Result<String> {
        let component_path = unsafe { CStr::from_ptr(component_path) }
            .to_string_lossy()
            .to_string();
        let operation = unsafe { CStr::from_ptr(operation) }
            .to_string_lossy()
            .to_string();
        let operation_args_json = unsafe { CStr::from_ptr(operation_args_json) }
            .to_string_lossy()
            .to_string();

        let operation_args = parse_args_json(&operation_args_json)?;
        invoke_component(&component_path, &operation, &operation_args, timeout_ms)
    });

    match result {
        Ok(Ok(json)) => {
            set_out(out_json, to_c_string(json));
            0
        }
        Ok(Err(err)) => {
            set_out(out_error, to_c_string(format!("{err:#}")));
            1
        }
        Err(_) => {
            set_out(out_error, to_c_string("native wasm runtime panic".to_string()));
            2
        }
    }
}

#[no_mangle]
pub extern "C" fn emma_wasm_plugin_open(
    component_path: *const c_char,
    out_handle: *mut u64,
    out_error: *mut *mut c_char,
) -> i32 {
    set_out_u64(out_handle, 0);
    set_out(out_error, std::ptr::null_mut());

    let result = std::panic::catch_unwind(|| -> Result<u64> {
        let component_path = unsafe { CStr::from_ptr(component_path) }
            .to_string_lossy()
            .to_string();

        let (cache_key, _) = get_or_create_worker(&component_path)?;
        let handle = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed);
        HANDLE_CACHE
            .get_or_init(|| Mutex::new(HashMap::new()))
            .lock()
            .map_err(|_| anyhow!("handle cache lock poisoned"))?
            .insert(handle, cache_key);
        Ok(handle)
    });

    match result {
        Ok(Ok(handle)) => {
            set_out_u64(out_handle, handle);
            0
        }
        Ok(Err(err)) => {
            set_out(out_error, to_c_string(format!("{err:#}")));
            1
        }
        Err(_) => {
            set_out(out_error, to_c_string("native wasm runtime panic".to_string()));
            2
        }
    }
}

#[no_mangle]
pub extern "C" fn emma_wasm_plugin_invoke(
    handle: u64,
    operation: *const c_char,
    operation_args_json: *const c_char,
    timeout_ms: u32,
    out_json: *mut *mut c_char,
    out_error: *mut *mut c_char,
) -> i32 {
    set_out(out_json, std::ptr::null_mut());
    set_out(out_error, std::ptr::null_mut());

    let result = std::panic::catch_unwind(|| {
        let operation = unsafe { CStr::from_ptr(operation) }
            .to_string_lossy()
            .to_string();
        let operation_args_json = unsafe { CStr::from_ptr(operation_args_json) }
            .to_string_lossy()
            .to_string();

        let component_path = HANDLE_CACHE
            .get_or_init(|| Mutex::new(HashMap::new()))
            .lock()
            .map_err(|_| anyhow!("handle cache lock poisoned"))?
            .get(&handle)
            .cloned()
            .ok_or_else(|| anyhow!("unknown plugin handle"))?;

        let operation_args = parse_args_json(&operation_args_json)?;
        invoke_component(&component_path, &operation, &operation_args, timeout_ms)
    });

    match result {
        Ok(Ok(json)) => {
            set_out(out_json, to_c_string(json));
            0
        }
        Ok(Err(err)) => {
            set_out(out_error, to_c_string(format!("{err:#}")));
            1
        }
        Err(_) => {
            set_out(out_error, to_c_string("native wasm runtime panic".to_string()));
            2
        }
    }
}

#[no_mangle]
pub extern "C" fn emma_wasm_plugin_close(handle: u64) {
    if handle == 0 {
        return;
    }

    let cache_key = HANDLE_CACHE
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .ok()
        .and_then(|mut guard| guard.remove(&handle));

    if let Some(key) = cache_key {
        invalidate_worker(&key);
    }
}

#[no_mangle]
pub extern "C" fn emma_wasm_runtime_free_string(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }

    unsafe {
        let _ = CString::from_raw(ptr);
    }
}

#[no_mangle]
pub extern "C" fn emma_wasm_runtime_take_last_timing() -> *mut c_char {
    match take_last_timing() {
        Some(value) => to_c_string(value),
        None => std::ptr::null_mut(),
    }
}
