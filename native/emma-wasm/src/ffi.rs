//! The C ABI the .NET side calls. Everything crosses as pointer plus length (no NUL-terminated
//! strings) and results are owned buffers the caller frees with [`emma_buffer_free`]. A panic
//! never crosses the boundary: it becomes an error code and message.
//!
//! The .NET side is responsible for not closing a plugin while a call on it is in flight (a
//! reference-counted safe handle does this). A runtime may be destroyed before its plugins.

use std::panic::{AssertUnwindSafe, catch_unwind};

use crate::config::PluginConfig;
use crate::runtime::{Plugin, Runtime, RuntimeOptions};

/// The call succeeded.
pub const EMMA_OK: i32 = 0;
/// An argument was null or not valid UTF-8 / JSON.
pub const EMMA_ERR_ARGUMENT: i32 = 1;
/// The runtime refused the request (bad component, bad configuration, ...); see the error buffer.
pub const EMMA_ERR_RUNTIME: i32 = 2;
/// The host panicked; see the error buffer. The handle should be discarded.
pub const EMMA_ERR_PANIC: i32 = 3;

/// The runtime options as the .NET side sends them.
#[derive(serde::Deserialize, Default)]
#[serde(deny_unknown_fields)]
struct Options {
    target: Option<String>,
}

/// An owned byte buffer handed across the boundary. Free it with [`emma_buffer_free`].
#[repr(C)]
#[derive(Debug)]
pub struct EmmaBuffer {
    /// The first byte, or null when empty.
    pub ptr: *mut u8,
    /// The number of bytes.
    pub len: usize,
    /// The allocation size, needed to free it.
    pub cap: usize,
}

impl EmmaBuffer {
    const EMPTY: Self = Self {
        ptr: std::ptr::null_mut(),
        len: 0,
        cap: 0,
    };

    fn from_vec(bytes: Vec<u8>) -> Self {
        if bytes.is_empty() {
            return Self::EMPTY;
        }
        let mut bytes = std::mem::ManuallyDrop::new(bytes);
        Self {
            ptr: bytes.as_mut_ptr(),
            len: bytes.len(),
            cap: bytes.capacity(),
        }
    }
}

/// Frees a buffer returned by this library. Passing an empty buffer is fine.
///
/// # Safety
/// `buffer` must have been returned by this library and not freed before.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_buffer_free(buffer: EmmaBuffer) {
    if !buffer.ptr.is_null() {
        // SAFETY: the caller promises the buffer came from `EmmaBuffer::from_vec`, which leaked a `Vec<u8>`
        // with exactly this pointer, length and capacity.
        drop(unsafe { Vec::from_raw_parts(buffer.ptr, buffer.len, buffer.cap) });
    }
}

/// Reads a byte slice from a pointer and length. A null pointer is only valid with length zero.
unsafe fn slice<'a>(ptr: *const u8, len: usize) -> Option<&'a [u8]> {
    if len == 0 {
        return Some(&[]);
    }
    if ptr.is_null() {
        return None;
    }
    // SAFETY: the caller guarantees `ptr` points to `len` readable bytes for the duration of the call.
    Some(unsafe { std::slice::from_raw_parts(ptr, len) })
}

fn write_error(out_error: *mut EmmaBuffer, message: &str) {
    if !out_error.is_null() {
        // SAFETY: a non-null `out_error` points to writable memory for one `EmmaBuffer`, per the contract.
        unsafe { out_error.write(EmmaBuffer::from_vec(message.as_bytes().to_vec())) };
    }
}

/// Runs a body, turning a panic into [`EMMA_ERR_PANIC`] with its message.
fn guarded(out_error: *mut EmmaBuffer, body: impl FnOnce() -> Result<(), (i32, String)>) -> i32 {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(())) => EMMA_OK,
        Ok(Err((code, message))) => {
            write_error(out_error, &message);
            code
        }
        Err(panic) => {
            let message = panic
                .downcast_ref::<&str>()
                .map(|s| (*s).to_owned())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "the host panicked".to_owned());
            write_error(out_error, &format!("host panic: {message}"));
            EMMA_ERR_PANIC
        }
    }
}

/// Creates a runtime. `options_json` may be empty for the defaults, or `{"target":"pulley64"}`.
///
/// # Safety
/// `options` must point to `options_len` readable bytes (or be null with length zero); `out_runtime`
/// must be writable; `out_error`, if non-null, must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_runtime_create(
    options: *const u8,
    options_len: usize,
    out_runtime: *mut *mut Runtime,
    out_error: *mut EmmaBuffer,
) -> i32 {
    guarded(out_error, || {
        if out_runtime.is_null() {
            return Err((EMMA_ERR_ARGUMENT, "out_runtime is null".to_owned()));
        }
        // SAFETY: forwarded from this function's contract.
        let bytes = unsafe { slice(options, options_len) }
            .ok_or((EMMA_ERR_ARGUMENT, "options is null".to_owned()))?;
        let parsed: Options = if bytes.is_empty() {
            Options::default()
        } else {
            serde_json::from_slice(bytes)
                .map_err(|e| (EMMA_ERR_ARGUMENT, format!("options are not valid: {e}")))?
        };
        let runtime = Runtime::new(&RuntimeOptions {
            target: parsed.target,
        })
        .map_err(|e| (EMMA_ERR_RUNTIME, format!("{e:#}")))?;
        // SAFETY: `out_runtime` was checked non-null and is writable per the contract.
        unsafe { out_runtime.write(Box::into_raw(Box::new(runtime))) };
        Ok(())
    })
}

/// Destroys a runtime. Plugins opened from it keep working until closed.
///
/// # Safety
/// `runtime` must come from [`emma_runtime_create`] and not have been destroyed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_runtime_destroy(runtime: *mut Runtime) {
    if !runtime.is_null() {
        // SAFETY: the pointer came from `Box::into_raw` in `emma_runtime_create`.
        drop(unsafe { Box::from_raw(runtime) });
    }
}

/// Gets the key that identifies which precompiled output this runtime can load.
///
/// # Safety
/// `runtime` must be a live runtime; `out_key` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_runtime_key(
    runtime: *const Runtime,
    out_key: *mut EmmaBuffer,
) -> i32 {
    guarded(std::ptr::null_mut(), || {
        if runtime.is_null() || out_key.is_null() {
            return Err((EMMA_ERR_ARGUMENT, "null argument".to_owned()));
        }
        // SAFETY: both pointers were checked non-null; `runtime` is live per the contract.
        unsafe {
            out_key.write(EmmaBuffer::from_vec(
                (*runtime).compatibility_key().into_bytes(),
            ));
        };
        Ok(())
    })
}

/// Compiles a component ahead of time.
///
/// # Safety
/// `runtime` must be live; `wasm` must point to `wasm_len` readable bytes; `out_cwasm` must be writable;
/// `out_error`, if non-null, must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_precompile(
    runtime: *const Runtime,
    wasm: *const u8,
    wasm_len: usize,
    out_cwasm: *mut EmmaBuffer,
    out_error: *mut EmmaBuffer,
) -> i32 {
    guarded(out_error, || {
        if runtime.is_null() || out_cwasm.is_null() {
            return Err((EMMA_ERR_ARGUMENT, "null argument".to_owned()));
        }
        // SAFETY: forwarded from this function's contract.
        let bytes = unsafe { slice(wasm, wasm_len) }
            .ok_or((EMMA_ERR_ARGUMENT, "wasm is null".to_owned()))?;
        // SAFETY: `runtime` was checked non-null and is live per the contract.
        let compiled = unsafe { &*runtime }
            .precompile(bytes)
            .map_err(|e| (EMMA_ERR_RUNTIME, format!("{e:#}")))?;
        // SAFETY: `out_cwasm` was checked non-null and is writable per the contract.
        unsafe { out_cwasm.write(EmmaBuffer::from_vec(compiled)) };
        Ok(())
    })
}

/// Opens a plugin from a JSON configuration (see `PluginConfig`).
///
/// On success `out_info` receives `{"searchAssist":bool}`.
///
/// # Safety
/// `runtime` must be live; `config` must point to `config_len` readable bytes; `out_plugin` and `out_info`
/// must be writable; `out_error`, if non-null, must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_plugin_open(
    runtime: *const Runtime,
    config: *const u8,
    config_len: usize,
    out_plugin: *mut *mut Plugin,
    out_info: *mut EmmaBuffer,
    out_error: *mut EmmaBuffer,
) -> i32 {
    guarded(out_error, || {
        if runtime.is_null() || out_plugin.is_null() || out_info.is_null() {
            return Err((EMMA_ERR_ARGUMENT, "null argument".to_owned()));
        }
        // SAFETY: forwarded from this function's contract.
        let bytes = unsafe { slice(config, config_len) }
            .ok_or((EMMA_ERR_ARGUMENT, "config is null".to_owned()))?;
        let config: PluginConfig = serde_json::from_slice(bytes).map_err(|e| {
            (
                EMMA_ERR_ARGUMENT,
                format!("the plugin configuration is not valid: {e}"),
            )
        })?;
        config
            .validate()
            .map_err(|message| (EMMA_ERR_ARGUMENT, message))?;
        // SAFETY: `runtime` was checked non-null and is live per the contract.
        let plugin = unsafe { &*runtime }
            .open(&config)
            .map_err(|e| (EMMA_ERR_RUNTIME, format!("{e:#}")))?;
        let info = serde_json::json!({ "searchAssist": plugin.has_search_assist() })
            .to_string()
            .into_bytes();
        // SAFETY: both out pointers were checked non-null and are writable per the contract.
        unsafe {
            out_info.write(EmmaBuffer::from_vec(info));
            out_plugin.write(Box::into_raw(Box::new(plugin)));
        }
        Ok(())
    })
}

/// Closes a plugin.
///
/// # Safety
/// `plugin` must come from [`emma_plugin_open`], not have been closed, and have no call in flight.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_plugin_close(plugin: *mut Plugin) {
    if !plugin.is_null() {
        // SAFETY: the pointer came from `Box::into_raw` in `emma_plugin_open`.
        drop(unsafe { Box::from_raw(plugin) });
    }
}

/// Runs one operation. `out_json` always receives the envelope on success of the *call machinery*
/// (including plugin errors and traps, which are inside the envelope); `out_blob` receives binary
/// output (the bytes of a `read`) or stays empty.
///
/// # Safety
/// `plugin` must be live; `operation` and `request` must point to readable bytes of the given lengths;
/// `out_json` and `out_blob` must be writable; `out_error`, if non-null, must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn emma_plugin_call(
    plugin: *const Plugin,
    operation: *const u8,
    operation_len: usize,
    request: *const u8,
    request_len: usize,
    out_json: *mut EmmaBuffer,
    out_blob: *mut EmmaBuffer,
    out_error: *mut EmmaBuffer,
) -> i32 {
    guarded(out_error, || {
        if plugin.is_null() || out_json.is_null() || out_blob.is_null() {
            return Err((EMMA_ERR_ARGUMENT, "null argument".to_owned()));
        }
        // SAFETY: forwarded from this function's contract.
        let (operation, request) =
            unsafe { (slice(operation, operation_len), slice(request, request_len)) };
        let (Some(operation), Some(request)) = (operation, request) else {
            return Err((EMMA_ERR_ARGUMENT, "operation or request is null".to_owned()));
        };
        let operation = std::str::from_utf8(operation)
            .map_err(|_| (EMMA_ERR_ARGUMENT, "operation is not UTF-8".to_owned()))?;
        // SAFETY: `plugin` was checked non-null and is live per the contract.
        let output = unsafe { &*plugin }.call(operation, request);
        // SAFETY: both out pointers were checked non-null and are writable per the contract.
        unsafe {
            out_json.write(EmmaBuffer::from_vec(output.json));
            out_blob.write(EmmaBuffer::from_vec(output.blob));
        }
        Ok(())
    })
}
