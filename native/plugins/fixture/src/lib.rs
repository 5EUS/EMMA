//! A deterministic plugin for testing the EMMA Wasm host. What it does is chosen by the search
//! text or the node id, so one component covers every host behaviour: crashes, runaway CPU, memory
//! exhaustion, logging, network calls, the cache, settings and every error class. It also serves
//! the same contract as a manga series, a recipe and proxied assets, to show the core has no
//! media types in it.

#[path = "bindings.generated.rs"]
mod bindings;

use bindings::emma::core::host;
use bindings::emma::core::types::{
    Choice, ErrorCode, FilterSpec, FilterType, Header, LookupSpec, Node, NodePage, PageRequest,
    PluginError, Prop, PropValue, Query,
};
use bindings::exports::emma::core::assets::{
    self, Asset, ByteRange, Chunk, Delivery, Remote, ResolveRequest,
};
use bindings::exports::emma::core::catalog;
use bindings::exports::emma::core::meta::{
    self, HealthReport, KindInfo, PluginInfo, SettingCategory, SettingCondition, SettingSpec,
    SettingType,
};

struct Fixture;

fn error(code: ErrorCode, message: &str) -> PluginError {
    PluginError {
        code,
        message: message.to_owned(),
        retry_after_ms: None,
    }
}

fn node(id: &str, kind: &str, title: &str, parent: Option<&str>) -> Node {
    Node {
        id: id.to_owned(),
        kind: kind.to_owned(),
        title: title.to_owned(),
        parent: parent.map(str::to_owned),
        thumbnail: None,
        props: vec![],
    }
}

fn text(key: &str, value: &str) -> Prop {
    Prop {
        key: key.to_owned(),
        value: PropValue::Text(value.to_owned()),
    }
}

fn kind(kind: &str, label: &str, children: &[&str], roles: &[&str]) -> KindInfo {
    KindInfo {
        kind: kind.to_owned(),
        label: label.to_owned(),
        children: children.iter().map(|c| (*c).to_owned()).collect(),
        roles: roles.iter().map(|r| (*r).to_owned()).collect(),
        profile: None,
        max_age_ms: None,
    }
}

/// A kind whose stored details and listings the host may serve for only `max_age_ms`.
fn aging(max_age_ms: u32, kind: KindInfo) -> KindInfo {
    KindInfo {
        max_age_ms: Some(max_age_ms),
        ..kind
    }
}

/// A kind that follows a published client profile, such as `emma.profile.video@1`.
fn profiled(profile: &str, kind: KindInfo) -> KindInfo {
    KindInfo {
        profile: Some(profile.to_owned()),
        ..kind
    }
}

fn whole(key: &str, value: i64) -> Prop {
    Prop {
        key: key.to_owned(),
        value: PropValue::Integer(value),
    }
}

fn flag(key: &str, value: bool) -> Prop {
    Prop {
        key: key.to_owned(),
        value: PropValue::Flag(value),
    }
}

/// An asset the host fetches from the fixture's media host, with the headers its plugin needs.
fn media(mime: &str, path: &str, props: Vec<Prop>) -> Asset {
    Asset {
        mime: mime.to_owned(),
        delivery: Delivery::Remote(Remote {
            url: format!("https://files.example.com/{path}"),
            headers: vec![Header {
                name: "referer".to_owned(),
                value: "https://example.com/".to_owned(),
            }],
            expires_ms: None,
        }),
        size: None,
        props,
    }
}

/// A chapter marker: an asset with nothing to fetch, only where it starts and what it is called.
fn marker(start_ms: i64, end_ms: Option<i64>, title: &str) -> Asset {
    let mut props = vec![whole("emma.start-ms", start_ms), text("emma.label", title)];
    props.extend(end_ms.map(|end| whole("emma.end-ms", end)));
    Asset {
        mime: "text/plain".to_owned(),
        delivery: Delivery::Inline(Vec::new()),
        size: None,
        props,
    }
}

fn setting(key: &str, label: &str, setting_type: SettingType) -> SettingSpec {
    SettingSpec {
        key: key.to_owned(),
        label: label.to_owned(),
        description: None,
        help: None,
        setting_type,
        category: None,
        required: false,
        default_values: vec![],
        choices: vec![],
        placeholder: None,
        max_length: None,
        visible_when: None,
    }
}

/// Pages a list by a numeric cursor, to exercise cursor handling.
fn paged(all: &[Node], cursor: Option<&str>, limit: Option<u32>) -> Result<NodePage, PluginError> {
    let start = match cursor {
        None => 0,
        Some(text) => text
            .parse::<usize>()
            .map_err(|_| error(ErrorCode::InvalidArgument, "bad cursor"))?,
    };
    let size = limit
        .map_or(50, |l| usize::try_from(l).unwrap_or(500))
        .clamp(1, 500);
    let end = (start + size).min(all.len());
    let total = u64::try_from(all.len()).unwrap_or(u64::MAX);
    let items = all.get(start..end).unwrap_or_default().to_vec();
    Ok(NodePage {
        items,
        next_cursor: (end < all.len()).then(|| end.to_string()),
        total: Some(total),
        facets: vec![],
    })
}

fn series_nodes() -> Vec<Node> {
    (1..=3)
        .map(|i| {
            let mut n = node(&format!("s{i}"), "series", &format!("Series {i}"), None);
            n.props = vec![
                text("emma.synopsis", "A series."),
                Prop {
                    key: "emma.rating".to_owned(),
                    value: PropValue::Number(7.5),
                },
            ];
            n
        })
        .collect()
}

fn recipe_nodes() -> Vec<Node> {
    vec![
        node("r1", "recipe", "Lasagna", None),
        node("r2", "recipe", "Pancakes", None),
    ]
}

fn episode_nodes() -> Vec<Node> {
    vec![node("e1", "episode", "Episode 1", None)]
}

fn track_nodes() -> Vec<Node> {
    vec![node("t1", "track", "Track 1", None)]
}

fn probe(title: &str, props: Vec<Prop>) -> NodePage {
    let mut n = node("probe", "probe", title, None);
    n.props = props;
    NodePage {
        items: vec![n],
        next_cursor: None,
        total: None,
        facets: vec![],
    }
}

fn run_probe(command: &str) -> Result<NodePage, PluginError> {
    if let Some(url) = command.strip_prefix("http:") {
        let response = host::http_fetch(&host::HttpRequest {
            method: "GET".to_owned(),
            url: url.to_owned(),
            headers: vec![Header {
                name: "x-probe".to_owned(),
                value: "1".to_owned(),
            }],
            body: None,
            timeout_ms: Some(5000),
        })?;
        let body = String::from_utf8_lossy(&response.body).into_owned();
        return Ok(probe(
            &format!("status={} bytes={}", response.status, response.body.len()),
            vec![text("body", &body)],
        ));
    }
    if let Some(key) = command.strip_prefix("setting:") {
        let values = host::setting_values(key);
        let first = host::setting(key);
        return Ok(probe(
            first.as_deref().unwrap_or("<none>"),
            vec![text("values", &values.join("|"))],
        ));
    }
    if let Some(code) = command.strip_prefix("error:") {
        let code = match code {
            "not-found" => ErrorCode::NotFound,
            "unauthorized" => ErrorCode::Unauthorized,
            "rate-limited" => {
                return Err(PluginError {
                    code: ErrorCode::RateLimited,
                    message: "slow down".to_owned(),
                    retry_after_ms: Some(1500),
                });
            }
            "unavailable" => ErrorCode::Unavailable,
            "invalid-argument" => ErrorCode::InvalidArgument,
            "unsupported" => ErrorCode::Unsupported,
            _ => ErrorCode::Internal,
        };
        return Err(error(code, "requested failure"));
    }
    match command {
        "trap" => panic!("fixture panic on purpose"),
        "spin" => {
            let mut counter = 0_u64;
            loop {
                counter = std::hint::black_box(counter.wrapping_add(1));
            }
        }
        "alloc" => {
            let mut memory = vec![0_u8; 256 * 1024 * 1024];
            for byte in memory.iter_mut().step_by(4096) {
                *byte = 1;
            }
            Ok(probe(
                &format!("allocated {}", std::hint::black_box(memory.len())),
                vec![],
            ))
        }
        "log" => {
            host::log(host::LogLevel::Debug, "debug line");
            host::log(host::LogLevel::Info, "info line");
            host::log(host::LogLevel::Error, "error line");
            Ok(probe("logged", vec![]))
        }
        "cache" => {
            let before = host::cache_get("k");
            host::cache_put("k", b"stored", 60_000);
            Ok(probe(if before.is_some() { "hit" } else { "miss" }, vec![]))
        }
        "now" => Ok(probe(&host::now_ms().to_string(), vec![])),
        "bigpage" => Ok(NodePage {
            items: (0..600)
                .map(|i| node(&format!("n{i}"), "series", "x", None))
                .collect(),
            next_cursor: None,
            total: None,
            facets: vec![],
        }),
        "undeclared" => Ok(NodePage {
            items: vec![node("u1", "mystery", "undeclared kind", None)],
            next_cursor: None,
            total: None,
            facets: vec![],
        }),
        _ => Ok(probe("unknown probe", vec![])),
    }
}

/// The filters the fixture declares: a choice filter and a lookup filter, both with a role.
fn filters() -> Vec<FilterSpec> {
    vec![
        FilterSpec {
            id: "status".to_owned(),
            label: "Status".to_owned(),
            filter_type: FilterType::SingleChoice,
            kinds: vec![],
            required: false,
            hidden: false,
            group: None,
            description: None,
            choices: vec![Choice {
                value: "ongoing".to_owned(),
                label: "Ongoing".to_owned(),
                description: None,
                is_default: false,
            }],
            range: None,
            lookup: None,
            role: Some("emma.filter.status".to_owned()),
        },
        FilterSpec {
            id: "tag".to_owned(),
            label: "Tag".to_owned(),
            filter_type: FilterType::Lookup,
            kinds: vec![],
            required: false,
            hidden: false,
            group: None,
            description: None,
            choices: vec![],
            range: None,
            lookup: Some(LookupSpec {
                min_query_length: 2,
                max_results: 5,
                debounce_ms: 250,
                allow_custom_values: false,
            }),
            role: Some("emma.filter.tag".to_owned()),
        },
    ]
}

impl meta::Guest for Fixture {
    fn describe() -> Result<PluginInfo, PluginError> {
        Ok(PluginInfo {
            kinds: vec![
                kind("series", "Series", &["chapter"], &[]),
                kind("chapter", "Chapter", &["page"], &[]),
                kind("page", "Page", &[], &["image"]),
                aging(
                    3_600_000,
                    kind("recipe", "Recipe", &["recipe.step"], &["image"]),
                ),
                kind("recipe.step", "Step", &[], &["text", "image"]),
                kind("probe", "Probe", &[], &["text", "image", "blob"]),
                profiled(
                    "emma.profile.video@1",
                    kind(
                        "episode",
                        "Episode",
                        &[],
                        &["stream", "subtitle", "audio", "chapters"],
                    ),
                ),
                profiled(
                    "emma.profile.audio@1",
                    kind("track", "Track", &[], &["audio", "chapters"]),
                ),
            ],
            filters: filters(),
            sorts: vec![Choice {
                value: "recent".to_owned(),
                label: "Recent".to_owned(),
                description: None,
                is_default: true,
            }],
            categories: vec![SettingCategory {
                id: "connection".to_owned(),
                label: "Connection".to_owned(),
                description: None,
            }],
            settings: vec![
                SettingSpec {
                    category: Some("connection".to_owned()),
                    max_length: Some(120),
                    ..setting("connection.user", "Account name", SettingType::Text)
                },
                SettingSpec {
                    category: Some("connection".to_owned()),
                    required: true,
                    ..setting("connection.token", "API token", SettingType::Secret)
                },
                SettingSpec {
                    default_values: vec!["en".to_owned()],
                    choices: vec![
                        Choice {
                            value: "en".to_owned(),
                            label: "English".to_owned(),
                            description: None,
                            is_default: true,
                        },
                        Choice {
                            value: "ja".to_owned(),
                            label: "Japanese".to_owned(),
                            description: None,
                            is_default: false,
                        },
                    ],
                    ..setting("search.language", "Language", SettingType::Choice)
                },
                SettingSpec {
                    default_values: vec!["false".to_owned()],
                    ..setting("advanced.verbose", "Verbose", SettingType::Boolean)
                },
                SettingSpec {
                    default_values: vec!["15000".to_owned()],
                    visible_when: Some(SettingCondition {
                        key: "advanced.verbose".to_owned(),
                        equals: "true".to_owned(),
                    }),
                    ..setting("advanced.timeout", "Timeout", SettingType::Duration)
                },
            ],
        })
    }

    fn health() -> HealthReport {
        HealthReport {
            ok: true,
            detail: None,
        }
    }
}

impl catalog::Guest for Fixture {
    fn search(query: Query) -> Result<NodePage, PluginError> {
        let text = query.text.trim();
        if text.is_empty() || text.eq_ignore_ascii_case("series") {
            return paged(&series_nodes(), query.cursor.as_deref(), query.limit);
        }
        if text.eq_ignore_ascii_case("recipe") {
            return paged(&recipe_nodes(), query.cursor.as_deref(), query.limit);
        }
        if text.eq_ignore_ascii_case("episode") {
            return paged(&episode_nodes(), query.cursor.as_deref(), query.limit);
        }
        if text.eq_ignore_ascii_case("track") {
            return paged(&track_nodes(), query.cursor.as_deref(), query.limit);
        }
        run_probe(text)
    }

    fn children(parent: Option<String>, request: PageRequest) -> Result<NodePage, PluginError> {
        match parent.as_deref() {
            None => paged(&series_nodes(), request.cursor.as_deref(), request.limit),
            Some("s1") => paged(
                &(1..=120)
                    .map(|i| {
                        node(
                            &format!("s1-c{i}"),
                            "chapter",
                            &format!("Chapter {i}"),
                            Some("s1"),
                        )
                    })
                    .collect::<Vec<_>>(),
                request.cursor.as_deref(),
                request.limit,
            ),
            Some(id) if id.contains("-c") => paged(
                &(1..=5)
                    .map(|i| {
                        node(
                            &format!("{id}-p{i}"),
                            "page",
                            &format!("Page {i}"),
                            Some(id),
                        )
                    })
                    .collect::<Vec<_>>(),
                request.cursor.as_deref(),
                request.limit,
            ),
            Some("r1") => paged(
                &[
                    node("r1-s1", "recipe.step", "Make the sauce", Some("r1")),
                    node("r1-s2", "recipe.step", "Layer it", Some("r1")),
                ],
                request.cursor.as_deref(),
                request.limit,
            ),
            Some(other) => Err(error(ErrorCode::NotFound, &format!("no node {other}"))),
        }
    }

    fn get(ids: Vec<String>) -> Result<Vec<Node>, PluginError> {
        let known: Vec<Node> = series_nodes()
            .into_iter()
            .chain(recipe_nodes())
            .chain(episode_nodes())
            .chain(track_nodes())
            .collect();
        Ok(ids
            .iter()
            .filter_map(|id| known.iter().find(|n| &n.id == id).cloned())
            .collect())
    }
}

fn pattern(length: usize) -> Vec<u8> {
    (0..length)
        .map(|i| u8::try_from(i % 251).unwrap_or(0))
        .collect()
}

impl assets::Guest for Fixture {
    fn resolve(request: ResolveRequest) -> Result<Vec<Asset>, PluginError> {
        match (request.node.as_str(), request.role.as_str()) {
            ("inline1", "text") => Ok(vec![Asset {
                mime: "text/plain".to_owned(),
                delivery: Delivery::Inline(b"hello".to_vec()),
                size: Some(5),
                props: vec![],
            }]),
            ("proxied1", "image") => Ok(vec![Asset {
                mime: "application/octet-stream".to_owned(),
                delivery: Delivery::Proxied("tok-1".to_owned()),
                size: Some(1000),
                props: vec![],
            }]),
            ("e1", "stream") => Ok(vec![
                media(
                    "application/vnd.apple.mpegurl",
                    "e1/master.m3u8",
                    vec![
                        text("emma.quality", "1080p"),
                        whole("emma.width", 1920),
                        whole("emma.height", 1080),
                        whole("emma.bitrate", 5_000_000),
                        whole("emma.duration-ms", 1_440_000),
                    ],
                ),
                media(
                    "video/mp4",
                    "e1.mp4",
                    vec![
                        text("emma.quality", "480p"),
                        flag("emma.default", true),
                        text("emma.drm.scheme", "none"),
                    ],
                ),
            ]),
            ("e1", "subtitle") => Ok(vec![
                media(
                    "text/vtt",
                    "e1/en.vtt",
                    vec![
                        text("emma.language", "en"),
                        text("emma.label", "English"),
                        flag("emma.default", true),
                    ],
                ),
                media("text/vtt", "e1/ja.vtt", vec![text("emma.language", "ja")]),
            ]),
            ("e1", "audio") => Ok(vec![media(
                "audio/mp4",
                "e1/ja.m4a",
                vec![text("emma.language", "ja"), text("emma.label", "Japanese")],
            )]),
            ("e1", "chapters") => Ok(vec![
                marker(0, Some(90_000), "Opening"),
                marker(90_000, None, "Part one"),
            ]),
            ("t1", "audio") => Ok(vec![Asset {
                mime: "audio/mpeg".to_owned(),
                delivery: Delivery::Proxied("tok-1".to_owned()),
                size: Some(1000),
                props: vec![whole("emma.duration-ms", 200_000)],
            }]),
            ("t1", "chapters") => Ok(vec![
                marker(0, Some(100_000), "Side A"),
                marker(100_000, Some(200_000), "Side B"),
            ]),
            ("probe", "blob") => Ok(vec![Asset {
                mime: "application/octet-stream".to_owned(),
                delivery: Delivery::Proxied("big".to_owned()),
                size: Some(5 * 1024 * 1024),
                props: vec![],
            }]),
            (id, "image") if id.contains("-p") => Ok(vec![Asset {
                mime: "image/png".to_owned(),
                delivery: Delivery::Remote(Remote {
                    url: format!("https://files.example.com/{id}.png"),
                    headers: vec![Header {
                        name: "referer".to_owned(),
                        value: "https://example.com/".to_owned(),
                    }],
                    expires_ms: None,
                }),
                size: None,
                props: vec![Prop {
                    key: "emma.quality".to_owned(),
                    value: PropValue::Text("original".to_owned()),
                }],
            }]),
            _ => Err(error(
                ErrorCode::Unsupported,
                "nothing to resolve for that node and role",
            )),
        }
    }

    fn read(token: String, range: Option<ByteRange>) -> Result<Chunk, PluginError> {
        let data = match token.as_str() {
            "tok-1" => pattern(1000),
            "big" => pattern(5 * 1024 * 1024),
            _ => return Err(error(ErrorCode::NotFound, "unknown token")),
        };
        let total = data.len();
        let to_usize = |value: u64| usize::try_from(value).unwrap_or(usize::MAX);
        let (start, length) = range.map_or((0, total), |r| {
            (to_usize(r.start), r.length.map_or(total, to_usize))
        });
        let end = start.saturating_add(length).min(total);
        let bytes = data.get(start.min(total)..end).unwrap_or_default().to_vec();
        Ok(Chunk {
            bytes,
            total: Some(u64::try_from(total).unwrap_or(u64::MAX)),
        })
    }
}

#[cfg(any(feature = "assist", feature = "newer-patch"))]
impl bindings::exports::emma::core::search_assist::Guest for Fixture {
    fn suggest(filter_id: String, text: String, limit: u32) -> Result<Vec<Choice>, PluginError> {
        Ok((1..=limit.min(5))
            .map(|i| Choice {
                value: format!("{filter_id}:{text}-{i}"),
                label: format!("{text} {i}"),
                description: None,
                is_default: false,
            })
            .collect())
    }

    fn enrich(ids: Vec<String>) -> Result<Vec<Node>, PluginError> {
        Ok(ids
            .into_iter()
            .map(|id| {
                let mut n = node(&id, "series", &format!("Enriched {id}"), None);
                n.props = vec![Prop {
                    key: "emma.rating".to_owned(),
                    value: PropValue::Number(9.0),
                }];
                n
            })
            .collect())
    }
}

bindings::export!(Fixture with_types_in bindings);
