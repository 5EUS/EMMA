//! The EMMA Wasm plugin host: runs `emma:core` components under wasmtime.
#![allow(clippy::module_name_repetitions)]

pub mod cache;
pub mod config;
pub mod ffi;
mod generated;
pub mod host;
pub mod network;
pub mod runtime;
pub mod wire;

pub use config::PluginConfig;
pub use runtime::{CallOutput, Plugin, Runtime, RuntimeOptions};
