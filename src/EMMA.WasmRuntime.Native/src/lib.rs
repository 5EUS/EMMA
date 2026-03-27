use anyhow::{anyhow, Context, Result};
use serde::Deserialize;
use serde_json::Value;
use std::collections::HashMap;
use std::env;
use std::ffi::{CStr, CString};
use std::fs;
use std::os::raw::c_char;
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};
use std::{sync::mpsc, thread};
use wasmtime::component::{Component, Linker, ResourceTable};
use wasmtime::{Config, Engine, OptLevel, Store, Strategy};
use wasmtime_wasi::p2::{IoView, WasiCtx, WasiCtxBuilder, WasiView};
use wasmtime_wasi_http::bindings::http::types::ErrorCode;
use wasmtime_wasi_http::body::HyperOutgoingBody;
use wasmtime_wasi_http::types::{default_send_request, HostFutureIncomingResponse, OutgoingRequestConfig};
use wasmtime_wasi_http::{HttpResult, WasiHttpCtx, WasiHttpView};

mod typed_bindings {
    wasmtime::component::bindgen!({
        path: "wit",
        world: "library",
        async: true,
    });
}

struct RunnerState {
    wasi_ctx: WasiCtx,
    http_ctx: WasiHttpCtx,
    table: ResourceTable,
    permitted_domains: Vec<String>,
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

    fn send_request(
        &mut self,
        request: hyper::Request<HyperOutgoingBody>,
        config: OutgoingRequestConfig,
    ) -> HttpResult<HostFutureIncomingResponse> {
        let host = request.uri().host().unwrap_or_default();
        let is_allowed = self.permitted_domains.iter().any(|domain| {
            !domain.trim().is_empty()
                && (host.eq_ignore_ascii_case(domain)
                    || host
                        .to_ascii_lowercase()
                        .ends_with(&format!(".{}", domain.to_ascii_lowercase())))
        });

        if !is_allowed {
            return Err(ErrorCode::HttpRequestDenied.into());
        }

        Ok(default_send_request(request, config))
    }
}

static ENGINE: OnceLock<Engine> = OnceLock::new();
static COMPONENT_CACHE: OnceLock<Mutex<HashMap<String, Arc<Component>>>> = OnceLock::new();
static WORKER_CACHE: OnceLock<Mutex<HashMap<String, Arc<WorkerHandle>>>> = OnceLock::new();
static HANDLE_CACHE: OnceLock<Mutex<HashMap<u64, String>>> = OnceLock::new();
static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);
static LAST_TIMING: OnceLock<Mutex<Option<String>>> = OnceLock::new();
static HOST_HTTP_CLIENT: OnceLock<reqwest::blocking::Client> = OnceLock::new();

struct InvokeAttemptOutput {
    json: String,
    timing: String,
}

struct WorkerRequest {
    operation: String,
    operation_args: Vec<String>,
    permitted_domains: Vec<String>,
    timeout_ms: u32,
    response_tx: mpsc::SyncSender<Result<InvokeAttemptOutput>>,
}

struct WorkerHandle {
    sender: mpsc::SyncSender<WorkerRequest>,
}

struct WorkerContext {
    engine: &'static Engine,
    typed_pre: typed_bindings::LibraryPre<RunnerState>,
    runtime: tokio::runtime::Runtime,
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

fn build_component_cache_key(component_path_abs: &Path) -> String {
    let base = component_path_abs.to_string_lossy().to_string();
    match fs::metadata(component_path_abs) {
        Ok(metadata) => {
            let len = metadata.len();
            let modified_ns = metadata
                .modified()
                .ok()
                .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
                .map(|duration| duration.as_nanos())
                .unwrap_or(0);
            format!("{base}|len={len}|mtime_ns={modified_ns}")
        }
        Err(_) => base,
    }
}

fn load_component(engine: &Engine, component_path_abs: &Path, cache_key: &str) -> Result<Arc<Component>> {

    let cache = COMPONENT_CACHE.get_or_init(|| Mutex::new(HashMap::new()));

    {
        let guard = cache
            .lock()
            .map_err(|_| anyhow!("component cache lock poisoned"))?;
        if let Some(existing) = guard.get(cache_key) {
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
        .entry(cache_key.to_string())
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
    let cache_key = build_component_cache_key(&component_path_abs);

    if let Some(existing) = WORKER_CACHE
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .ok()
        .and_then(|guard| guard.get(&cache_key).cloned())
    {
        return Ok((cache_key, existing));
    }

    let component = load_component(engine, &component_path_abs, &cache_key)?;
    let mut linker = create_linker(engine)?;
    typed_bindings::Library::add_to_linker::<
        RunnerState,
        wasmtime::component::HasSelf<RunnerState>,
    >(&mut linker, |state| state)
        .context("failed to add typed world imports to linker")?;

    let instance_pre = linker
        .instantiate_pre(component.as_ref())
        .context("failed to pre-instantiate component")?;

    let typed_pre = typed_bindings::LibraryPre::new(instance_pre)
        .context("failed to build typed world pre-instantiation")?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_io()
        .enable_time()
        .build()
        .context("failed to create shared tokio runtime for wasm worker")?;

    let (tx, rx) = mpsc::sync_channel::<WorkerRequest>(64);
    let context = WorkerContext {
        engine,
        typed_pre,
        runtime,
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

fn invalidate_component(cache_key: &str) {
    if let Ok(mut guard) = COMPONENT_CACHE
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
            &request.permitted_domains,
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
    permitted_domains: &[String],
) -> Result<InvokeAttemptOutput> {
    match invoke_typed_operation(context, operation, operation_args, permitted_domains)? {
        Some(output) => Ok(output),
        None => Err(anyhow!(
            "unsupported operation for typed world runtime: '{operation}'"
        )),
    }
}

fn invoke_component(
    component_path: &str,
    operation: &str,
    operation_args: &[String],
    permitted_domains: Vec<String>,
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
        permitted_domains: permitted_domains.clone(),
        timeout_ms,
        response_tx,
    };

    let send_start = Instant::now();
    if worker.sender.send(request).is_err() {
        invalidate_worker(&cache_key);

        let (retry_cache_key, retry_worker) = get_or_create_worker(component_path)?;
        let (retry_tx, retry_rx) = mpsc::sync_channel(1);
        let retry_request = WorkerRequest {
            operation: operation.to_string(),
            operation_args: operation_args.to_vec(),
            permitted_domains,
            timeout_ms,
            response_tx: retry_tx,
        };

        let retry_send_start = Instant::now();
        retry_worker
            .sender
            .send(retry_request)
            .map_err(|_| anyhow!("WASM worker channel send failed after retry"))?;
        let send_ms = retry_send_start.elapsed().as_millis();

        return recv_worker_response(
            &retry_cache_key,
            operation,
            timeout_ms,
            worker_acquire_ms,
            send_ms,
            total_start,
            retry_rx,
        );
    }

    let send_ms = send_start.elapsed().as_millis();

    recv_worker_response(
        &cache_key,
        operation,
        timeout_ms,
        worker_acquire_ms,
        send_ms,
        total_start,
        response_rx,
    )
}

fn recv_worker_response(
    cache_key: &str,
    operation: &str,
    timeout_ms: u32,
    worker_acquire_ms: u128,
    send_ms: u128,
    total_start: Instant,
    response_rx: mpsc::Receiver<Result<InvokeAttemptOutput>>,
) -> Result<String> {
    let wait_start = Instant::now();
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
    let wait_ms = wait_start.elapsed().as_millis();

    match response {
        Ok(output) => {
            set_last_timing(format!(
                "[TEMP_TIMING_REMOVE] wasmInvoke op={operation} workerAcquireMs={worker_acquire_ms} sendMs={send_ms} waitMs={wait_ms} totalMs={} detail={}",
                total_start.elapsed().as_millis(),
                output.timing
            ));

            Ok(output.json)
        }
        Err(err) => {
            set_last_timing(format!(
                "[TEMP_TIMING_REMOVE] wasmInvoke op={operation} failed workerAcquireMs={worker_acquire_ms} sendMs={send_ms} waitMs={wait_ms} totalMs={} err={:#}",
                total_start.elapsed().as_millis(),
                err
            ));
            Err(err)
        }
    }
}

impl typed_bindings::emma::plugin::host_bridge::Host for RunnerState {
    async fn operation_payload(
        &mut self,
        operation: String,
        args_json: Option<String>,
    ) -> Option<String> {
        let _ = operation;

        let raw = args_json?.trim().to_string();
        if raw.is_empty() {
            return None;
        }

        if raw.starts_with("https://") || raw.starts_with("http://") {
            return fetch_host_payload(self, &raw);
        }

        let args = parse_args_json_value(Some(raw.as_str()));
        let url = get_json_arg_string(args.as_ref(), "url")?;
        fetch_host_payload(self, &url)
    }

    async fn search_payload(&mut self, query: String) -> Option<String> {
        let _ = query;
        None
    }

    async fn chapters_payload(&mut self, media_id: String) -> Option<String> {
        let _ = media_id;
        None
    }

    async fn page_payload(
        &mut self,
        media_id: String,
        chapter_id: String,
        page_index: u32,
    ) -> Option<String> {
        let _ = media_id;
        let _ = chapter_id;
        let _ = page_index;
        None
    }

    async fn pages_payload(
        &mut self,
        media_id: String,
        chapter_id: String,
        start_index: u32,
        count: u32,
    ) -> Option<String> {
        let _ = media_id;
        let _ = chapter_id;
        let _ = start_index;
        let _ = count;
        None
    }
}

fn parse_args_json_value(args_json: Option<&str>) -> Option<Value> {
    let value = args_json?.trim();
    if value.is_empty() {
        return None;
    }

    serde_json::from_str::<Value>(value).ok()
}

fn get_json_arg_string(args: Option<&Value>, key: &str) -> Option<String> {
    let value = args?.as_object()?.get(key)?;
    match value {
        Value::String(text) if !text.trim().is_empty() => Some(text.trim().to_string()),
        Value::Number(number) => Some(number.to_string()),
        _ => None,
    }
}

fn is_host_allowed(permitted_domains: &[String], host: &str) -> bool {
    let host_lower = host.to_ascii_lowercase();
    permitted_domains.iter().any(|domain| {
        let trimmed = domain.trim();
        !trimmed.is_empty()
            && (host_lower == trimmed.to_ascii_lowercase()
                || host_lower.ends_with(&format!(".{}", trimmed.to_ascii_lowercase())))
    })
}

fn get_host_http_client() -> Option<&'static reqwest::blocking::Client> {
    if let Some(client) = HOST_HTTP_CLIENT.get() {
        return Some(client);
    }

    let builder = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(15))
        .user_agent("EMMA-WasmRuntime/1.0");
    let client = builder.build().ok()?;
    let _ = HOST_HTTP_CLIENT.set(client);
    HOST_HTTP_CLIENT.get()
}

fn fetch_host_payload(state: &RunnerState, absolute_url: &str) -> Option<String> {
    let parsed = reqwest::Url::parse(absolute_url).ok()?;
    let host = parsed.host_str()?;
    if !is_host_allowed(&state.permitted_domains, host) {
        return None;
    }

    let client = get_host_http_client()?;
    let response = client.get(parsed).send().ok()?;
    if !response.status().is_success() {
        return None;
    }

    response.text().ok().filter(|value| !value.trim().is_empty())
}

fn invoke_typed_operation(
    context: &mut WorkerContext,
    operation: &str,
    operation_args: &[String],
    permitted_domains: &[String],
) -> Result<Option<InvokeAttemptOutput>> {
    let typed_pre = &context.typed_pre;

    if operation != "handshake" && operation != "capabilities" && operation != "invoke" {
        return Ok(None);
    }

    let op_start = Instant::now();
    let state = RunnerState {
        wasi_ctx: WasiCtxBuilder::new().build(),
        http_ctx: WasiHttpCtx::new(),
        table: ResourceTable::new(),
        permitted_domains: permitted_domains.to_vec(),
    };

    let mut store = Store::new(context.engine, state);
    store.set_epoch_deadline(1);

    let instance = context
        .runtime
        .block_on(async { typed_pre.instantiate_async(&mut store).await })
        .context("failed to instantiate typed world")?;

    let guest = instance.emma_plugin_plugin();

    let json = match operation {
        "handshake" => {
            let response = context
                .runtime
                .block_on(async { guest.call_handshake(&mut store).await })
                .context("typed handshake failed")?;
            serde_json::to_string(&serde_json::json!({
                "version": response.version,
                "message": response.message,
            }))
            .context("failed to serialize typed handshake response")?
        }
        "capabilities" => {
            let capabilities = context
                .runtime
                .block_on(async { guest.call_capabilities(&mut store).await })
                .context("typed capabilities failed")?;
            let value = capabilities
                .into_iter()
                .map(|item| {
                    serde_json::json!({
                        "name": item.name,
                        "mediaTypes": item.media_types,
                        "operations": item.operations,
                    })
                })
                .collect::<Vec<_>>();
            serde_json::to_string(&value).context("failed to serialize typed capabilities")?
        }
        "invoke" => {
            let nested_operation = operation_args.get(0).cloned().unwrap_or_default();
            let media_id = operation_args
                .get(1)
                .filter(|value| !value.trim().is_empty())
                .cloned();
            let media_type = operation_args
                .get(2)
                .filter(|value| !value.trim().is_empty())
                .cloned();
            let args_json = operation_args
                .get(3)
                .filter(|value| !value.trim().is_empty())
                .cloned();

            let request = typed_bindings::exports::emma::plugin::plugin::MediaOperationRequest {
                operation: nested_operation,
                media_id,
                media_type,
                args_json,
                payload_json: None,
            };

            let response = context
                .runtime
                .block_on(async { guest.call_invoke(&mut store, &request).await })
                .context("typed invoke call failed")?;

            let operation_result = match response {
                Ok(ok) => serde_json::json!({
                    "isError": false,
                    "error": null,
                    "contentType": ok.content_type,
                    "payloadJson": ok.payload_json,
                }),
                Err(typed_bindings::exports::emma::plugin::plugin::OperationError::UnsupportedOperation(message)) => serde_json::json!({
                    "isError": true,
                    "error": format!("unsupported-operation:{}", message),
                    "contentType": "application/problem+json",
                    "payloadJson": "",
                }),
                Err(typed_bindings::exports::emma::plugin::plugin::OperationError::InvalidArguments(message)) => serde_json::json!({
                    "isError": true,
                    "error": format!("invalid-arguments:{}", message),
                    "contentType": "application/problem+json",
                    "payloadJson": "",
                }),
                Err(typed_bindings::exports::emma::plugin::plugin::OperationError::Failed(message)) => serde_json::json!({
                    "isError": true,
                    "error": format!("failed:{}", message),
                    "contentType": "application/problem+json",
                    "payloadJson": "",
                }),
            };

            serde_json::to_string(&operation_result)
                .context("failed to serialize typed invoke operation result")?
        }
        _ => return Ok(None),
    };

    Ok(Some(InvokeAttemptOutput {
        json,
        timing: format!(
            "typedWorld=1 op={} opMs={}",
            operation,
            op_start.elapsed().as_millis()
        ),
    }))
}

#[derive(Deserialize)]
struct InvokeArgsEnvelope {
    #[serde(default)]
    args: Vec<String>,
    #[serde(default, rename = "permittedDomains")]
    permitted_domains: Option<Vec<String>>,
}

fn parse_args_json(operation_args_json: &str) -> Result<(Vec<String>, Vec<String>)> {
    if operation_args_json.trim().is_empty() {
        return Ok((vec![], vec![]));
    }

    if operation_args_json.trim_start().starts_with('[') {
        let args = serde_json::from_str::<Vec<String>>(operation_args_json)
            .context("failed to parse operation args JSON")?;
        return Ok((args, vec![]));
    }

    let envelope = serde_json::from_str::<InvokeArgsEnvelope>(operation_args_json)
        .context("failed to parse operation args envelope")?;
    let permitted_domains = envelope.permitted_domains.unwrap_or_default();

    Ok((envelope.args, permitted_domains))
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

        let (operation_args, permitted_domains) = parse_args_json(&operation_args_json)?;
        invoke_component(
            &component_path,
            &operation,
            &operation_args,
            permitted_domains,
            timeout_ms,
        )
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

        let (operation_args, permitted_domains) = parse_args_json(&operation_args_json)?;
        invoke_component(
            &component_path,
            &operation,
            &operation_args,
            permitted_domains,
            timeout_ms,
        )
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
        invalidate_component(&key);
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
