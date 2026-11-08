/*
 * emma_native.h - the C ABI of the EMMA native library (libemma_native.so, emma_native.dll, libemma_native.dylib).
 *
 * Design: one call, many operations. Everything EMMA can do is an operation name passed to emma_call, with a JSON
 * request and a JSON answer, so adding a capability never adds an export and the ABI stays small and stable.
 * The JSON shapes are the ones the HTTP API uses (docs/architecture/api.md, src/EMMA.Api/README.md).
 * Operations and their requests are listed in docs/architecture/native-abi.md.
 *
 * Conventions
 *  - Strings and JSON are UTF-8, passed as pointer + length (no terminator needed).
 *  - Every EmmaBuffer returned to you is yours: release it with emma_buffer_free(buffer.data). An empty buffer has
 *    data == NULL and needs no release.
 *  - A handle is a number, not a pointer. Using a closed or invented handle returns an error, never crashes.
 *  - Functions are thread-safe. A handle may be used from many threads at once.
 *  - Nothing here throws or aborts; unexpected failures come back as EMMA_PANIC.
 *
 * Answers
 *  - EMMA_OK: the operation ran. The JSON buffer holds {"ok": ...} on success or
 *    {"error": {"code": "...", "message": "...", "retry_after_ms": n}} when the operation failed
 *    (for example "not_found"). Read the envelope.
 *  - Any other status: the call never reached an operation; the JSON buffer, if present, holds an error envelope.
 */
#ifndef EMMA_NATIVE_H
#define EMMA_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define EMMA_ABI_VERSION 1

#define EMMA_OK 0
#define EMMA_INVALID_ARGUMENT 1
#define EMMA_RUNTIME_ERROR 2
#define EMMA_PANIC 3

/* Bytes returned to the caller. Free with emma_buffer_free(data). */
typedef struct EmmaBuffer {
    uint8_t *data;
    size_t length;
} EmmaBuffer;

/* An open runtime. Zero is never a valid handle. */
typedef intptr_t EmmaRuntime;

/* Version of this ABI. A library whose version differs from EMMA_ABI_VERSION must not be used. */
int32_t emma_abi_version(void);

/*
 * Creates a runtime from a JSON configuration:
 *   {"data_dir": "/path", "user": "local",
 *    "trust": {"require_signed": true,
 *              "repositories": [{"id": "main", "root_keys": {"key-1": "-----BEGIN PUBLIC KEY-----..."}}]}}
 * "data_dir" is required; "user" defaults to "local"; "trust" defaults to require_signed with no repositories.
 * On success *runtime is the handle. On failure *runtime is 0 and, if error is not NULL, it holds an error envelope.
 */
int32_t emma_runtime_create(const uint8_t *config, size_t config_length, EmmaRuntime *runtime, EmmaBuffer *error);

/* Closes a handle: calls in flight are cancelled and awaited. Closing an unknown or closed handle does nothing. */
void emma_runtime_destroy(EmmaRuntime runtime);

/*
 * Runs an operation and waits for it. request may be NULL with request_length 0, meaning {}.
 * *json always receives the envelope; *blob receives binary content for operations that return bytes
 * (assets.content) and is empty otherwise. Both must be non-NULL.
 */
int32_t emma_call(EmmaRuntime runtime, const uint8_t *operation, size_t operation_length,
                  const uint8_t *request, size_t request_length, EmmaBuffer *json, EmmaBuffer *blob);

/*
 * Starts an operation and returns at once. Returns EMMA_OK when started; the callback is then called exactly once,
 * from another thread, with user_data, the status, and the JSON envelope and binary content as (pointer, length)
 * pairs. The callback now owns both pointers and must free each non-NULL one with emma_buffer_free.
 * If the return is not EMMA_OK the callback is never called. request is copied before this returns.
 *
 * Everything is passed by value and points at heap memory, never at the library's stack, so the callback may
 * hand the values to another thread and read them later. A Dart NativeCallable.listener does exactly that.
 */
typedef void (*EmmaCallback)(intptr_t user_data, int32_t status, uint8_t *json, size_t json_length, uint8_t *blob,
                             size_t blob_length);

int32_t emma_call_async(EmmaRuntime runtime, const uint8_t *operation, size_t operation_length,
                        const uint8_t *request, size_t request_length, EmmaCallback callback, intptr_t user_data);

/* Releases memory returned in an EmmaBuffer (pass buffer.data). NULL is allowed. */
void emma_buffer_free(uint8_t *data);

#ifdef __cplusplus
}
#endif

#endif /* EMMA_NATIVE_H */
