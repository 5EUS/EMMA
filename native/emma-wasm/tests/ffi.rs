//! Tests that drive the C ABI exactly as the .NET side does.
#![allow(missing_docs, clippy::too_many_lines)]

mod common;

use std::time::{Duration, Instant};

use emma_wasm::ffi::{
    EMMA_ERR_ARGUMENT, EMMA_ERR_RUNTIME, EMMA_OK, EmmaBuffer, emma_wasm_buffer_free,
    emma_wasm_plugin_call, emma_wasm_plugin_close, emma_wasm_plugin_open, emma_wasm_precompile,
    emma_wasm_runtime_create, emma_wasm_runtime_destroy, emma_wasm_runtime_key,
};
use emma_wasm::{Plugin, Runtime};
use serde_json::{Value, json};

const EMPTY: EmmaBuffer = EmmaBuffer {
    ptr: std::ptr::null_mut(),
    len: 0,
    cap: 0,
};

fn take(buffer: EmmaBuffer) -> Vec<u8> {
    let bytes = if buffer.ptr.is_null() {
        Vec::new()
    } else {
        unsafe { std::slice::from_raw_parts(buffer.ptr, buffer.len) }.to_vec()
    };
    unsafe { emma_wasm_buffer_free(buffer) };
    bytes
}

fn text(buffer: EmmaBuffer) -> String {
    String::from_utf8(take(buffer)).unwrap()
}

fn create_runtime() -> *mut Runtime {
    let mut runtime = std::ptr::null_mut();
    let mut error = EMPTY;
    let code =
        unsafe { emma_wasm_runtime_create(std::ptr::null(), 0, &raw mut runtime, &raw mut error) };
    assert_eq!(code, EMMA_OK, "{}", text(error));
    runtime
}

fn open_plugin(runtime: *const Runtime, config: &Value) -> (*mut Plugin, Value) {
    let bytes = serde_json::to_vec(config).unwrap();
    let (mut plugin, mut info, mut error) = (std::ptr::null_mut(), EMPTY, EMPTY);
    let code = unsafe {
        emma_wasm_plugin_open(
            runtime,
            bytes.as_ptr(),
            bytes.len(),
            &raw mut plugin,
            &raw mut info,
            &raw mut error,
        )
    };
    assert_eq!(code, EMMA_OK, "{}", text(error));
    (plugin, serde_json::from_slice(&take(info)).unwrap())
}

fn call(plugin: *const Plugin, operation: &str, request: &Value) -> (Value, Vec<u8>) {
    let bytes = serde_json::to_vec(request).unwrap();
    let (mut json_out, mut blob, mut error) = (EMPTY, EMPTY, EMPTY);
    let code = unsafe {
        emma_wasm_plugin_call(
            plugin,
            operation.as_ptr(),
            operation.len(),
            bytes.as_ptr(),
            bytes.len(),
            &raw mut json_out,
            &raw mut blob,
            &raw mut error,
        )
    };
    assert_eq!(code, EMMA_OK, "{}", text(error));
    (serde_json::from_slice(&take(json_out)).unwrap(), take(blob))
}

#[test]
fn the_full_lifecycle_works_through_the_c_abi() {
    let runtime = create_runtime();
    let (plugin, info) = open_plugin(runtime, &common::config("fixture-assist"));

    let (envelope, _) = call(
        plugin,
        "search",
        &json!({ "text": "recipe", "kinds": [], "filters": [], "sort": null, "cursor": null, "limit": null }),
    );
    let (_, bytes) = call(
        plugin,
        "read",
        &json!({ "token": "tok-1", "range": { "start": 0, "length": 4 } }),
    );

    assert_eq!(info["searchAssist"], true);
    assert_eq!(envelope["ok"]["items"][0]["title"], "Lasagna");
    assert_eq!(bytes, [0, 1, 2, 3]);
    unsafe {
        emma_wasm_plugin_close(plugin);
        emma_wasm_runtime_destroy(runtime);
    }
}

#[test]
fn a_runaway_plugin_is_still_stopped_after_the_runtime_handle_is_destroyed() {
    let runtime = create_runtime();
    let mut limited = common::config("fixture");
    limited["limits"]["cpuBudgetMs"] = json!(200);
    let (plugin, _) = open_plugin(runtime, &limited);
    unsafe { emma_wasm_runtime_destroy(runtime) };

    let started = Instant::now();
    let (envelope, _) = call(
        plugin,
        "search",
        &json!({ "text": "spin", "kinds": [], "filters": [], "sort": null, "cursor": null, "limit": null }),
    );

    assert_eq!(envelope["trapped"], true);
    assert!(
        started.elapsed() < Duration::from_secs(3),
        "the epoch clock must outlive the runtime handle"
    );
    unsafe { emma_wasm_plugin_close(plugin) };
}

#[test]
fn bad_input_is_reported_as_an_error_code_with_a_message() {
    let runtime = create_runtime();
    let (mut plugin, mut info, mut error) = (std::ptr::null_mut(), EMPTY, EMPTY);

    let garbage = unsafe {
        emma_wasm_plugin_open(
            runtime,
            b"not json".as_ptr(),
            8,
            &raw mut plugin,
            &raw mut info,
            &raw mut error,
        )
    };
    let message = text(error);
    let unknown_field = json!({ "componentPath": "x", "limits": { "cpuBudgetMs": 1, "memoryMb": 1 }, "filesystem": ["/"] });
    let bytes = serde_json::to_vec(&unknown_field).unwrap();
    let mut error = EMPTY;
    let refused = unsafe {
        emma_wasm_plugin_open(
            runtime,
            bytes.as_ptr(),
            bytes.len(),
            &raw mut plugin,
            &raw mut info,
            &raw mut error,
        )
    };
    let refused_message = text(error);
    let null_runtime = unsafe {
        emma_wasm_plugin_open(
            std::ptr::null(),
            bytes.as_ptr(),
            bytes.len(),
            &raw mut plugin,
            &raw mut info,
            std::ptr::null_mut(),
        )
    };
    let missing = json!({ "componentPath": "/no/such/file.wasm", "limits": { "cpuBudgetMs": 1000, "memoryMb": 64 } });
    let bytes = serde_json::to_vec(&missing).unwrap();
    let mut error = EMPTY;
    let missing_code = unsafe {
        emma_wasm_plugin_open(
            runtime,
            bytes.as_ptr(),
            bytes.len(),
            &raw mut plugin,
            &raw mut info,
            &raw mut error,
        )
    };
    let missing_message = text(error);

    assert_eq!(garbage, EMMA_ERR_ARGUMENT);
    assert!(message.contains("not valid"), "{message}");
    assert_eq!(refused, EMMA_ERR_ARGUMENT);
    assert!(refused_message.contains("filesystem"), "{refused_message}");
    assert_eq!(null_runtime, EMMA_ERR_ARGUMENT);
    assert_eq!(missing_code, EMMA_ERR_RUNTIME);
    assert!(missing_message.contains("file.wasm"), "{missing_message}");
    assert!(plugin.is_null());
    unsafe { emma_wasm_runtime_destroy(runtime) };
}

#[test]
fn null_arguments_never_crash() {
    let (mut json_out, mut blob) = (EMPTY, EMPTY);

    let call_code = unsafe {
        emma_wasm_plugin_call(
            std::ptr::null(),
            b"x".as_ptr(),
            1,
            std::ptr::null(),
            0,
            &raw mut json_out,
            &raw mut blob,
            std::ptr::null_mut(),
        )
    };
    let create_code = unsafe {
        emma_wasm_runtime_create(
            std::ptr::null(),
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        )
    };
    let key_code = unsafe { emma_wasm_runtime_key(std::ptr::null(), std::ptr::null_mut()) };
    unsafe {
        emma_wasm_plugin_close(std::ptr::null_mut());
        emma_wasm_runtime_destroy(std::ptr::null_mut());
        emma_wasm_buffer_free(EMPTY);
    }

    assert_eq!(
        (call_code, create_code, key_code),
        (EMMA_ERR_ARGUMENT, EMMA_ERR_ARGUMENT, EMMA_ERR_ARGUMENT)
    );
}

#[test]
fn an_unknown_runtime_option_is_refused() {
    let (mut runtime, mut error) = (std::ptr::null_mut(), EMPTY);
    let options = br#"{"target":"x86_64-unknown-linux-gnu","jit":true}"#;

    let code = unsafe {
        emma_wasm_runtime_create(
            options.as_ptr(),
            options.len(),
            &raw mut runtime,
            &raw mut error,
        )
    };

    assert_eq!(code, EMMA_ERR_ARGUMENT);
    assert!(text(error).contains("jit"));
    assert!(runtime.is_null());
}

#[test]
fn precompile_and_the_key_work_through_the_c_abi() {
    let runtime = create_runtime();
    let wasm = std::fs::read(common::plugin_path("fixture")).unwrap();
    let (mut compiled, mut key, mut error) = (EMPTY, EMPTY, EMPTY);
    // Every call clears its outputs on entry, so the failing call needs its own buffer or it would wipe `compiled`.
    let mut rejected = EMPTY;

    let precompile = unsafe {
        emma_wasm_precompile(
            runtime,
            wasm.as_ptr(),
            wasm.len(),
            &raw mut compiled,
            &raw mut error,
        )
    };
    let key_code = unsafe { emma_wasm_runtime_key(runtime, &raw mut key) };
    let bad = unsafe {
        emma_wasm_precompile(
            runtime,
            b"junk".as_ptr(),
            4,
            &raw mut rejected,
            &raw mut error,
        )
    };

    assert_eq!(
        (precompile, key_code, bad),
        (EMMA_OK, EMMA_OK, EMMA_ERR_RUNTIME)
    );
    assert!(take(compiled).len() > 1000);
    assert!(take(rejected).is_empty());
    assert_eq!(text(key).len(), 16);
    assert!(!text(error).is_empty());
    unsafe { emma_wasm_runtime_destroy(runtime) };
}

#[test]
fn calls_from_many_threads_through_the_c_abi_do_not_interfere() {
    let runtime = create_runtime();
    let (plugin, _) = open_plugin(runtime, &common::config("fixture"));
    let address = plugin as usize;

    std::thread::scope(|scope| {
        for _ in 0..6 {
            scope.spawn(move || {
                let plugin = address as *const Plugin;
                for _ in 0..4 {
                    let (envelope, _) = call(plugin, "health", &json!({}));
                    assert_eq!(envelope["ok"]["ok"], true);
                }
            });
        }
    });

    unsafe {
        emma_wasm_plugin_close(plugin);
        emma_wasm_runtime_destroy(runtime);
    }
}

/// A caller that does not zero its output buffers (a stack slot the .NET side frees even on
/// success) must get empty ones back, never its own stale memory.
#[test]
fn output_buffers_are_empty_after_success() {
    let mut stale_byte = 0_u8;
    let stale_ptr = &raw mut stale_byte;
    let stale = || EmmaBuffer {
        ptr: stale_ptr,
        len: 7,
        cap: 7,
    };
    let (mut runtime, mut error) = (std::ptr::null_mut(), stale());

    let code =
        unsafe { emma_wasm_runtime_create(std::ptr::null(), 0, &raw mut runtime, &raw mut error) };

    assert_eq!(code, EMMA_OK);
    assert!(error.ptr.is_null() && error.len == 0 && error.cap == 0);
    let mut key = stale();
    assert_eq!(
        unsafe { emma_wasm_runtime_key(runtime, &raw mut key) },
        EMMA_OK
    );
    assert!(!text(key).is_empty());
    unsafe { emma_wasm_runtime_destroy(runtime) };
}
