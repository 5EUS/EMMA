# EMMA.Native

Native C ABI surface for the embedded EMMA runtime. This project is intended to be built with NativeAOT and consumed via FFI (for example, Flutter `dart:ffi`).

## Implemented exports

All strings are UTF-8. Any non-null string returned from EMMA must be freed with `emma_string_free`.

- `int emma_runtime_start()`
  - Starts an embedded runtime and returns a handle. Returns `0` on failure.
- `void emma_runtime_stop(int handle)`
  - Stops and releases a runtime handle.
- `int emma_runtime_status(int handle)`
  - Returns `1` if the handle is active, otherwise `0`.
- `const char* emma_runtime_list_media_json(int handle)`
  - Returns JSON array of media summaries. `null` on error.
- `const char* emma_runtime_list_plugins_json()`
  - Returns JSON array of plugins. Currently returns `[]`.
- `int emma_runtime_open_plugin(int handle, const char* pluginIdUtf8)`
  - Returns `1` on success, `0` on failure. Currently unimplemented.
- `const char* emma_last_error()`
  - Returns the last error string for the calling thread. `null` if none.
- `void emma_string_free(void* value)`
  - Frees strings allocated by EMMA.

## NativeAOT publish recipe

Build the native library with NativeAOT and a stable output name. Output is placed under `artifacts/aot/EMMA.Native/<rid>`.

Scripted:

```bash
./scripts/publish-native-aot.sh osx-arm64
```

Manual:

```bash
dotnet publish src/EMMA.Native/EMMA.Native.csproj \
  -c Release \
  -r osx-arm64 \
  -p:PublishAot=true \
  -p:SelfContained=true \
  -p:NativeLib=Shared \
  -p:NativeLibName=emma_native \
  -o artifacts/aot/EMMA.Native/osx-arm64
```

### Output naming by platform

- macOS (osx-arm64, osx-x64): `libemma_native.dylib`
- Windows (win-x64): `emma_native.dll`
- Linux (linux-x64): `libemma_native.so`
- Android (android-arm64, android-x64): `libemma_native.so`
- iOS device (ios-arm64): `libemma_native.a` (static)
- iOS simulator (iossimulator-arm64, iossimulator-x64): `libemma_native.a` (static)

The publish script copies the default NativeAOT output to the names above if needed.

### Recommended RIDs

- macOS: `osx-arm64`, `osx-x64`
- Windows: `win-x64`
- Linux: `linux-x64`
- Android: `android-arm64`, `android-x64`
- iOS: `ios-arm64`, `iossimulator-arm64`, `iossimulator-x64`

## Notes

- The current embedded runtime uses in-memory ports and does not seed media by default.
- Plugin lifecycle and listing are placeholders until the plugin host is bridged into the native runtime.
