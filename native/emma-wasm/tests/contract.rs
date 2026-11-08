//! The WIT contract in `src/EMMA.Contracts/wit` is the interface every plugin is built against. The host and the
//! fixture plugins compile it as a side effect, but only a parse of the whole directory says, fast and by name, that
//! the package is intact and still has the shape the manifest schema and the docs promise.
#![allow(missing_docs)]

use std::path::PathBuf;

use wit_parser::{Resolve, WorldItem};

fn contract_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../src/EMMA.Contracts/wit")
}

fn parse() -> Resolve {
    let mut resolve = Resolve::default();
    resolve
        .push_dir(contract_dir())
        .expect("the contract parses and resolves");
    resolve
}

fn export_names(resolve: &Resolve, world: &str) -> Vec<String> {
    let id = resolve
        .select_world(
            &[resolve.packages.iter().next().expect("one package").0],
            Some(world),
        )
        .unwrap_or_else(|error| panic!("world {world} is missing: {error}"));
    let mut names: Vec<String> = resolve.worlds[id]
        .exports
        .iter()
        .filter_map(|(_, item)| match item {
            WorldItem::Interface { id, .. } => resolve.interfaces[*id].name.clone(),
            _ => None,
        })
        .collect();
    names.sort();
    names
}

#[test]
fn the_contract_is_one_package_at_version_one() {
    let resolve = parse();

    let packages: Vec<String> = resolve
        .packages
        .iter()
        .map(|(_, package)| package.name.to_string())
        .collect();

    assert_eq!(packages, ["emma:core@1.0.0"]);
}

#[test]
fn the_base_world_exports_exactly_what_a_manifest_requires() {
    // `contract.required` in a manifest names these interfaces (emma:core/meta@1 and so on); the host refuses a
    // plugin that does not export them, so a change here is a major version.
    assert_eq!(
        export_names(&parse(), "plugin"),
        ["assets", "catalog", "meta"]
    );
}

#[test]
fn the_search_assist_world_adds_one_export() {
    assert_eq!(
        export_names(&parse(), "plugin-search-assist"),
        ["assets", "catalog", "meta", "search-assist"]
    );
}
