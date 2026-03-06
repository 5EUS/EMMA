use anyhow::{anyhow, Context, Result};
use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::path::{Path, PathBuf};
use wasmtime::component::{Component, Linker, ResourceTable};
use wasmtime::{Config, Engine, Store};
use wasmtime_wasi::{DirPerms, FilePerms};
use wasmtime_wasi::p2::bindings::sync::Command;
use wasmtime_wasi::p2::pipe::MemoryOutputPipe;
use wasmtime_wasi::p2::{IoView, WasiCtx, WasiCtxBuilder, WasiView};
use wasmtime_wasi_http::{WasiHttpCtx, WasiHttpView};

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

fn invoke_component(
    component_path: &str,
    operation: &str,
    operation_args: &[String],
    _timeout_ms: u32,
) -> Result<String> {
    let mut config = Config::new();
    config.wasm_component_model(true);
    let engine = Engine::new(&config).context("failed to create wasmtime engine")?;

    let component = Component::from_file(&engine, component_path).context("failed to load component")?;

    let mut linker = Linker::new(&engine);
    wasmtime_wasi::p2::add_to_linker_sync(&mut linker)
        .context("failed to add WASI preview2 interfaces")?;
    wasmtime_wasi_http::add_only_http_to_linker_sync(&mut linker)
        .context("failed to add wasi:http interfaces")?;

    let component_path_abs = std::fs::canonicalize(component_path)
        .unwrap_or_else(|_| Path::new(component_path).to_path_buf());
    let component_dir = component_path_abs
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));

    let managed_entries = resolve_managed_entry_candidates(component_dir);
    let mut arg_variants = Vec::new();

    if !managed_entries.is_empty() {
        for entry in &managed_entries {
            let entry_rooted = if entry.starts_with('/') {
                entry.clone()
            } else {
                format!("/{entry}")
            };

            let mut variant1 = vec!["dotnet.wasm".to_string(), entry_rooted.clone(), operation.to_string()];
            variant1.extend(operation_args.iter().cloned());
            arg_variants.push(variant1);

            let mut variant2 = vec!["dotnet.wasm".to_string(), entry.clone(), operation.to_string()];
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

    let mut failures = Vec::new();

    for wasi_args in arg_variants {
        match invoke_component_once(
            &engine,
            &component,
            &linker,
            component_path,
            &component_path_abs,
            component_dir,
            &wasi_args,
        ) {
            Ok(stdout) => return Ok(stdout),
            Err(err) => {
                failures.push(format!("args={:?}: {err:#}", wasi_args));
            }
        }
    }

    Err(anyhow!(
        "all WASM invocation argv strategies failed:\n{}",
        failures.join("\n")
    ))
}

fn invoke_component_once(
    engine: &Engine,
    component: &Component,
    linker: &Linker<RunnerState>,
    component_path_raw: &str,
    _component_path: &Path,
    component_dir: &Path,
    wasi_args: &[String],
) -> Result<String> {
    let stdout_pipe = MemoryOutputPipe::new(2 * 1024 * 1024);
    let stderr_pipe = MemoryOutputPipe::new(2 * 1024 * 1024);

    let mut builder = WasiCtxBuilder::new();
    builder
        .preopened_dir(component_dir, "/", DirPerms::all(), FilePerms::all())
        .context("failed to preopen component directory")?;
    
    // Mount a writable temp directory for hostbridge files
    // This is needed on macOS where the app bundle is read-only
    let bridge_dir = get_bridge_dir(component_path_raw)?;
    if let Err(e) = std::fs::create_dir_all(&bridge_dir) {
        eprintln!("Warning: failed to create bridge directory: {}", e);
    } else {
        // Only mount if we successfully created the directory
        if let Err(e) = builder.preopened_dir(&bridge_dir, "/.hostbridge", DirPerms::all(), FilePerms::all()) {
            eprintln!("Warning: failed to mount bridge directory: {}", e);
        }
    }
    
    builder.env("PWD", "/");
    builder.env("MONO_PATH", "/");
    builder.args(wasi_args);
    builder.inherit_env();
    builder.stdout(stdout_pipe.clone());
    builder.stderr(stderr_pipe.clone());

    let state = RunnerState {
        wasi_ctx: builder.build(),
        http_ctx: WasiHttpCtx::new(),
        table: ResourceTable::new(),
    };

    let mut store = Store::new(engine, state);
    let command = Command::instantiate(&mut store, component, linker)
        .context("failed to instantiate command component")?;

    let run_result = command
        .wasi_cli_run()
        .call_run(&mut store);

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
        return Ok(json_payload);
    }

    let mut parts = vec!["WASM component output did not include JSON payload.".to_string()];
    parts.push(format!("stdout: {stdout}"));
    if !stderr.is_empty() {
        parts.push(format!("stderr: {stderr}"));
    }

    Err(anyhow!(parts.join("\n")))
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
    let mut candidates = vec![stem.clone()];
    let dll_name = format!("{stem}.dll");
    let dll_path: PathBuf = component_dir.join(&dll_name);
    if dll_path.is_file() {
        candidates.push(dll_name);
    }

    candidates.sort();
    candidates.dedup();
    candidates
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

    let result = std::panic::catch_unwind(|| {
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
pub extern "C" fn emma_wasm_runtime_free_string(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }

    unsafe {
        let _ = CString::from_raw(ptr);
    }
}
