//! Shared helpers for the integration tests: a tiny local HTTP server and plugin-loading shortcuts.
#![allow(dead_code, missing_docs)]

use std::fmt::Write as _;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::Duration;

use emma_wasm::{Plugin, PluginConfig, Runtime, RuntimeOptions};
use serde_json::{Value, json};

/// One request the test server received.
#[derive(Debug, Clone)]
pub struct Received {
    pub method: String,
    pub path: String,
    pub headers: Vec<(String, String)>,
}

impl Received {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(key, _)| key.eq_ignore_ascii_case(name))
            .map(|(_, value)| value.as_str())
    }
}

/// What the test server answers.
pub struct Reply {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
    pub delay: Duration,
}

impl Reply {
    pub fn ok(body: &str) -> Self {
        Self {
            status: 200,
            headers: vec![],
            body: body.as_bytes().to_vec(),
            delay: Duration::ZERO,
        }
    }

    pub fn status(status: u16) -> Self {
        Self {
            status,
            headers: vec![],
            body: vec![],
            delay: Duration::ZERO,
        }
    }

    pub fn redirect(status: u16, location: &str) -> Self {
        Self {
            status,
            headers: vec![("Location".to_owned(), location.to_owned())],
            body: vec![],
            delay: Duration::ZERO,
        }
    }

    pub fn delayed(mut self, delay: Duration) -> Self {
        self.delay = delay;
        self
    }
}

type Handler = dyn Fn(&Received) -> Reply + Send + Sync;

/// A local HTTP/1.1 server bound to a free loopback port.
pub struct TestServer {
    pub port: u16,
    requests: Arc<Mutex<Vec<Received>>>,
    stop: Arc<AtomicBool>,
    thread: Option<JoinHandle<()>>,
}

impl TestServer {
    pub fn start(handler: impl Fn(&Received) -> Reply + Send + Sync + 'static) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
        listener.set_nonblocking(true).expect("nonblocking");
        let port = listener.local_addr().expect("addr").port();
        let requests = Arc::new(Mutex::new(Vec::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let handler: Arc<Handler> = Arc::new(handler);
        let thread = {
            let (requests, stop) = (Arc::clone(&requests), Arc::clone(&stop));
            std::thread::spawn(move || {
                while !stop.load(Ordering::Relaxed) {
                    match listener.accept() {
                        Ok((stream, _)) => {
                            let (requests, handler) = (Arc::clone(&requests), Arc::clone(&handler));
                            std::thread::spawn(move || serve(stream, &requests, handler.as_ref()));
                        }
                        Err(_) => std::thread::sleep(Duration::from_millis(5)),
                    }
                }
            })
        };
        Self {
            port,
            requests,
            stop,
            thread: Some(thread),
        }
    }

    pub fn url(&self, path: &str) -> String {
        format!("http://localhost:{}{path}", self.port)
    }

    pub fn requests(&self) -> Vec<Received> {
        self.requests.lock().expect("lock").clone()
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn serve(stream: TcpStream, requests: &Mutex<Vec<Received>>, handler: &Handler) {
    let Ok(clone) = stream.try_clone() else {
        return;
    };
    let mut reader = BufReader::new(clone);
    let mut line = String::new();
    if reader.read_line(&mut line).is_err() {
        return;
    }
    let mut parts = line.split_whitespace();
    let (method, path) = (
        parts.next().unwrap_or("").to_owned(),
        parts.next().unwrap_or("").to_owned(),
    );
    let mut headers = Vec::new();
    let mut content_length = 0_usize;
    loop {
        let mut header = String::new();
        if reader.read_line(&mut header).is_err() || header.trim().is_empty() {
            break;
        }
        if let Some((name, value)) = header.split_once(':') {
            if name.eq_ignore_ascii_case("content-length") {
                content_length = value.trim().parse().unwrap_or(0);
            }
            headers.push((name.trim().to_owned(), value.trim().to_owned()));
        }
    }
    let mut body = vec![0_u8; content_length];
    let _ = reader.read_exact(&mut body);

    let received = Received {
        method,
        path,
        headers,
    };
    requests.lock().expect("lock").push(received.clone());
    let reply = handler(&received);
    std::thread::sleep(reply.delay);

    let mut stream = stream;
    let mut head = format!(
        "HTTP/1.1 {} X\r\nContent-Length: {}\r\nConnection: close\r\n",
        reply.status,
        reply.body.len()
    );
    for (name, value) in &reply.headers {
        let _ = write!(head, "{name}: {value}\r\n");
    }
    head.push_str("\r\n");
    let _ = stream.write_all(head.as_bytes());
    let _ = stream.write_all(&reply.body);
    let _ = stream.flush();
}

/// The path of a built plugin component.
pub fn plugin_path(name: &str) -> PathBuf {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .join("../../artifacts/native/plugins")
        .join(format!("{name}.wasm"));
    assert!(
        path.exists(),
        "{} is missing: run `sh native/build-plugins.sh` first",
        path.display()
    );
    path
}

/// A plugin configuration as JSON, the same shape the .NET side sends.
pub fn config(name: &str) -> Value {
    json!({
        "componentPath": plugin_path(name),
        "limits": { "cpuBudgetMs": 5000, "memoryMb": 128 },
        "permissions": { "network": [], "cache": false },
    })
}

pub fn runtime() -> Runtime {
    Runtime::new(&RuntimeOptions::default()).expect("runtime")
}

pub fn open(runtime: &Runtime, config: &Value) -> Plugin {
    let config: PluginConfig = serde_json::from_value(config.clone()).expect("config");
    runtime.open(&config).expect("open")
}

/// Calls an operation and returns the envelope and the binary payload.
pub fn call(plugin: &Plugin, operation: &str, request: &Value) -> (Value, Vec<u8>) {
    let output = plugin.call(operation, &serde_json::to_vec(request).expect("request"));
    (
        serde_json::from_slice(&output.json).expect("envelope"),
        output.blob,
    )
}

/// Searches with some text and returns the envelope.
pub fn search(plugin: &Plugin, text: &str) -> Value {
    call(plugin, "search", &json!({ "text": text, "kinds": [], "filters": [], "sort": null, "cursor": null, "limit": null })).0
}
