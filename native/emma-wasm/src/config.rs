//! What the .NET side tells the host when it opens a plugin.

use std::collections::HashMap;
use std::path::PathBuf;

use serde::Deserialize;

/// Resource ceilings, from the plugin manifest.
#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Limits {
    /// CPU budget per call in milliseconds. Time spent waiting inside host calls is not counted.
    pub cpu_budget_ms: u32,
    /// Memory ceiling in megabytes.
    pub memory_mb: u32,
    /// Wall-clock limit for a whole call, host waits included.
    #[serde(default = "default_call_timeout_ms")]
    pub call_timeout_ms: u32,
}

const fn default_call_timeout_ms() -> u32 {
    60_000
}

/// What the plugin may ask the host for, from the plugin manifest.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Permissions {
    /// Exact host names or `*.example.com` wildcards.
    #[serde(default)]
    pub network: Vec<String>,
    /// Whether `host.cache-get` and `host.cache-put` work.
    #[serde(default)]
    pub cache: bool,
}

/// Switches that exist only so tests can run hermetically. The production host never sets them.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Testing {
    /// Lets plugins reach loopback and private addresses (a local test server).
    #[serde(default)]
    pub allow_private_addresses: bool,
    /// Freezes `host.now-ms` at this value.
    #[serde(default)]
    pub fixed_now_ms: Option<i64>,
}

/// Everything needed to open one plugin.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PluginConfig {
    /// The component file, already verified by the .NET side.
    pub component_path: PathBuf,
    /// Whether the file is a precompiled `.cwasm` produced by [`crate::Runtime::precompile`] on this host.
    #[serde(default)]
    pub precompiled: bool,
    /// Resource ceilings.
    pub limits: Limits,
    /// What the plugin may ask for.
    #[serde(default)]
    pub permissions: Permissions,
    /// User settings, as text values by key (secrets already decrypted).
    #[serde(default)]
    pub settings: HashMap<String, Vec<String>>,
    /// Test-only switches.
    #[serde(default)]
    pub testing: Testing,
}

impl PluginConfig {
    /// Checks the numbers are within the ranges the manifest schema allows.
    ///
    /// # Errors
    /// A message naming the first value out of range.
    pub fn validate(&self) -> Result<(), String> {
        if !(1..=600_000).contains(&self.limits.cpu_budget_ms) {
            return Err("limits.cpuBudgetMs must be from 1 to 600000".to_owned());
        }
        if !(1..=4096).contains(&self.limits.memory_mb) {
            return Err("limits.memoryMb must be from 1 to 4096".to_owned());
        }
        if !(100..=600_000).contains(&self.limits.call_timeout_ms) {
            return Err("limits.callTimeoutMs must be from 100 to 600000".to_owned());
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(json: &str) -> Result<PluginConfig, serde_json::Error> {
        serde_json::from_str(json)
    }

    #[test]
    fn a_minimal_config_gets_defaults() {
        let config =
            parse(r#"{"componentPath":"p.wasm","limits":{"cpuBudgetMs":100,"memoryMb":64}}"#)
                .unwrap();

        assert!(config.validate().is_ok());
        assert_eq!(config.limits.call_timeout_ms, 60_000);
        assert!(config.permissions.network.is_empty());
        assert!(!config.permissions.cache);
        assert!(!config.testing.allow_private_addresses);
    }

    #[test]
    fn unknown_fields_are_refused() {
        assert!(parse(r#"{"componentPath":"p.wasm","limits":{"cpuBudgetMs":1,"memoryMb":1},"filesystem":["/"]}"#).is_err());
        assert!(
            parse(
                r#"{"componentPath":"p.wasm","limits":{"cpuBudgetMs":1,"memoryMb":1,"threads":8}}"#
            )
            .is_err()
        );
    }

    #[test]
    fn out_of_range_limits_are_refused() {
        for (cpu, memory) in [(0, 64), (600_001, 64), (100, 0), (100, 5000)] {
            let json = format!(
                r#"{{"componentPath":"p","limits":{{"cpuBudgetMs":{cpu},"memoryMb":{memory}}}}}"#
            );
            assert!(parse(&json).unwrap().validate().is_err(), "{cpu} {memory}");
        }
    }
}
