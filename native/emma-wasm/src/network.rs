//! Outbound network access for plugins: the allow-list, the address check, and the fetcher.
//!
//! Every plugin request goes through [`Network::fetch`]. The allow-list applies to the first URL
//! and to every redirect hop; credentials are dropped when a redirect leaves the original host;
//! and a vetted resolver means a connection can only ever go to an address that was checked,
//! so a permitted host name cannot be pointed at a loopback or internal address.

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::sync::Arc;
use std::time::{Duration, Instant};

use ureq::Agent;
use ureq::config::Config;
use ureq::unversioned::resolver::{DefaultResolver, ResolvedSocketAddrs, Resolver};
use ureq::unversioned::transport::{DefaultConnector, NextTimeout};

/// The most redirects followed for one fetch.
const MAX_REDIRECTS: usize = 5;

/// Headers that carry credentials and must not follow a redirect to another host.
const CREDENTIAL_HEADERS: [&str; 3] = ["authorization", "cookie", "proxy-authorization"];

/// Headers the transport owns; a plugin may not set them.
const FORBIDDEN_HEADERS: [&str; 6] = [
    "host",
    "content-length",
    "transfer-encoding",
    "connection",
    "upgrade",
    "te",
];

/// The methods a plugin may use.
const ALLOWED_METHODS: [&str; 7] = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

/// Which hosts a plugin may reach, from its manifest.
#[derive(Debug, Clone, Default)]
pub struct HostPolicy {
    exact: Vec<String>,
    wildcard_suffixes: Vec<String>,
}

impl HostPolicy {
    /// Builds a policy from manifest patterns: exact host names, or `*.example.com` wildcards.
    #[must_use]
    pub fn new<S: AsRef<str>>(patterns: &[S]) -> Self {
        let mut policy = Self::default();
        for pattern in patterns {
            let pattern = pattern.as_ref().trim().to_ascii_lowercase();
            if let Some(suffix) = pattern.strip_prefix('*') {
                if suffix.starts_with('.') {
                    policy.wildcard_suffixes.push(suffix.to_owned());
                }
            } else if !pattern.is_empty() {
                policy.exact.push(pattern);
            }
        }
        policy
    }

    /// Decides whether a URL may be reached. Only http and https are ever allowed. A wildcard
    /// matches subdomains at any depth but not the apex, and never an IP literal.
    #[must_use]
    pub fn allows(&self, url: &ureq::http::Uri) -> bool {
        let Some(scheme) = url.scheme_str() else {
            return false;
        };
        if !scheme.eq_ignore_ascii_case("http") && !scheme.eq_ignore_ascii_case("https") {
            return false;
        }
        let Some(host) = url.host() else {
            return false;
        };
        let host = host
            .trim_start_matches('[')
            .trim_end_matches(']')
            .to_ascii_lowercase();
        if self.exact.contains(&host) {
            return true;
        }
        let is_ip = host.parse::<IpAddr>().is_ok();
        !is_ip
            && self
                .wildcard_suffixes
                .iter()
                .any(|suffix| host.ends_with(suffix.as_str()))
    }
}

/// Decides whether an address is publicly routable. Loopback, private, link-local, shared,
/// documentation, multicast and reserved ranges are not, and neither are IPv6 addresses that
/// embed a non-public IPv4 address.
#[must_use]
pub fn is_public_address(address: IpAddr) -> bool {
    match address {
        IpAddr::V4(v4) => is_public_v4(v4),
        IpAddr::V6(v6) => is_public_v6(v6),
    }
}

fn is_public_v4(address: Ipv4Addr) -> bool {
    let [a, b, c, _] = address.octets();
    !(a == 0
        || a == 10
        || (a == 100 && (64..=127).contains(&b))
        || a == 127
        || (a == 169 && b == 254)
        || (a == 172 && (16..=31).contains(&b))
        || (a == 192 && b == 0 && c == 0)
        || (a == 192 && b == 0 && c == 2)
        || (a == 192 && b == 88 && c == 99)
        || (a == 192 && b == 168)
        || (a == 198 && (b == 18 || b == 19))
        || (a == 198 && b == 51 && c == 100)
        || (a == 203 && b == 0 && c == 113)
        || a >= 224)
}

fn is_public_v6(address: Ipv6Addr) -> bool {
    if let Some(mapped) = address.to_ipv4_mapped() {
        return is_public_v4(mapped);
    }
    let segments = address.segments();
    // 64:ff9b::/96 (NAT64) embeds an IPv4 address in the last 32 bits.
    if segments[0] == 0x0064 && segments[1] == 0xff9b && segments[2..6].iter().all(|s| *s == 0) {
        let embedded = Ipv4Addr::new(
            (segments[6] >> 8) as u8,
            (segments[6] & 0xff) as u8,
            (segments[7] >> 8) as u8,
            (segments[7] & 0xff) as u8,
        );
        return is_public_v4(embedded);
    }
    !(address.is_unspecified()
        || address.is_loopback()
        || (segments[0] & 0xfe00) == 0xfc00 // fc00::/7 unique local
        || (segments[0] & 0xffc0) == 0xfe80 // fe80::/10 link local
        || (segments[0] & 0xffc0) == 0xfec0 // fec0::/10 deprecated site local
        || (segments[0] & 0xff00) == 0xff00 // multicast
        || (segments[0] == 0x2001 && segments[1] == 0x0db8)) // documentation
}

/// A resolver that only ever returns publicly routable addresses (unless private access is
/// explicitly enabled for tests). The address ureq connects to is the one returned here, so
/// there is no gap between checking and using it.
#[derive(Debug)]
struct VettedResolver {
    allow_private: bool,
    inner: DefaultResolver,
}

impl Resolver for VettedResolver {
    fn resolve(
        &self,
        uri: &ureq::http::Uri,
        config: &Config,
        timeout: NextTimeout,
    ) -> Result<ResolvedSocketAddrs, ureq::Error> {
        let resolved = self.inner.resolve(uri, config, timeout)?;
        if self.allow_private {
            return Ok(resolved);
        }
        let mut vetted = self.inner.empty();
        for address in resolved.iter().filter(|a| is_public_address(a.ip())) {
            vetted.push(*address);
        }
        if vetted.is_empty() {
            return Err(ureq::Error::HostNotFound);
        }
        Ok(vetted)
    }
}

/// What a plugin asked to fetch.
#[derive(Debug, Clone)]
pub struct FetchRequest {
    /// Upper-case HTTP method.
    pub method: String,
    /// The absolute URL.
    pub url: String,
    /// Request headers.
    pub headers: Vec<(String, String)>,
    /// The request body, if any.
    pub body: Option<Vec<u8>>,
    /// The plugin's requested timeout in milliseconds.
    pub timeout_ms: Option<u32>,
}

/// What came back.
#[derive(Debug, Clone)]
pub struct FetchResponse {
    /// The HTTP status.
    pub status: u16,
    /// Response headers, names lower-cased.
    pub headers: Vec<(String, String)>,
    /// The body, at most the configured limit.
    pub body: Vec<u8>,
}

/// Why a fetch did not happen.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FetchError {
    /// The request was malformed or names a host the plugin may not reach.
    Refused(String),
    /// The upstream could not be reached or answered badly.
    Unavailable(String),
}

/// Limits applied to every fetch.
#[derive(Debug, Clone, Copy)]
pub struct FetchLimits {
    /// The largest response body accepted.
    pub max_response_bytes: u64,
    /// The largest request body accepted.
    pub max_request_bytes: usize,
    /// The longest a single request may take.
    pub max_timeout: Duration,
}

impl Default for FetchLimits {
    fn default() -> Self {
        Self {
            max_response_bytes: 32 * 1024 * 1024,
            max_request_bytes: 8 * 1024 * 1024,
            max_timeout: Duration::from_secs(30),
        }
    }
}

/// Performs fetches on behalf of one plugin.
#[derive(Debug, Clone)]
pub struct Network {
    policy: Arc<HostPolicy>,
    limits: FetchLimits,
    agent: Agent,
}

impl Network {
    /// Creates a network for one plugin.
    #[must_use]
    pub fn new(policy: HostPolicy, limits: FetchLimits, allow_private_addresses: bool) -> Self {
        let config = Config::builder()
            .max_redirects(0)
            .http_status_as_error(false)
            .user_agent("EMMA/1.0 (plugin host)")
            .build();
        let resolver = VettedResolver {
            allow_private: allow_private_addresses,
            inner: DefaultResolver::default(),
        };
        Self {
            policy: Arc::new(policy),
            limits,
            agent: Agent::with_parts(config, DefaultConnector::default(), resolver),
        }
    }

    /// Fetches a URL, following redirects only when each hop is allowed.
    ///
    /// # Errors
    /// [`FetchError::Refused`] for a malformed request or a forbidden host; [`FetchError::Unavailable`]
    /// when the upstream cannot be reached or the response is too large.
    pub fn fetch(
        &self,
        request: &FetchRequest,
        deadline: Instant,
    ) -> Result<FetchResponse, FetchError> {
        let method = request.method.to_ascii_uppercase();
        if !ALLOWED_METHODS.contains(&method.as_str()) {
            return Err(FetchError::Refused(format!(
                "The method '{}' is not allowed.",
                request.method
            )));
        }
        if request
            .body
            .as_ref()
            .is_some_and(|b| b.len() > self.limits.max_request_bytes)
        {
            return Err(FetchError::Refused(
                "The request body is too large.".to_owned(),
            ));
        }
        let mut headers = validated_headers(&request.headers)?;
        let mut current: ureq::http::Uri = request
            .url
            .parse()
            .map_err(|_| FetchError::Refused("The URL is not valid.".to_owned()))?;
        let follows_redirects = method == "GET" || method == "HEAD";

        for _ in 0..=MAX_REDIRECTS {
            if !self.policy.allows(&current) {
                return Err(FetchError::Refused(format!(
                    "The plugin may not reach '{}'.",
                    current.host().unwrap_or("that address")
                )));
            }
            let response = self.send_once(
                &method,
                &current,
                &headers,
                request.body.as_deref(),
                request.timeout_ms,
                deadline,
            )?;
            let status = response.status().as_u16();
            let location = response
                .headers()
                .get("location")
                .and_then(|v| v.to_str().ok())
                .map(str::to_owned);
            match (
                follows_redirects && matches!(status, 301 | 302 | 303 | 307 | 308),
                location,
            ) {
                (true, Some(location)) => {
                    let next = resolve_location(&current, &location).ok_or_else(|| {
                        FetchError::Unavailable("The redirect target is not valid.".to_owned())
                    })?;
                    if next.host() != current.host() {
                        headers.retain(|(name, _)| !CREDENTIAL_HEADERS.contains(&name.as_str()));
                    }
                    current = next;
                }
                _ => return self.read_response(response),
            }
        }
        Err(FetchError::Unavailable("Too many redirects.".to_owned()))
    }

    fn send_once(
        &self,
        method: &str,
        url: &ureq::http::Uri,
        headers: &[(String, String)],
        body: Option<&[u8]>,
        timeout_ms: Option<u32>,
        deadline: Instant,
    ) -> Result<ureq::http::Response<ureq::Body>, FetchError> {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(FetchError::Unavailable(
                "The call ran out of time.".to_owned(),
            ));
        }
        let wanted = timeout_ms.map_or(self.limits.max_timeout, |ms| {
            Duration::from_millis(u64::from(ms))
        });
        let timeout = wanted.min(self.limits.max_timeout).min(remaining);

        let mut builder = ureq::http::Request::builder()
            .method(method)
            .uri(url.clone());
        for (name, value) in headers {
            builder = builder.header(name.as_str(), value.as_str());
        }
        let request = builder
            .body(body.map(<[u8]>::to_vec).unwrap_or_default())
            .map_err(|_| FetchError::Refused("The request is not valid.".to_owned()))?;

        let configured = self
            .agent
            .configure_request(request)
            .timeout_global(Some(timeout))
            .build();
        self.agent
            .run(configured)
            .map_err(|error| FetchError::Unavailable(describe(&error, url)))
    }

    fn read_response(
        &self,
        mut response: ureq::http::Response<ureq::Body>,
    ) -> Result<FetchResponse, FetchError> {
        let status = response.status().as_u16();
        let headers = response
            .headers()
            .iter()
            .map(|(name, value)| {
                (
                    name.as_str().to_ascii_lowercase(),
                    String::from_utf8_lossy(value.as_bytes()).into_owned(),
                )
            })
            .collect();
        let body = response
            .body_mut()
            .with_config()
            .limit(self.limits.max_response_bytes)
            .read_to_vec()
            .map_err(|error| match error {
                ureq::Error::BodyExceedsLimit(_) => {
                    FetchError::Unavailable("The response is larger than allowed.".to_owned())
                }
                other => {
                    FetchError::Unavailable(format!("The response could not be read: {other}."))
                }
            })?;
        Ok(FetchResponse {
            status,
            headers,
            body,
        })
    }
}

fn describe(error: &ureq::Error, url: &ureq::http::Uri) -> String {
    let host = url.host().unwrap_or("the host");
    match error {
        ureq::Error::HostNotFound => format!("'{host}' could not be resolved to a public address."),
        ureq::Error::Timeout(_) => format!("'{host}' did not answer in time."),
        other => format!("Could not reach '{host}': {other}."),
    }
}

fn validated_headers(headers: &[(String, String)]) -> Result<Vec<(String, String)>, FetchError> {
    let mut checked = Vec::with_capacity(headers.len());
    for (name, value) in headers {
        let lower = name.to_ascii_lowercase();
        if FORBIDDEN_HEADERS.contains(&lower.as_str()) {
            return Err(FetchError::Refused(format!(
                "The header '{name}' is set by the host."
            )));
        }
        if ureq::http::HeaderName::from_bytes(lower.as_bytes()).is_err()
            || ureq::http::HeaderValue::from_str(value).is_err()
        {
            return Err(FetchError::Refused(format!(
                "The header '{name}' is not valid."
            )));
        }
        checked.push((lower, value.clone()));
    }
    Ok(checked)
}

/// Resolves a `Location` header against the URL that produced it.
fn resolve_location(base: &ureq::http::Uri, location: &str) -> Option<ureq::http::Uri> {
    let scheme = base.scheme_str()?;
    let authority = base.authority()?;
    let resolved = if location.contains("://") {
        location.to_owned()
    } else if let Some(rest) = location.strip_prefix("//") {
        format!("{scheme}://{rest}")
    } else if location.starts_with('/') {
        format!("{scheme}://{authority}{location}")
    } else {
        let directory = base.path().rsplit_once('/').map_or("", |(dir, _)| dir);
        format!("{scheme}://{authority}{directory}/{location}")
    };
    resolved.parse().ok()
}

/// The address a vetted resolver would reject, for diagnostics in tests.
#[must_use]
pub fn socket_is_public(address: SocketAddr) -> bool {
    is_public_address(address.ip())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn uri(text: &str) -> ureq::http::Uri {
        text.parse().unwrap()
    }

    #[test]
    fn shared_vectors_agree_with_the_csharp_policy() {
        #[derive(serde::Deserialize)]
        struct Case {
            patterns: Vec<String>,
            url: String,
            allowed: bool,
        }
        #[derive(serde::Deserialize)]
        struct Vectors {
            cases: Vec<Case>,
        }
        let text = std::fs::read_to_string(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/vectors/network-policy.json"
        ))
        .unwrap();
        let vectors: Vectors = serde_json::from_str(&text).unwrap();
        assert!(vectors.cases.len() >= 15);
        for case in vectors.cases {
            let policy = HostPolicy::new(&case.patterns);
            assert_eq!(
                policy.allows(&uri(&case.url)),
                case.allowed,
                "{:?} vs {}",
                case.patterns,
                case.url
            );
        }
    }

    #[test]
    fn private_and_reserved_addresses_are_not_public() {
        for text in [
            "0.0.0.0",
            "10.1.2.3",
            "100.64.0.1",
            "127.0.0.1",
            "169.254.169.254",
            "172.16.0.1",
            "172.31.255.255",
            "192.0.0.1",
            "192.168.1.1",
            "198.18.0.1",
            "198.51.100.1",
            "203.0.113.9",
            "224.0.0.1",
            "255.255.255.255",
            "::",
            "::1",
            "fc00::1",
            "fd12:3456::1",
            "fe80::1",
            "ff02::1",
            "2001:db8::1",
            "::ffff:127.0.0.1",
            "::ffff:10.0.0.1",
            "64:ff9b::7f00:1",
        ] {
            assert!(
                !is_public_address(text.parse().unwrap()),
                "{text} should not be public"
            );
        }
    }

    #[test]
    fn ordinary_addresses_are_public() {
        for text in [
            "8.8.8.8",
            "1.1.1.1",
            "93.184.216.34",
            "172.32.0.1",
            "100.63.255.255",
            "2606:4700:4700::1111",
            "::ffff:8.8.8.8",
        ] {
            assert!(
                is_public_address(text.parse().unwrap()),
                "{text} should be public"
            );
        }
    }

    #[test]
    fn locations_resolve_like_a_browser() {
        let base = uri("https://api.example.com/a/b/c.png?x=1");
        assert_eq!(
            resolve_location(&base, "https://other.example.com/z")
                .unwrap()
                .to_string(),
            "https://other.example.com/z"
        );
        assert_eq!(
            resolve_location(&base, "//cdn.example.com/q")
                .unwrap()
                .to_string(),
            "https://cdn.example.com/q"
        );
        assert_eq!(
            resolve_location(&base, "/root").unwrap().to_string(),
            "https://api.example.com/root"
        );
        assert_eq!(
            resolve_location(&base, "d.png").unwrap().to_string(),
            "https://api.example.com/a/b/d.png"
        );
    }

    #[test]
    fn forbidden_and_invalid_headers_are_refused() {
        assert!(validated_headers(&[("Host".into(), "evil".into())]).is_err());
        assert!(validated_headers(&[("Content-Length".into(), "1".into())]).is_err());
        assert!(validated_headers(&[("X-Bad".into(), "a\r\nb".into())]).is_err());
        assert!(validated_headers(&[("bad name".into(), "a".into())]).is_err());
        assert_eq!(
            validated_headers(&[("Accept".into(), "x".into())]).unwrap()[0].0,
            "accept"
        );
    }

    #[test]
    fn a_forbidden_host_is_refused_before_any_connection() {
        let network = Network::new(
            HostPolicy::new(&["api.example.com"]),
            FetchLimits::default(),
            false,
        );
        let request = FetchRequest {
            method: "GET".into(),
            url: "https://evil.example.net/".into(),
            headers: vec![],
            body: None,
            timeout_ms: None,
        };

        let error = network
            .fetch(&request, Instant::now() + Duration::from_secs(5))
            .unwrap_err();

        assert!(
            matches!(error, FetchError::Refused(message) if message.contains("evil.example.net"))
        );
    }

    #[test]
    fn methods_outside_the_list_are_refused() {
        let network = Network::new(
            HostPolicy::new(&["api.example.com"]),
            FetchLimits::default(),
            false,
        );
        let request = FetchRequest {
            method: "CONNECT".into(),
            url: "https://api.example.com/".into(),
            headers: vec![],
            body: None,
            timeout_ms: None,
        };

        assert!(matches!(
            network.fetch(&request, Instant::now() + Duration::from_secs(5)),
            Err(FetchError::Refused(_))
        ));
    }
}
