# EMMA Plugin Template

Minimal starter plugin with transport wiring in place and domain behavior intentionally stubbed.

## Run

```bash
dotnet run --project EMMA.PluginTemplate.csproj
```

Default port is 5000 (via template defaults). Override with:

```bash
dotnet run --project EMMA.PluginTemplate.csproj -- --port 6001
```

or

```bash
EMMA_PLUGIN_PORT=6001 dotnet run --project EMMA.PluginTemplate.csproj
```

## Build and pack

From repo root:

```bash
./scripts/build-pack-plugin.sh ./EMMA.PluginTemplate.plugin.json
```

WASM package variant:

```bash
TARGETS="wasm" ./scripts/build-pack-plugin.sh ./EMMA.PluginTemplate.plugin.json
```

ASP.NET plugin package variant (example Linux x64):

```bash
TARGETS="linux-x64" ./scripts/build-pack-plugin-aspnet.sh ./EMMA.PluginTemplate.plugin.json
```

## Signing and CI setup guide

This template uses delegated RSA signing for plugin manifests.

Signing happens during packaging via scripts/sign-plugin.sh and writes signature metadata into packaged manifest JSON.

### Required signing fields

Set these values when signing:

- EMMA_PLUGIN_SIGNING_KEY_ID
- EMMA_PLUGIN_REPOSITORY_ID
- One of:
	- EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64
	- EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM

Optional:

- EMMA_PLUGIN_SIGNATURE_ISSUED_AT_UTC
- EMMA_PLUGIN_SIGNATURE_EXPIRES_AT_UTC

### Local development setup

1. Generate delegated key pair:

```bash
./scripts/generate-hmac-key.sh ./.keys my-plugin-release-2026-q2
```

2. Create local env file from example:

```bash
cp .signing.env.example .signing.env
source .signing.env
```

3. Build signed package:

```bash
EMMA_REQUIRE_SIGNED_PLUGINS=1 TARGETS="wasm" ./scripts/build-pack-plugin.sh ./EMMA.PluginTemplate.plugin.json
```

The following local secret files are ignored by default:

- .keys/
- *.private.pem
- .signing.env

### CI setup (GitHub Actions)

For compatibility with existing workflows, CI still uses secret name EMMA_HMAC_KEY_BASE64.

Set that secret to delegated private key PEM content, either:

- One-line base64 PEM (recommended)
- Raw PEM block

Recommended secret generation:

```bash
base64 -w 0 /absolute/path/to/delegated.private.pem
```

Paste exact output as secret value (no wrapping quotes).

### How packaging scripts consume signing config

- scripts/build-pack-plugin.sh and scripts/build-pack-plugin-aspnet.sh call scripts/sign-plugin.sh
- scripts/sign-plugin.sh accepts:
	- Raw PEM
	- PEM with escaped newlines
	- Base64 PEM
- Script validates key parse with openssl before signing

### Repository trust requirements

Manifest signatures are only accepted at runtime when the host trust bundle contains:

1. Root public key file referenced by rootKeyId
2. Repository delegation metadata file signed by that root key
3. Delegation entry authorizing plugin ID scope for the signing keyId

Coordinate with repository operators to keep delegation metadata and root trust files current.

### Rotation and revocation

Delegated key rotation:

1. Add new delegated key entry to repository delegations.
2. Re-sign delegations with root private key.
3. Update CI secret to new delegated private key.
4. Publish release signed with new keyId.
5. Retire old key.

Emergency response for key compromise:

1. Mark compromised delegated key as revoked.
2. Publish updated signed delegations metadata.
3. Rotate CI secret immediately.
4. Re-release plugin with replacement key.

### Common failure troubleshooting

If CI says base64: invalid input:

- Secret is not valid base64 and also not parseable PEM.
- Recreate secret from delegated private key with base64 -w 0.

If CI says Could not read private key:

- Secret likely contains wrong key type, encrypted key, or malformed formatting.
- Ensure it is an unencrypted PEM private key.

If runtime rejects signature:

- keyId/repositoryId mismatch, missing trust root, stale delegations, or scope mismatch.
- Verify host has up-to-date root and delegation files.

## What You Must Customize

1. Implement domain behavior in `Infrastructure/CoreClient.cs`.
2. Fill in TODOs in `Infrastructure/PluginTemplateHooks.cs` (search, chapters, streams, segment).
3. Implement provider URL strategy in `Infrastructure/PluginTemplateHooks.cs` if network-backed.
4. Implement payload fetch logic in `Infrastructure/WasmClient.cs` when needed.
5. Update plugin metadata and permissions in `EMMA.PluginTemplate.plugin.json`.
6. Rename files/types/namespace after scaffolding to your plugin identity.

## Notes

This template intentionally returns minimal/default values until you add your own logic.

## Suggested First Test

1. Build and run plugin.
2. Verify handshake/capabilities operations respond.
3. Add one hardcoded search item in `CoreClient.Search`.
4. Add one stream in `CoreClient.GetStreams`.
5. Iterate from there with real provider integration.
