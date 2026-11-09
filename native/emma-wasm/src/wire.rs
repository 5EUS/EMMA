//! The JSON the host exchanges with the .NET side. Records that carry bytes get explicit wire
//! shapes so bytes travel as base64 (or a separate binary buffer), not as JSON arrays of numbers.

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use serde::{Deserialize, Serialize};

use crate::generated::contract::emma::core::types;
use crate::generated::contract::exports::emma::core::assets;
use crate::host::LogEntry;

/// An asset as it crosses to the .NET side.
#[derive(Debug, Serialize)]
pub struct WireAsset {
    /// The payload type.
    pub mime: String,
    /// How the bytes are delivered.
    pub delivery: WireDelivery,
    /// The size in bytes, when known.
    pub size: Option<u64>,
    /// Typed facts about the asset.
    pub props: Vec<types::Prop>,
}

/// How an asset's bytes are delivered, with inline bytes as base64.
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum WireDelivery {
    /// Small payloads returned directly.
    Inline {
        /// The bytes, base64.
        base64: String,
    },
    /// The host fetches the URL.
    Remote {
        /// The absolute URL.
        url: String,
        /// Request headers the fetch must carry.
        headers: Vec<types::Header>,
        /// When the URL stops working, ms since the Unix epoch.
        expires_ms: Option<i64>,
    },
    /// Only the plugin can read it, through this token.
    Proxied {
        /// The opaque token.
        token: String,
    },
}

impl From<assets::Asset> for WireAsset {
    fn from(asset: assets::Asset) -> Self {
        let delivery = match asset.delivery {
            assets::Delivery::Inline(bytes) => WireDelivery::Inline {
                base64: STANDARD.encode(bytes),
            },
            assets::Delivery::Remote(remote) => WireDelivery::Remote {
                url: remote.url,
                headers: remote.headers,
                expires_ms: remote.expires_ms,
            },
            assets::Delivery::Proxied(token) => WireDelivery::Proxied { token },
        };
        Self {
            mime: asset.mime,
            delivery,
            size: asset.size,
            props: asset.props,
        }
    }
}

/// The request body of `read`.
#[derive(Debug, Deserialize)]
pub struct ReadRequest {
    /// The proxied asset's token.
    pub token: String,
    /// The range to read, or everything.
    pub range: Option<assets::ByteRange>,
}

/// The request body of `children`.
#[derive(Debug, Deserialize)]
pub struct ChildrenRequest {
    /// The plugin-local parent id, or `None` for the browse root.
    pub parent: Option<String>,
    /// Cursor and limit.
    pub request: types::PageRequest,
}

/// The request body of `get` and `enrich`.
#[derive(Debug, Deserialize)]
pub struct IdsRequest {
    /// Plugin-local node ids.
    pub ids: Vec<String>,
}

/// The request body of `suggest`.
#[derive(Debug, Deserialize)]
pub struct SuggestRequest {
    /// The id of the `lookup` filter being completed.
    pub filter_id: String,
    /// The partially typed text.
    pub text: String,
    /// The most suggestions wanted.
    pub limit: u32,
}

/// What the host returns for every call: a result or an error, the plugin's logs, and whether the
/// plugin trapped (crashed or was stopped), so the .NET side can count failures.
#[derive(Debug, Serialize)]
pub struct Envelope {
    /// The result, when the call succeeded.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ok: Option<serde_json::Value>,
    /// The failure, when it did not.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<types::PluginError>,
    /// Whether the plugin trapped.
    pub trapped: bool,
    /// What the plugin logged during the call.
    pub logs: Vec<LogEntry>,
}
