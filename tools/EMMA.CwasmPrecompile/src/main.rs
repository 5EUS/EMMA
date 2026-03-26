use anyhow::{anyhow, Context, Result};
use std::env;
use std::fs;
use std::path::PathBuf;
use wasmtime::{Config, Engine, Strategy};

fn main() {
    if let Err(error) = run() {
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let mut args = env::args().skip(1);
    let input = args
        .next()
        .ok_or_else(|| anyhow!("usage: emma_cwasm_precompile <input.wasm> <output.cwasm> [target]"))?;
    let output = args
        .next()
        .ok_or_else(|| anyhow!("usage: emma_cwasm_precompile <input.wasm> <output.cwasm> [target]"))?;
    let target = args.next();

    let input_path = PathBuf::from(&input);
    let output_path = PathBuf::from(&output);

    if !input_path.is_file() {
        return Err(anyhow!("input component not found: {}", input_path.display()));
    }

    if let Some(parent) = output_path.parent() {
        fs::create_dir_all(parent)
            .with_context(|| format!("failed to create output directory: {}", parent.display()))?;
    }

    let mut config = Config::new();
    config.wasm_component_model(true);
    config.async_support(true);
    config.epoch_interruption(true);
    config.strategy(Strategy::Cranelift);

    if let Some(target) = target.as_deref() {
        config
            .target(target)
            .with_context(|| format!("invalid target: {target}"))?;
    }

    let engine = Engine::new(&config).context("failed to create wasmtime engine")?;

    let component_bytes = fs::read(&input_path)
        .with_context(|| format!("failed to read input component: {}", input_path.display()))?;

    let serialized = engine
        .precompile_component(&component_bytes)
        .with_context(|| format!("failed to precompile component: {}", input_path.display()))?;

    fs::write(&output_path, serialized)
        .with_context(|| format!("failed to write output artifact: {}", output_path.display()))?;

    Ok(())
}
