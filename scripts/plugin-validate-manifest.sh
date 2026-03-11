#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <manifest-path>" >&2
  exit 1
fi

MANIFEST_PATH="$1"
if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

python3 - "$MANIFEST_PATH" <<'PY'
import json
import pathlib
import sys
from urllib.parse import urlparse

manifest_path = pathlib.Path(sys.argv[1])
manifest = json.loads(manifest_path.read_text(encoding='utf-8'))

errors = []

def required_str(name):
    value = manifest.get(name)
    if not isinstance(value, str) or not value.strip():
        errors.append(f"Missing or invalid required string field: {name}")

required_str("id")
required_str("name")
required_str("version")
required_str("protocol")

protocol = manifest.get("protocol")
if isinstance(protocol, str) and protocol.strip().lower() not in {"grpc"}:
    errors.append("Only 'grpc' protocol is currently supported for packaged plugins.")

runtime = manifest.get("runtime")
wasm_bridge = (runtime or {}).get("wasmHostBridge") if isinstance(runtime, dict) else None
http_ops = (wasm_bridge or {}).get("http") if isinstance(wasm_bridge, dict) else None

permissions = manifest.get("permissions") if isinstance(manifest.get("permissions"), dict) else {}
domains = permissions.get("domains") if isinstance(permissions.get("domains"), list) else []

normalized_domains = []
for domain in domains:
    if not isinstance(domain, str):
        continue
    value = domain.strip().lower()
    if value:
        normalized_domains.append(value)

if http_ops:
    if not normalized_domains:
        errors.append("runtime.wasmHostBridge.http requires explicit permissions.domains allowlist.")
    if any(domain == "*" for domain in normalized_domains):
        errors.append("permissions.domains cannot contain wildcard '*' when wasmHostBridge.http is configured.")

    for op_name, op in http_ops.items():
        if not isinstance(op, dict):
            errors.append(f"runtime.wasmHostBridge.http.{op_name} must be an object.")
            continue

        url_template = op.get("urlTemplate")
        if not isinstance(url_template, str) or not url_template.strip():
            errors.append(f"runtime.wasmHostBridge.http.{op_name}.urlTemplate is required.")
            continue

        url_template = url_template.strip()
        if "://" in url_template:
            parsed = urlparse(url_template)
            if not parsed.scheme or not parsed.netloc:
                errors.append(f"runtime.wasmHostBridge.http.{op_name}.urlTemplate is not a valid absolute URL.")
            else:
                host = parsed.hostname.lower() if parsed.hostname else ""
                if host and normalized_domains:
                    allowed = any(host == domain or host.endswith("." + domain) for domain in normalized_domains)
                    if not allowed:
                        errors.append(
                            f"runtime.wasmHostBridge.http.{op_name}.urlTemplate host '{host}' is not covered by permissions.domains.")

signature = manifest.get("signature")
if signature is not None:
    if not isinstance(signature, dict):
        errors.append("signature must be an object when present.")
    else:
        algorithm = signature.get("algorithm")
        value = signature.get("value")
        if algorithm is not None and (not isinstance(algorithm, str) or not algorithm.strip()):
            errors.append("signature.algorithm must be a non-empty string when present.")
        if value is not None and (not isinstance(value, str) or not value.strip()):
            errors.append("signature.value must be a non-empty string when present.")

if errors:
    print(f"Manifest validation failed: {manifest_path}")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print(f"Manifest validation passed: {manifest_path}")
PY
