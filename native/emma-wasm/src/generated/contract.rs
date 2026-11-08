//! The `plugin` world of the `emma:core` contract. The serde derives let the host exchange the
//! contract's records with the .NET side as JSON without a hand-written mapping.
wasmtime::component::bindgen!({
    path: "../../src/EMMA.Contracts/wit",
    world: "plugin",
    additional_derives: [serde::Serialize, serde::Deserialize],
});
