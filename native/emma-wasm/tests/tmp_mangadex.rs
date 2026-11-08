#![allow(missing_docs, clippy::pedantic)]
mod common;
use common::{Reply, TestServer, call, runtime};
use serde_json::{Value, json};

const WASM: &str = "/tmp/claude-1000/-home-zeus-Git-EMMA/72826495-3c1b-41a3-a021-13bf40b9a578/scratchpad/emma-test-plugin-rust/rust/target/wasm32-wasip2/release/emma_mangadex.wasm";

fn host_of(received: &common::Received) -> String { received.header("host").unwrap_or("localhost").to_owned() }

fn canned(received: &common::Received) -> Reply {
    let path = received.path.split('?').next().unwrap_or("");
    let mut reply = match path {
        "/ping" => Reply::ok("{\"result\":\"ok\"}"),
        "/manga" => Reply::ok(&json!({ "total": 41, "offset": 0, "data": [{
            "id": "m-1", "type": "manga",
            "attributes": { "title": { "en": "Berserk" }, "description": { "en": "Dark fantasy." }, "status": "ongoing", "year": 1989, "contentRating": "suggestive", "publicationDemographic": "seinen",
                "tags": [ { "attributes": { "name": { "en": "Action" } } } ] },
            "relationships": [ { "type": "author", "attributes": { "name": "Kentaro Miura" } }, { "type": "cover_art", "attributes": { "fileName": "abc.jpg" } } ] }] }).to_string()),
        "/manga/m-1/feed" => Reply::ok(&json!({ "total": 3, "offset": 0, "data": [
            { "id": "c-1", "attributes": { "chapter": "1", "title": "The Black Swordsman", "pages": 2, "publishAt": "2021-05-02T10:11:12+00:00", "translatedLanguage": "en" }, "relationships": [ { "type": "scanlation_group", "attributes": { "name": "Group A" } } ] },
            { "id": "c-2", "attributes": { "chapter": "2", "pages": 0 } },
            { "id": "c-3", "attributes": { "chapter": "3", "pages": 5 } } ] }).to_string()),
        "/at-home/server/c-1" => Reply::ok(&json!({ "baseUrl": format!("http://{}/img", host_of(received)), "chapter": { "hash": "h1", "data": ["1.png", "2.jpg"], "dataSaver": ["1.jpg"] } }).to_string()),
        "/statistics/manga" => Reply::ok(&json!({ "statistics": { "m-1": { "rating": { "bayesian": 8.7 }, "follows": 1200 } } }).to_string()),
        "/manga/missing/feed" => Reply::status(404),
        "/at-home/server/limited" => {
            let mut r = Reply::status(429);
            let at = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_secs() + 3;
            r.headers.push(("X-RateLimit-Retry-After".to_owned(), at.to_string()));
            r
        }
        _ => Reply::status(404),
    };
    reply.headers.push(("Content-Type".to_owned(), "application/json".to_owned()));
    reply
}

fn ok(e: &Value) -> &Value { assert!(e.get("error").is_none(), "{e}"); &e["ok"] }

#[test]
fn hermetic_full_flow() {
    let server = TestServer::start(canned);
    let runtime = runtime();
    let config: emma_wasm::PluginConfig = serde_json::from_value(json!({
        "componentPath": WASM, "limits": { "cpuBudgetMs": 2000, "memoryMb": 128 },
        "permissions": { "network": ["localhost"], "cache": true },
        "settings": { "advanced.apiBaseUrl": [format!("http://localhost:{}", server.port)], "search.defaultMaturity": ["safe", "suggestive"] },
        "testing": { "allowPrivateAddresses": true } })).unwrap();
    let plugin = runtime.open(&config).unwrap();
    assert!(plugin.has_search_assist());

    let health = call(&plugin, "health", &json!({})).0;
    assert_eq!(ok(&health)["ok"], true, "{health}");

    let info = call(&plugin, "describe", &json!({})).0;
    assert_eq!(ok(&info)["kinds"][0]["kind"], "manga");
    assert_eq!(ok(&info)["settings"].as_array().unwrap().len(), 7);

    let found = call(&plugin, "search", &json!({ "text": "berserk", "kinds": [], "filters": [{ "id": "status", "values": ["ongoing"] }], "sort": "followedCount", "cursor": null, "limit": 20 })).0;
    let manga = &ok(&found)["items"][0];
    assert_eq!((manga["id"].as_str(), manga["title"].as_str()), (Some("manga:m-1"), Some("Berserk")));
    assert_eq!(manga["thumbnail"], "https://uploads.mangadex.org/covers/m-1/abc.jpg.256.jpg");
    assert_eq!(ok(&found)["next_cursor"], "1");
    assert_eq!(ok(&found)["total"], 41);
    let search_request = server.requests().into_iter().find(|r| r.path.starts_with("/manga?")).unwrap();
    for needle in ["title=berserk", "status%5B%5D=ongoing", "contentRating%5B%5D=safe", "order%5BfollowedCount%5D=desc", "includes%5B%5D=cover_art"] {
        // brackets are sent literally by the plugin; accept either encoding
        let alt = needle.replace("%5B", "[").replace("%5D", "]");
        assert!(search_request.path.contains(needle) || search_request.path.contains(&alt), "{needle} in {}", search_request.path);
    }

    let chapters = call(&plugin, "children", &json!({ "parent": "manga:m-1", "request": { "cursor": null, "limit": null } })).0;
    let titles: Vec<&str> = ok(&chapters)["items"].as_array().unwrap().iter().map(|c| c["title"].as_str().unwrap()).collect();
    assert_eq!(titles, ["Chapter 1 — The Black Swordsman", "Chapter 3"], "chapters without pages are skipped");
    assert_eq!(ok(&chapters)["items"][0]["parent"], "manga:m-1");
    assert!(ok(&chapters)["next_cursor"].is_null());

    let pages = call(&plugin, "children", &json!({ "parent": "chapter:c-1", "request": { "cursor": null, "limit": null } })).0;
    assert_eq!(ok(&pages)["items"].as_array().unwrap().len(), 2);
    let page_id = ok(&pages)["items"][1]["id"].as_str().unwrap().to_owned();
    assert_eq!(page_id, "page:c-1:data:h1:2.jpg");

    let before = server.requests().len();
    let resolved = call(&plugin, "resolve", &json!({ "node": page_id, "role": "image", "hints": [] })).0;
    let asset = &ok(&resolved)[0];
    assert_eq!(asset["mime"], "image/jpeg");
    assert_eq!(asset["delivery"]["remote"]["url"], format!("http://localhost:{}/img/data/h1/2.jpg", server.port));
    assert_eq!(server.requests().len(), before, "the at-home details were cached by the earlier listing, so resolve makes no request");

    let suggestions = call(&plugin, "suggest", &json!({ "text": "ber", "limit": 5 })).0;
    assert_eq!(ok(&suggestions), &json!(["Berserk"]));
    let enriched = call(&plugin, "enrich", &json!({ "ids": ["manga:m-1"] })).0;
    assert_eq!(ok(&enriched)[0]["props"][0]["value"]["Number"], 8.7);

    let missing = call(&plugin, "children", &json!({ "parent": "manga:missing", "request": { "cursor": null, "limit": null } })).0;
    assert_eq!(missing["error"]["code"], "NotFound");
    let limited = call(&plugin, "children", &json!({ "parent": "chapter:limited", "request": { "cursor": null, "limit": null } })).0;
    assert_eq!(limited["error"]["code"], "RateLimited");
    let wait = limited["error"]["retry_after_ms"].as_u64().unwrap();
    assert!((1000..=4000).contains(&wait), "retry hint {wait}");
    let wrong = call(&plugin, "resolve", &json!({ "node": "manga:m-1", "role": "image", "hints": [] })).0;
    assert_eq!(wrong["error"]["code"], "Unsupported");
}

#[test]
#[ignore = "needs the live MangaDex API"]
fn live_mangadex() {
    let runtime = runtime();
    let config: emma_wasm::PluginConfig = serde_json::from_value(json!({
        "componentPath": WASM, "limits": { "cpuBudgetMs": 2000, "memoryMb": 128 },
        "permissions": { "network": ["api.mangadex.org", "uploads.mangadex.org", "*.mangadex.network"], "cache": true } })).unwrap();
    let plugin = runtime.open(&config).unwrap();

    println!("HEALTH {}", call(&plugin, "health", &json!({})).0);
    let found = call(&plugin, "search", &json!({ "text": "", "kinds": [], "filters": [], "sort": "followedCount", "cursor": null, "limit": 12 })).0;
    let items = ok(&found)["items"].as_array().unwrap();
    println!("POPULAR total={} first={}", ok(&found)["total"], items[0]["title"]);
    let mut shown = false;
    'outer: for manga in items {
        let manga_id = manga["id"].as_str().unwrap();
        let chapters = call(&plugin, "children", &json!({ "parent": manga_id, "request": { "cursor": null, "limit": 20 } })).0;
        let Some(list) = chapters["ok"]["items"].as_array() else { continue };
        for chapter in list.iter().take(6) {
            let chapter_id = chapter["id"].as_str().unwrap();
            let pages = call(&plugin, "children", &json!({ "parent": chapter_id, "request": { "cursor": null, "limit": 3 } })).0;
            if pages.get("error").is_some() { continue; }
            println!("MANGA {} | {} | thumb={}", manga["id"], manga["title"], manga["thumbnail"]);
            println!("CHAPTER {} | {}", chapter["id"], chapter["title"]);
            println!("PAGES total={} first={}", ok(&pages)["total"], ok(&pages)["items"][0]["id"]);
            let page = ok(&pages)["items"][0]["id"].as_str().unwrap().to_owned();
            let resolved = call(&plugin, "resolve", &json!({ "node": page, "role": "image", "hints": [] })).0;
            println!("ASSET {}", ok(&resolved)[0]);
            let enriched = call(&plugin, "enrich", &json!({ "ids": [manga_id] })).0;
            println!("ENRICH {}", ok(&enriched)[0]["props"]);
            let suggestions = call(&plugin, "suggest", &json!({ "text": "naruto", "limit": 3 })).0;
            println!("SUGGEST {}", ok(&suggestions));
            shown = true;
            break 'outer;
        }
    }
    assert!(shown, "no readable chapter found among the popular manga");
}
