//! Tests that load the fixture component and drive it through the host.
#![allow(missing_docs)]

mod common;

use std::time::{Duration, Instant};

use common::{Reply, TestServer, call, config, open, runtime, search};
use serde_json::{Value, json};

fn ok(envelope: &Value) -> &Value {
    assert!(
        envelope.get("error").is_none(),
        "unexpected error: {envelope}"
    );
    &envelope["ok"]
}

fn error_code(envelope: &Value) -> &str {
    envelope["error"]["code"]
        .as_str()
        .unwrap_or_else(|| panic!("expected an error: {envelope}"))
}

fn titles(page: &Value) -> Vec<&str> {
    page["items"]
        .as_array()
        .unwrap()
        .iter()
        .map(|n| n["title"].as_str().unwrap())
        .collect()
}

#[test]
fn describe_reports_kinds_filters_and_typed_settings() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let (envelope, _) = call(&plugin, "describe", &json!({}));
    let info = ok(&envelope);

    let kinds: Vec<&str> = info["kinds"]
        .as_array()
        .unwrap()
        .iter()
        .map(|k| k["kind"].as_str().unwrap())
        .collect();
    assert!(kinds.contains(&"series") && kinds.contains(&"recipe") && kinds.contains(&"probe"));
    assert_eq!(info["categories"][0]["id"], "connection");
    let secret = info["settings"]
        .as_array()
        .unwrap()
        .iter()
        .find(|s| s["key"] == "connection.token")
        .unwrap();
    assert_eq!(secret["setting_type"], "Secret");
    assert_eq!(secret["required"], true);
    let timeout = info["settings"]
        .as_array()
        .unwrap()
        .iter()
        .find(|s| s["key"] == "advanced.timeout")
        .unwrap();
    assert_eq!(timeout["visible_when"]["key"], "advanced.verbose");
    assert_eq!(envelope["trapped"], false);
}

#[test]
fn health_reports_ok() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let (envelope, _) = call(&plugin, "health", &json!({}));

    assert_eq!(ok(&envelope)["ok"], true);
}

#[test]
fn search_pages_through_results_by_cursor() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));
    let first = call(&plugin, "search", &json!({ "text": "series", "kinds": [], "filters": [], "sort": null, "cursor": null, "limit": 2 })).0;
    let page = ok(&first);
    assert_eq!(titles(page), ["Series 1", "Series 2"]);
    assert_eq!(page["next_cursor"], "2");
    assert_eq!(page["total"], 3);

    let second = call(&plugin, "search", &json!({ "text": "series", "kinds": [], "filters": [], "sort": null, "cursor": "2", "limit": 2 })).0;

    assert_eq!(titles(ok(&second)), ["Series 3"]);
    assert!(ok(&second)["next_cursor"].is_null());
}

#[test]
fn the_same_contract_serves_a_recipe_as_easily_as_a_series() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let recipes = search(&plugin, "recipe");
    let steps = call(
        &plugin,
        "children",
        &json!({ "parent": "r1", "request": { "cursor": null, "limit": null } }),
    )
    .0;

    assert_eq!(titles(ok(&recipes)), ["Lasagna", "Pancakes"]);
    assert_eq!(ok(&recipes)["items"][0]["kind"], "recipe");
    assert_eq!(titles(ok(&steps)), ["Make the sauce", "Layer it"]);
}

#[test]
fn children_walk_down_the_tree_and_unknown_parents_are_not_found() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));
    let children = |parent: Value, limit: Value| {
        call(
            &plugin,
            "children",
            &json!({ "parent": parent, "request": { "cursor": null, "limit": limit } }),
        )
        .0
    };

    let chapters = children(json!("s1"), json!(3));
    let pages = children(json!("s1-c2"), Value::Null);
    let missing = children(json!("nope"), Value::Null);

    assert_eq!(
        titles(ok(&chapters)),
        ["Chapter 1", "Chapter 2", "Chapter 3"]
    );
    assert_eq!(ok(&chapters)["total"], 120);
    assert_eq!(ok(&pages)["items"][0]["parent"], "s1-c2");
    assert_eq!(error_code(&missing), "NotFound");
}

#[test]
fn get_returns_known_nodes_in_request_order_and_omits_unknown() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let (envelope, _) = call(&plugin, "get", &json!({ "ids": ["s2", "unknown", "r1"] }));

    assert_eq!(
        titles(&json!({ "items": ok(&envelope) })),
        ["Series 2", "Lasagna"]
    );
}

#[test]
fn resolve_returns_each_delivery_kind() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));
    let resolve = |node: &str, role: &str| {
        call(
            &plugin,
            "resolve",
            &json!({ "node": node, "role": role, "hints": [] }),
        )
        .0
    };

    let remote = resolve("s1-c1-p1", "image");
    let inline = resolve("inline1", "text");
    let proxied = resolve("proxied1", "image");
    let unsupported = resolve("s1", "image");

    let asset = &ok(&remote)[0];
    assert_eq!(asset["mime"], "image/png");
    assert_eq!(
        asset["delivery"]["remote"]["url"],
        "https://files.example.com/s1-c1-p1.png"
    );
    assert_eq!(asset["delivery"]["remote"]["headers"][0]["name"], "referer");
    assert_eq!(ok(&inline)[0]["delivery"]["inline"]["base64"], "aGVsbG8=");
    assert_eq!(ok(&proxied)[0]["delivery"]["proxied"]["token"], "tok-1");
    assert_eq!(error_code(&unsupported), "Unsupported");
}

#[test]
fn read_returns_bytes_as_a_binary_buffer_and_honours_ranges() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let (whole, bytes) = call(&plugin, "read", &json!({ "token": "tok-1", "range": null }));
    let (part, slice) = call(
        &plugin,
        "read",
        &json!({ "token": "tok-1", "range": { "start": 10, "length": 5 } }),
    );
    let (missing, none) = call(&plugin, "read", &json!({ "token": "nope", "range": null }));

    assert_eq!(ok(&whole)["total"], 1000);
    assert_eq!(bytes.len(), 1000);
    assert_eq!(bytes[250], 250);
    assert_eq!(slice, [10, 11, 12, 13, 14]);
    assert_eq!(ok(&part)["total"], 1000);
    assert_eq!(error_code(&missing), "NotFound");
    assert!(none.is_empty());
}

#[test]
fn a_large_read_does_not_inflate_into_json() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let output = plugin.call("read", br#"{"token":"big","range":null}"#);

    assert_eq!(output.blob.len(), 5 * 1024 * 1024);
    assert!(
        output.json.len() < 200,
        "the envelope must not carry the bytes"
    );
}

#[test]
fn every_error_class_and_the_retry_hint_pass_through() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    for (name, code) in [
        ("not-found", "NotFound"),
        ("unauthorized", "Unauthorized"),
        ("unavailable", "Unavailable"),
        ("invalid-argument", "InvalidArgument"),
        ("unsupported", "Unsupported"),
        ("other", "Internal"),
    ] {
        let envelope = search(&plugin, &format!("error:{name}"));
        assert_eq!(error_code(&envelope), code);
        assert_eq!(envelope["trapped"], false, "a returned error is not a trap");
    }
    let limited = search(&plugin, "error:rate-limited");
    assert_eq!(error_code(&limited), "RateLimited");
    assert_eq!(limited["error"]["retry_after_ms"], 1500);
}

#[test]
fn a_crashing_plugin_is_reported_with_its_message_and_does_not_poison_later_calls() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let crashed = search(&plugin, "trap");
    let after = search(&plugin, "series");

    assert_eq!(crashed["trapped"], true);
    assert_eq!(error_code(&crashed), "Internal");
    assert!(
        crashed["error"]["message"]
            .as_str()
            .unwrap()
            .contains("crashed")
    );
    let logs = serde_json::to_string(&crashed["logs"]).unwrap();
    assert!(
        logs.contains("fixture panic on purpose"),
        "the panic message should reach the logs: {logs}"
    );
    assert_eq!(titles(ok(&after)).len(), 3);
}

#[test]
fn runaway_cpu_is_stopped_by_the_budget() {
    let runtime = runtime();
    let mut limited = config("fixture");
    limited["limits"]["cpuBudgetMs"] = json!(200);
    let plugin = open(&runtime, &limited);

    let started = Instant::now();
    let spun = search(&plugin, "spin");

    assert!(
        started.elapsed() < Duration::from_secs(3),
        "the loop must be stopped promptly"
    );
    assert_eq!(spun["trapped"], true);
    assert!(
        spun["error"]["message"].as_str().unwrap().contains("CPU"),
        "{spun}"
    );
    assert_eq!(
        titles(ok(&search(&plugin, "series"))).len(),
        3,
        "the plugin works again afterwards"
    );
}

#[test]
fn memory_use_is_capped() {
    let runtime = runtime();
    let mut limited = config("fixture");
    limited["limits"]["memoryMb"] = json!(64);
    let plugin = open(&runtime, &limited);

    let greedy = search(&plugin, "alloc");

    assert_eq!(greedy["trapped"], true, "{greedy}");
    assert_eq!(error_code(&greedy), "Internal");
    assert_eq!(titles(ok(&search(&plugin, "series"))).len(), 3);
}

#[test]
fn log_entries_arrive_with_their_levels() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let envelope = search(&plugin, "log");

    let logs: Vec<(&str, &str)> = envelope["logs"]
        .as_array()
        .unwrap()
        .iter()
        .map(|l| (l["level"].as_str().unwrap(), l["message"].as_str().unwrap()))
        .collect();
    assert_eq!(
        logs,
        [
            ("debug", "debug line"),
            ("info", "info line"),
            ("error", "error line")
        ]
    );
}

#[test]
fn the_cache_works_only_when_the_manifest_grants_it() {
    let runtime = runtime();
    let mut granted = config("fixture");
    granted["permissions"]["cache"] = json!(true);
    let with_cache = open(&runtime, &granted);
    let without_cache = open(&runtime, &config("fixture"));

    let first = titles(ok(&search(&with_cache, "cache")))
        .first()
        .copied()
        .map(str::to_owned);
    let second = titles(ok(&search(&with_cache, "cache")))
        .first()
        .copied()
        .map(str::to_owned);
    let denied_first = titles(ok(&search(&without_cache, "cache")))
        .first()
        .copied()
        .map(str::to_owned);
    let denied_second = titles(ok(&search(&without_cache, "cache")))
        .first()
        .copied()
        .map(str::to_owned);

    assert_eq!(
        (first.as_deref(), second.as_deref()),
        (Some("miss"), Some("hit"))
    );
    assert_eq!(
        (denied_first.as_deref(), denied_second.as_deref()),
        (Some("miss"), Some("miss"))
    );
}

#[test]
fn settings_reach_the_plugin_as_text() {
    let runtime = runtime();
    let mut with_settings = config("fixture");
    with_settings["settings"] =
        json!({ "connection.user": ["alice"], "search.maturity": ["safe", "suggestive"] });
    let plugin = open(&runtime, &with_settings);

    let user = search(&plugin, "setting:connection.user");
    let multi = search(&plugin, "setting:search.maturity");
    let unset = search(&plugin, "setting:nothing");

    assert_eq!(titles(ok(&user)), ["alice"]);
    assert_eq!(titles(ok(&multi)), ["safe"]);
    assert_eq!(
        ok(&multi)["items"][0]["props"][0]["value"]["Text"],
        "safe|suggestive"
    );
    assert_eq!(titles(ok(&unset)), ["<none>"]);
}

#[test]
fn the_clock_can_be_frozen_for_tests() {
    let runtime = runtime();
    let mut frozen = config("fixture");
    frozen["testing"] = json!({ "fixedNowMs": 1_700_000_000_123_i64 });

    let envelope = search(&open(&runtime, &frozen), "now");

    assert_eq!(titles(ok(&envelope)), ["1700000000123"]);
}

#[test]
fn http_to_a_host_outside_the_allow_list_is_refused_naming_the_host() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let envelope = search(&plugin, "http:https://evil.example.net/x");

    assert_eq!(error_code(&envelope), "InvalidArgument");
    assert!(
        envelope["error"]["message"]
            .as_str()
            .unwrap()
            .contains("evil.example.net")
    );
    assert_eq!(envelope["trapped"], false);
}

#[test]
fn http_through_the_host_reaches_an_allowed_server() {
    let server = TestServer::start(|_| Reply::ok("pong"));
    let runtime = runtime();
    let mut allowed = config("fixture");
    allowed["permissions"]["network"] = json!(["localhost"]);
    allowed["testing"] = json!({ "allowPrivateAddresses": true });
    let plugin = open(&runtime, &allowed);

    let envelope = search(&plugin, &format!("http:{}", server.url("/ping")));

    assert_eq!(titles(ok(&envelope)), ["status=200 bytes=4"]);
    assert_eq!(server.requests()[0].header("x-probe"), Some("1"));
}

#[test]
fn an_allowed_name_that_resolves_to_loopback_is_refused_in_production_mode() {
    let server = TestServer::start(|_| Reply::ok("internal"));
    let runtime = runtime();
    let mut allowed = config("fixture");
    allowed["permissions"]["network"] = json!(["localhost"]);
    let plugin = open(&runtime, &allowed);

    let envelope = search(&plugin, &format!("http:{}", server.url("/internal")));

    assert_eq!(error_code(&envelope), "Unavailable");
    assert!(server.requests().is_empty());
}

#[test]
fn time_spent_waiting_on_the_network_is_not_charged_to_the_cpu_budget() {
    // The upstream takes 700 ms but the plugin's CPU budget is only 200 ms: the call must succeed,
    // because waiting on the network is the host's time, not the plugin's.
    let server =
        TestServer::start(|_| Reply::ok("slow but fine").delayed(Duration::from_millis(700)));
    let runtime = runtime();
    let mut allowed = config("fixture");
    allowed["permissions"]["network"] = json!(["localhost"]);
    allowed["limits"]["cpuBudgetMs"] = json!(200);
    allowed["testing"] = json!({ "allowPrivateAddresses": true });
    let plugin = open(&runtime, &allowed);

    let envelope = search(&plugin, &format!("http:{}", server.url("/slow")));

    assert_eq!(envelope["trapped"], false, "{envelope}");
    assert_eq!(titles(ok(&envelope)), ["status=200 bytes=13"]);
}

#[test]
fn the_call_timeout_bounds_a_plugin_that_keeps_waiting() {
    let server = TestServer::start(|_| Reply::ok("never").delayed(Duration::from_secs(5)));
    let runtime = runtime();
    let mut allowed = config("fixture");
    allowed["permissions"]["network"] = json!(["localhost"]);
    allowed["limits"]["callTimeoutMs"] = json!(400);
    allowed["testing"] = json!({ "allowPrivateAddresses": true });
    let plugin = open(&runtime, &allowed);

    let started = Instant::now();
    let envelope = search(&plugin, &format!("http:{}", server.url("/hang")));

    assert!(started.elapsed() < Duration::from_secs(3));
    assert_eq!(error_code(&envelope), "Unavailable");
}

#[test]
fn unknown_operations_and_malformed_requests_are_invalid_arguments_not_crashes() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    let unknown = call(&plugin, "format-disk", &json!({})).0;
    let malformed = plugin.call("search", b"{ not json");
    let malformed: Value = serde_json::from_slice(&malformed.json).unwrap();
    let wrong_shape = call(&plugin, "search", &json!({ "text": 5 })).0;

    for envelope in [&unknown, &malformed, &wrong_shape] {
        assert_eq!(error_code(envelope), "InvalidArgument");
        assert_eq!(envelope["trapped"], false);
    }
}

#[test]
fn optional_search_assist_is_detected_and_used_when_present_and_unsupported_when_absent() {
    let runtime = runtime();
    let plain = open(&runtime, &config("fixture"));
    let assisted = open(&runtime, &config("fixture-assist"));

    let suggestions = call(&assisted, "suggest", &json!({ "text": "ber", "limit": 2 })).0;
    let enriched = call(&assisted, "enrich", &json!({ "ids": ["s1"] })).0;
    let absent = call(&plain, "suggest", &json!({ "text": "ber", "limit": 2 })).0;

    assert!(!plain.has_search_assist());
    assert!(assisted.has_search_assist());
    assert_eq!(ok(&suggestions), &json!(["ber 1", "ber 2"]));
    assert_eq!(ok(&enriched)[0]["title"], "Enriched s1");
    assert_eq!(error_code(&absent), "Unsupported");
    // An assisted plugin still serves the base contract.
    assert_eq!(titles(ok(&search(&assisted, "series"))).len(), 3);
}

#[test]
fn a_plugin_built_for_a_newer_compatible_contract_version_loads_and_its_extension_is_detected() {
    // fixture-newer-patch was built against emma:core@1.0.1; this host was built for 1.0.0.
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture-newer-patch"));

    let health = call(&plugin, "health", &json!({})).0;
    let suggestions = call(&plugin, "suggest", &json!({ "text": "ab", "limit": 2 })).0;

    assert_eq!(ok(&health)["ok"], true);
    assert!(plugin.has_search_assist());
    assert_eq!(ok(&suggestions), &json!(["ab 1", "ab 2"]));
}

#[test]
fn a_precompiled_component_loads_and_behaves_the_same() {
    let runtime = runtime();
    let source = std::fs::read(common::plugin_path("fixture")).unwrap();
    let compiled = runtime.precompile(&source).unwrap();
    let directory = std::env::temp_dir().join(format!(
        "emma-cwasm-{}-{}",
        std::process::id(),
        runtime.compatibility_key()
    ));
    std::fs::create_dir_all(&directory).unwrap();
    let path = directory.join("fixture.cwasm");
    std::fs::write(&path, &compiled).unwrap();
    let mut precompiled = config("fixture");
    precompiled["componentPath"] = json!(path);
    precompiled["precompiled"] = json!(true);

    let envelope = search(&open(&runtime, &precompiled), "series");

    assert_eq!(titles(ok(&envelope)).len(), 3);
    assert_ne!(
        compiled, source,
        "the precompiled output is native code, not the component"
    );
    std::fs::remove_dir_all(&directory).unwrap();
}

#[test]
fn the_compatibility_key_is_stable_and_names_the_engine() {
    let first = runtime();
    let second = runtime();

    assert_eq!(first.compatibility_key(), second.compatibility_key());
    assert_eq!(first.compatibility_key().len(), 16);
}

#[test]
fn calls_run_in_parallel_without_interfering() {
    let runtime = runtime();
    let plugin = open(&runtime, &config("fixture"));

    std::thread::scope(|scope| {
        let workers: Vec<_> = (0..8)
            .map(|index| {
                let plugin = &plugin;
                scope.spawn(move || {
                    for _ in 0..5 {
                        let crashing = index % 4 == 0;
                        let envelope = search(plugin, if crashing { "trap" } else { "series" });
                        assert_eq!(envelope["trapped"], crashing);
                    }
                })
            })
            .collect();
        for worker in workers {
            worker.join().unwrap();
        }
    });
}

#[test]
fn opening_something_that_is_not_a_plugin_fails_clearly() {
    let runtime = runtime();
    let directory = std::env::temp_dir().join(format!("emma-bad-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let garbage = directory.join("garbage.wasm");
    std::fs::write(&garbage, b"this is not wasm").unwrap();
    let mut bad = config("fixture");
    bad["componentPath"] = json!(garbage);
    let mut missing = config("fixture");
    missing["componentPath"] = json!(directory.join("missing.wasm"));
    let mut out_of_range = config("fixture");
    out_of_range["limits"]["memoryMb"] = json!(0);

    for config in [bad, missing, out_of_range] {
        let parsed: emma_wasm::PluginConfig = serde_json::from_value(config).unwrap();
        assert!(runtime.open(&parsed).is_err());
    }
    std::fs::remove_dir_all(&directory).unwrap();
}
