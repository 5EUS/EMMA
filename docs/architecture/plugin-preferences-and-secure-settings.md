# Plugin Preferences And Secure Settings

## Goal

Allow plugin manifests to declare persistent settings and credentials that:

- are stored by the EMMA core, not by plugin code or the UI
- have explicit types and validation rules
- can be rendered by emmaui without hardcoded plugin-specific forms
- support multiple protection levels for sensitive values
- can ship incrementally without blocking the non-secret settings path

This design treats the manifest-authored schema as signed plugin metadata and treats stored values as host-owned state.

## Design Summary

Add a new manifest section for host-managed preferences. The plugin manifest declares a bounded, typed schema. EMMA normalizes and validates that schema during manifest load, stores values in core-owned storage, and exposes two surfaces:

1. a read surface for emmaui to render forms and current value state
2. a mutation surface for setting, clearing, rotating, and validating values

Secret values are never persisted by emmaui and are never returned in cleartext after write unless the selected protection level explicitly allows it.

## Manifest Extension

Add a new top-level manifest section:

```json
{
  "id": "example.plugin",
  "name": "Example Plugin",
  "preferences": {
    "version": 1,
    "categories": [
      {
        "id": "account",
        "label": "Account",
        "description": "Credentials and account behavior"
      }
    ],
    "fields": [
      {
        "key": "account.username",
        "label": "Username",
        "type": "string",
        "category": "account",
        "required": true,
        "maxLength": 128,
        "defaultValue": ""
      },
      {
        "key": "account.password",
        "label": "Password",
        "type": "secret",
        "category": "account",
        "required": true,
        "secret": {
          "protection": "platform",
          "revealInUi": false,
          "allowCopy": false
        }
      },
      {
        "key": "search.safeMode",
        "label": "Safe mode",
        "type": "boolean",
        "defaultValue": true
      },
      {
        "key": "search.language",
        "label": "Preferred language",
        "type": "enum",
        "defaultValue": "en",
        "options": [
          { "value": "en", "label": "English" },
          { "value": "ja", "label": "Japanese" }
        ]
      }
    ]
  }
}
```

The schema is manifest-authored and therefore signed with the rest of the manifest. Plugins do not register settings dynamically at runtime.

## Supported Field Types

The host should only support bounded types that can be validated and rendered safely:

- `boolean`
- `int`
- `double`
- `string`
- `text`
- `enum`
- `multiEnum`
- `url`
- `duration`
- `json`
- `secret`

Notes:

- `json` is the escape hatch for relatively arbitrary values, but it must be schema-bounded by size and optional JSON-shape hints. It should not be the default for ordinary plugin settings.
- `secret` is a storage and UI contract, not just a text input hint.
- Nested arbitrary object graphs should not be allowed in the first implementation. Use flat keys such as `account.username` and `advanced.headers` instead.

## Field Metadata

Each field can carry host-renderable metadata:

- `key`: stable namespaced key, unique within the plugin
- `label`
- `description`
- `category`
- `type`
- `required`
- `defaultValue`
- `placeholder`
- `helpText`
- `options` for enum-like fields
- `min`, `max`, `step` for numeric fields
- `minLength`, `maxLength`, `pattern` for strings
- `visibleWhen` for simple host-evaluable conditional display rules
- `restartRequired`
- `sensitive`: true for values that should be redacted in logs and exports even if not stored as secrets
- `syncPolicy`: `device-local` or `exportable`
- `secret` object for secret-specific behavior

The host owns validation semantics. The UI should treat these as hints plus constraints, not as executable logic.

## Secret Protection Levels

Secrets need different storage behavior over time, and the host must be able to implement stronger modes later without changing the manifest contract.

Use a manifest-facing protection enum:

- `plain`
- `protected`
- `platform`
- `session`

Semantics:

- `plain`: stored in the core settings store like any other value. Allowed only for development and explicit low-risk cases. Should be rejected by policy for fields marked `secret` unless the host is in a relaxed development mode.
- `protected`: stored encrypted at rest by the core with a host-managed encryption key. If a platform secret store is available, the encryption key should be rooted there.
- `platform`: stored only in the OS secret store when available, such as Keychain on macOS, Credential Manager on Windows, or libsecret on Linux. The core stores only a reference and metadata.
- `session`: never written to disk. Lives only in the current runtime session and is cleared on restart.

Incremental delivery path:

1. Implement `boolean`, numeric, string, enum, and `secret` with `session` and `protected`.
2. Add `platform` adapters per supported OS.
3. Add policy controls that can forbid weaker storage levels for selected plugins or all production plugins.

## Safety Limits

Manifest-authored settings should be flexible, but bounded:

- maximum 128 fields per plugin in the initial implementation
- maximum key length 128
- keys restricted to lowercase dotted identifiers such as `account.password`
- maximum category count 16
- maximum enum options per field 100
- maximum string default length 4 KB
- maximum stored `text` or `json` value size 32 KB per field unless explicitly raised by host policy
- maximum total non-secret stored payload per plugin 256 KB in the initial implementation
- no runtime-provided scripts, templates, regexes with catastrophic complexity, or arbitrary UI widget injection
- `visibleWhen` must use a tiny declarative rule language evaluated entirely by the host

Host validation must reject manifests that exceed these limits or downgrade unsupported fields to an inert error state rather than letting the UI improvise.

## Core Storage Model

Stored values belong to the EMMA core, keyed by plugin ID and field key.

Use a new storage abstraction in EMMA.Application and back it in EMMA.Storage:

- `IPluginPreferenceStore`
- `PluginPreferenceDefinition`
- `PluginPreferenceValueRecord`
- `PluginPreferenceSecretHandle`

Suggested persistence split:

- non-secret values in SQLite
- secret references and metadata in SQLite
- secret material in the configured secret backend

Suggested SQLite record shape:

```text
plugin_preference_values
- plugin_id
- field_key
- field_type
- protection_level
- json_value
- secret_handle
- value_state
- schema_version
- updated_at_utc
- updated_by
```

`value_state` should distinguish:

- `unset`
- `set`
- `invalid`
- `unavailable`
- `requires-migration`

This lets the UI show a useful state for secrets without needing the cleartext value.

## Schema Lifecycle

Schema comes from the manifest and is re-derived on every manifest load or rescan.

Recommended reconciliation rules:

- if a field still exists with the same compatible type, keep the stored value
- if validation constraints tighten and the value no longer passes, mark it `invalid`
- if a field is removed, keep the stored value as orphaned data for one schema generation and then garbage-collect it on explicit cleanup
- if protection level is strengthened, migrate the stored value forward automatically when possible
- if protection level is weakened, do not downgrade automatically without an explicit host policy decision

The manifest should expose a `preferences.version` integer so the host can reason about migrations.

## Host API Surface

Add explicit host endpoints and library contracts rather than overloading the existing plugin summary payload.

### Read surfaces

`GET /plugins/{pluginId}/preferences/schema`

Returns:

- normalized categories
- normalized field definitions
- supported protection backends on the current platform
- current per-field value state
- cleartext values only for non-secret fields
- redacted state for secret fields

`GET /plugins/{pluginId}/preferences/summary`

Returns lightweight counts and flags for plugin lists:

- `hasPreferences`
- `requiredUnsetCount`
- `secretFieldCount`
- `invalidFieldCount`

### Mutation surfaces

`PUT /plugins/{pluginId}/preferences/values/{fieldKey}`

Behavior:

- validates the payload against the normalized schema
- writes through the core preference store
- applies the requested or manifest-default protection level if allowed
- returns updated value state and any warnings

`DELETE /plugins/{pluginId}/preferences/values/{fieldKey}`

Behavior:

- clears the value from the core store
- destroys secret material when applicable

`POST /plugins/{pluginId}/preferences/validate`

Behavior:

- evaluates all fields and returns field-level errors without mutating data

The Native and Flutter runtime bindings should wrap these as typed calls rather than leaving each UI caller to build raw JSON.

## UI Contract

emmaui should render plugin preferences entirely from the host-provided normalized schema.

The UI needs:

- plugin title and category metadata
- field order
- labels, descriptions, placeholders, and help text
- per-field type and widget hints
- constraints and enum options
- current effective value state
- whether the field is writable, secret, unset, invalid, or requires restart
- capability info about whether the requested protection level is supported on this device

UI rules:

- never persist plugin preference values locally outside the host APIs
- do not request current secret values after write unless the field explicitly allows reveal and the host policy allows it
- treat host validation as authoritative
- mask secret fields by default and prefer replace-not-read flows

The current plugin settings placeholder in emmaui is the correct landing zone for this screen.

## Plugin Runtime Access

Plugins need access to their configured values, but secret handling must stay host-controlled.

Recommended rule set:

- a plugin can access only its own settings
- non-secret values can be materialized into a typed settings object per request or per plugin session
- secret values should be fetched by the host and handed to the plugin only at the moment they are needed
- secret values must never appear in plugin logs, diagnostics, or generic capability payloads
- future SDK helpers should expose a typed settings accessor so plugin authors do not parse raw JSON manually

Add a host-side abstraction such as `IPluginSettingsResolver` that resolves the effective typed settings snapshot for a plugin invocation.

## Recommended Core Types

Add manifest-side records near the existing plugin manifest model:

- `PluginManifestPreferences`
- `PluginManifestPreferenceCategory`
- `PluginManifestPreferenceField`
- `PluginManifestPreferenceOption`
- `PluginManifestPreferenceSecretOptions`

Add normalized runtime-side records for host/UI use:

- `PluginPreferenceSchema`
- `PluginPreferenceFieldDefinition`
- `PluginPreferenceValueSnapshot`
- `PluginPreferenceMutationRequest`
- `PluginPreferenceMutationResult`

Keep manifest records close to manifest loading and keep normalized records close to host service boundaries.

## Security Rules

- Manifest schema is trusted only as signed metadata, not as executable UI code.
- All values are namespaced by plugin ID.
- Secret values are redacted in logs, traces, backups, and diagnostics by default.
- Export and restore flows must let operators exclude secret material entirely.
- Host policy may reject manifests that request unsupported or disallowed protection levels.
- Unsigned or locally-developed plugins should be able to use the feature only behind an explicit development policy gate for weak secret storage.
- Plugins must never be able to enumerate another plugin's fields or values.

## Implementation Plan

### Phase 1

- Add manifest records and JSON serialization support.
- Add manifest validation and normalization for preference schemas.
- Add SQLite-backed non-secret storage and `protected` secret storage with a host-managed encryption layer.
- Add host endpoints for schema read, summary read, set, clear, and validate.
- Wire emmaui to render a generated plugin preferences page from the normalized schema.

### Phase 2

- Add per-platform secret backends for `platform` storage.
- Add runtime access helpers for plugins and SDK-facing typed settings resolution.
- Add migration handling when manifests evolve.

### Phase 3

- Add backup and restore policy controls for exportable versus device-local fields.
- Add policy knobs for minimum required protection per plugin or publisher.
- Add audit events for secret rotation, clear, and validation failures.

## Concrete Repository Touch Points

The existing ownership seams already line up with this design:

- manifest records: `EMMA/src/EMMA.PluginHost/Plugins/PluginManifest.cs`
- manifest parsing: `EMMA/src/EMMA.PluginHost/Plugins/PluginManifestLoader.cs`
- manifest JSON context: `EMMA/src/EMMA.PluginHost/Plugins/PluginManifestJsonContext.cs`
- plugin summary and host HTTP surface: `EMMA/src/EMMA.PluginHost/Services/PluginHostEndpoints.cs`
- native host exports: `EMMA/src/EMMA.PluginHost.Library/PluginHostExports.cs`
- plugin settings placeholder UI: `emmaui/lib/screens/settings/plugins_settings_page.dart`

That means this can be implemented without introducing a second parallel settings system.

## Recommendation

Build this as a core-owned plugin preference subsystem, not as a UI persistence feature. The manifest should define a bounded schema, the host should normalize and store values, and the UI should become a pure renderer plus mutation client. That keeps security policy, storage behavior, secret handling, and migration rules in one place.