//! A starting point for an EMMA plugin.
//!
//! A plugin answers three questions about a tree of nodes, and nothing else:
//!
//! * `meta`: what kinds of node do you produce, and how do they nest? (`describe`, `health`)
//! * `catalog`: give me nodes (`search`, `children`, `get`). Search, browsing, chapter lists, episode lists
//!   and track lists are all "give me nodes", so one shape serves every kind of media.
//! * `assets`: give me the bytes of a node for a role (`resolve`, `read`).
//!
//! EMMA owns everything else: the database, caching, retries, the network (`host::http_fetch`), settings and
//! progress. The sample below serves two made-up kinds from memory so it builds and runs with no network;
//! replace the data with calls to your source.
//!
//! Edit [`plugin.json`](../plugin.json) to match: its `kinds` must equal what `describe` returns.

mod bindings;

use bindings::emma::core::types::{ErrorCode, Node, NodePage, PageRequest, PluginError, Query};
use bindings::exports::emma::core::assets::{
    self, Asset, ByteRange, Chunk, Delivery, ResolveRequest,
};
use bindings::exports::emma::core::catalog;
use bindings::exports::emma::core::meta::{self, HealthReport, KindInfo, PluginInfo};

/// The plugin. EMMA instantiates it once and calls it through the three interfaces below.
struct Plugin;

/// The kind of a top-level node.
const COLLECTION: &str = "demo.collection";
/// The kind of a node inside a collection.
const ENTRY: &str = "demo.entry";

impl PluginError {
    /// A failure EMMA understands: the class says whether to retry, the message is shown to the user.
    fn new(code: ErrorCode, message: &str) -> Self {
        Self {
            code,
            message: message.into(),
            retry_after_ms: None,
        }
    }
}

impl Node {
    /// A node with no parent, thumbnail or props. Add them with struct update syntax or the builders below.
    fn new(id: &str, kind: &str, title: &str) -> Self {
        Self {
            id: id.into(),
            kind: kind.into(),
            title: title.into(),
            parent: None,
            thumbnail: None,
            props: Vec::new(),
        }
    }

    /// The same node, inside `parent`.
    fn within(self, parent: &str) -> Self {
        Self {
            parent: Some(parent.into()),
            ..self
        }
    }
}

fn collections() -> Vec<Node> {
    (1..=3)
        .map(|i| Node::new(&format!("c{i}"), COLLECTION, &format!("Collection {i}")))
        .collect()
}

fn entries(collection: &str) -> Vec<Node> {
    (1..=5)
        .map(|i| {
            Node::new(&format!("{collection}-e{i}"), ENTRY, &format!("Entry {i}"))
                .within(collection)
        })
        .collect()
}

/// Returns one page of `all`. The cursor is an offset here; it can be any string your source understands.
fn page(all: &[Node], request: &PageRequest) -> Result<NodePage, PluginError> {
    let start = match request.cursor.as_deref() {
        None => 0,
        Some(text) => text
            .parse::<usize>()
            .map_err(|_| PluginError::new(ErrorCode::InvalidArgument, "bad cursor"))?,
    };
    let size = request
        .limit
        .map_or(50, |limit| usize::try_from(limit).unwrap_or(500))
        .clamp(1, 500);
    let end = start.saturating_add(size).min(all.len());
    Ok(NodePage {
        items: all.get(start..end).unwrap_or_default().to_vec(),
        next_cursor: (end < all.len()).then(|| end.to_string()),
        total: u64::try_from(all.len()).ok(),
        facets: vec![],
    })
}

impl meta::Guest for Plugin {
    fn describe() -> Result<PluginInfo, PluginError> {
        Ok(PluginInfo {
            kinds: vec![
                KindInfo {
                    kind: COLLECTION.to_owned(),
                    label: "Collection".to_owned(),
                    children: vec![ENTRY.to_owned()],
                    roles: vec![],
                    profile: None,
                },
                KindInfo {
                    kind: ENTRY.to_owned(),
                    label: "Entry".to_owned(),
                    children: vec![],
                    roles: vec!["text".to_owned()],
                    profile: None,
                },
            ],
            filters: vec![],
            sorts: vec![],
            categories: vec![],
            settings: vec![],
        })
    }

    fn health() -> HealthReport {
        HealthReport {
            ok: true,
            detail: None,
        }
    }
}

impl catalog::Guest for Plugin {
    fn search(query: Query) -> Result<NodePage, PluginError> {
        let needle = query.text.trim().to_lowercase();
        let matches: Vec<Node> = collections()
            .into_iter()
            .filter(|n| n.title.to_lowercase().contains(&needle))
            .collect();
        page(
            &matches,
            &PageRequest {
                cursor: query.cursor,
                limit: query.limit,
            },
        )
    }

    fn children(parent: Option<String>, request: PageRequest) -> Result<NodePage, PluginError> {
        match parent.as_deref() {
            None => page(&collections(), &request),
            Some(id) if collections().iter().any(|c| c.id == id) => page(&entries(id), &request),
            Some(_) => Err(PluginError::new(ErrorCode::NotFound, "no such node")),
        }
    }

    fn get(ids: Vec<String>) -> Result<Vec<Node>, PluginError> {
        // Unknown ids are left out rather than failing the call.
        let mut known = collections();
        for collection in collections() {
            known.extend(entries(&collection.id));
        }
        Ok(ids
            .iter()
            .filter_map(|id| known.iter().find(|n| &n.id == id).cloned())
            .collect())
    }
}

impl assets::Guest for Plugin {
    fn resolve(request: ResolveRequest) -> Result<Vec<Asset>, PluginError> {
        if request.role != "text" || !request.node.contains("-e") {
            return Err(PluginError::new(
                ErrorCode::Unsupported,
                "nothing to resolve for that node and role",
            ));
        }
        // Small payloads can be returned inline. For anything big, return `Delivery::Remote` with a URL
        // (the host fetches it, under your manifest's network permissions) or `Delivery::Proxied` with a
        // token that `read` understands.
        let body = format!("The text of {}.", request.node).into_bytes();
        Ok(vec![Asset {
            mime: "text/plain".to_owned(),
            size: u64::try_from(body.len()).ok(),
            delivery: Delivery::Inline(body),
            props: vec![],
        }])
    }

    fn read(_token: String, _range: Option<ByteRange>) -> Result<Chunk, PluginError> {
        Err(PluginError::new(
            ErrorCode::Unsupported,
            "this plugin has no proxied assets",
        ))
    }
}

bindings::export!(Plugin with_types_in bindings);
