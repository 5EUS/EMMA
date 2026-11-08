//! Tests for `Network::fetch` against a local server.
#![allow(missing_docs)]

mod common;

use std::time::{Duration, Instant};

use common::{Reply, TestServer};
use emma_wasm::network::{FetchError, FetchLimits, FetchRequest, HostPolicy, Network};

fn request(url: &str) -> FetchRequest {
    FetchRequest {
        method: "GET".to_owned(),
        url: url.to_owned(),
        headers: vec![],
        body: None,
        timeout_ms: None,
    }
}

fn deadline() -> Instant {
    Instant::now() + Duration::from_secs(10)
}

fn local(patterns: &[&str], limits: FetchLimits) -> Network {
    Network::new(HostPolicy::new(patterns), limits, true)
}

#[test]
fn a_response_comes_back_with_lowercase_headers() {
    let server = TestServer::start(|_| {
        let mut reply = Reply::ok("hello");
        reply
            .headers
            .push(("X-Custom".to_owned(), "Value".to_owned()));
        reply
    });

    let response = local(&["localhost"], FetchLimits::default())
        .fetch(&request(&server.url("/a")), deadline())
        .unwrap();

    assert_eq!(response.status, 200);
    assert_eq!(response.body, b"hello");
    assert!(
        response
            .headers
            .iter()
            .any(|(name, value)| name == "x-custom" && value == "Value")
    );
}

#[test]
fn error_statuses_are_successful_calls() {
    let server = TestServer::start(|_| Reply::status(404));

    let response = local(&["localhost"], FetchLimits::default())
        .fetch(&request(&server.url("/missing")), deadline())
        .unwrap();

    assert_eq!(response.status, 404);
}

#[test]
fn a_permitted_redirect_is_followed() {
    let server = TestServer::start(|received| {
        if received.path == "/start" {
            Reply::redirect(302, "/final")
        } else {
            Reply::ok("done")
        }
    });

    let response = local(&["localhost"], FetchLimits::default())
        .fetch(&request(&server.url("/start")), deadline())
        .unwrap();

    assert_eq!(response.body, b"done");
    assert_eq!(server.requests().len(), 2);
}

#[test]
fn a_redirect_to_a_forbidden_host_is_refused_before_it_is_requested() {
    let server = TestServer::start(|_| Reply::redirect(302, "http://evil.example.net/steal"));

    let error = local(&["localhost"], FetchLimits::default())
        .fetch(&request(&server.url("/start")), deadline())
        .unwrap_err();

    assert!(
        matches!(&error, FetchError::Refused(message) if message.contains("evil.example.net")),
        "{error:?}"
    );
    assert_eq!(server.requests().len(), 1);
}

#[test]
fn credentials_are_dropped_when_a_redirect_changes_host_but_other_headers_stay() {
    // "localhost" and "127.0.0.1" are different hosts to the policy but reach the same server.
    let server = TestServer::start(|received| {
        if received.path == "/a" {
            Reply::redirect(
                302,
                &format!(
                    "http://127.0.0.1:{}/b",
                    received
                        .header("host")
                        .unwrap_or("")
                        .rsplit(':')
                        .next()
                        .unwrap_or("0")
                ),
            )
        } else {
            Reply::ok("ok")
        }
    });
    let mut fetch = request(&server.url("/a"));
    fetch.headers = vec![
        ("Authorization".into(), "Bearer secret".into()),
        ("Cookie".into(), "s=1".into()),
        ("Referer".into(), "https://example.com/".into()),
    ];

    local(&["localhost", "127.0.0.1"], FetchLimits::default())
        .fetch(&fetch, deadline())
        .unwrap();

    let seen = server.requests();
    assert_eq!(seen.len(), 2);
    assert!(seen[0].header("authorization").is_some() && seen[0].header("cookie").is_some());
    assert!(
        seen[1].header("authorization").is_none(),
        "credentials must not follow the redirect"
    );
    assert!(seen[1].header("cookie").is_none());
    assert_eq!(seen[1].header("referer"), Some("https://example.com/"));
}

#[test]
fn an_endless_redirect_chain_stops() {
    let server = TestServer::start(|_| Reply::redirect(302, "/again"));

    let error = local(&["localhost"], FetchLimits::default())
        .fetch(&request(&server.url("/")), deadline())
        .unwrap_err();

    assert!(
        matches!(&error, FetchError::Unavailable(message) if message.contains("redirects")),
        "{error:?}"
    );
    assert_eq!(server.requests().len(), 6);
}

#[test]
fn only_get_and_head_follow_redirects() {
    let server = TestServer::start(|_| Reply::redirect(302, "/elsewhere"));
    let mut post = request(&server.url("/submit"));
    post.method = "POST".to_owned();
    post.body = Some(b"data".to_vec());

    let response = local(&["localhost"], FetchLimits::default())
        .fetch(&post, deadline())
        .unwrap();

    assert_eq!(response.status, 302);
    assert_eq!(server.requests().len(), 1);
}

#[test]
fn a_response_over_the_size_limit_is_refused() {
    let server = TestServer::start(|_| Reply::ok(&"x".repeat(5000)));
    let limits = FetchLimits {
        max_response_bytes: 1000,
        ..FetchLimits::default()
    };

    let error = local(&["localhost"], limits)
        .fetch(&request(&server.url("/big")), deadline())
        .unwrap_err();

    assert!(
        matches!(&error, FetchError::Unavailable(message) if message.contains("larger")),
        "{error:?}"
    );
}

#[test]
fn a_slow_upstream_times_out() {
    let server = TestServer::start(|_| Reply::ok("late").delayed(Duration::from_secs(3)));
    let mut fetch = request(&server.url("/slow"));
    fetch.timeout_ms = Some(300);

    let started = Instant::now();
    let error = local(&["localhost"], FetchLimits::default())
        .fetch(&fetch, deadline())
        .unwrap_err();

    assert!(matches!(error, FetchError::Unavailable(_)));
    assert!(started.elapsed() < Duration::from_secs(2));
}

#[test]
fn the_call_deadline_bounds_a_fetch() {
    let server = TestServer::start(|_| Reply::ok("late").delayed(Duration::from_secs(3)));

    let started = Instant::now();
    let error = local(&["localhost"], FetchLimits::default())
        .fetch(
            &request(&server.url("/slow")),
            Instant::now() + Duration::from_millis(300),
        )
        .unwrap_err();

    assert!(matches!(error, FetchError::Unavailable(_)));
    assert!(started.elapsed() < Duration::from_secs(2));
}

#[test]
fn an_allowed_name_cannot_be_pointed_at_a_loopback_address() {
    // SSRF: "localhost" is on the allow-list but resolves to 127.0.0.1, which is not public.
    let server = TestServer::start(|_| Reply::ok("secret internal data"));
    let network = Network::new(
        HostPolicy::new(&["localhost"]),
        FetchLimits::default(),
        false,
    );

    let error = network
        .fetch(&request(&server.url("/internal")), deadline())
        .unwrap_err();

    assert!(
        matches!(&error, FetchError::Unavailable(message) if message.contains("public address")),
        "{error:?}"
    );
    assert!(
        server.requests().is_empty(),
        "the connection must never have been made"
    );
}
