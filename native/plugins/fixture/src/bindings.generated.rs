//! Bindings generated from `src/EMMA.Contracts/wit` by wit-bindgen at compile time. Generated code
//! is exempt from the doc and style lints that apply to the hand-written crate.
#![allow(
    missing_docs,
    clippy::all,
    clippy::pedantic,
    unused_qualifications,
    dead_code
)]

#[cfg(not(any(feature = "assist", feature = "newer-patch")))]
wit_bindgen::generate!({ path: "../../../src/EMMA.Contracts/wit", world: "plugin", generate_all });

#[cfg(all(feature = "assist", not(feature = "newer-patch")))]
wit_bindgen::generate!({ path: "../../../src/EMMA.Contracts/wit", world: "plugin-search-assist", generate_all });

#[cfg(feature = "newer-patch")]
wit_bindgen::generate!({ path: "target/wit-newer-patch", world: "plugin-search-assist", generate_all });
