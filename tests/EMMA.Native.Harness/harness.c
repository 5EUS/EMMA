/*
 * Loads the published NativeAOT library the way an embedding app would (dlopen / LoadLibrary), creates a runtime,
 * runs operations through both call styles, checks error paths and shuts down cleanly. Exits non-zero on the first
 * failed expectation. Built and run by scripts/development/native-smoke.sh.
 *
 * It needs no plugin: the managed tests (EMMA.Native.Tests) cover behaviour against a real Wasm plugin. This proves
 * the part only a real library can: exports, marshalling, buffer ownership and AOT.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
typedef HMODULE Lib;
#define LOAD(path) LoadLibraryA(path)
#define SYMBOL(lib, name) ((void *)GetProcAddress(lib, name))
#else
#include <dlfcn.h>
#include <pthread.h>
typedef void *Lib;
#define LOAD(path) dlopen(path, RTLD_NOW)
#define SYMBOL(lib, name) dlsym(lib, name)
#endif

#include "emma_native.h"

static int32_t (*p_abi_version)(void);
static int32_t (*p_create)(const uint8_t *, size_t, EmmaRuntime *, EmmaBuffer *);
static void (*p_destroy)(EmmaRuntime);
static int32_t (*p_call)(EmmaRuntime, const uint8_t *, size_t, const uint8_t *, size_t, EmmaBuffer *, EmmaBuffer *);
static int32_t (*p_call_async)(EmmaRuntime, const uint8_t *, size_t, const uint8_t *, size_t, EmmaCallback, intptr_t);
static void (*p_free)(uint8_t *);

static int failures = 0;

#define EXPECT(condition, ...)                                                   \
    do {                                                                         \
        if (!(condition)) {                                                      \
            fprintf(stderr, "FAIL line %d: ", __LINE__);                         \
            fprintf(stderr, __VA_ARGS__);                                        \
            fprintf(stderr, "\n");                                               \
            failures++;                                                          \
        }                                                                        \
    } while (0)

static int contains(const EmmaBuffer *buffer, const char *needle) {
    if (buffer->data == NULL) return 0;
    char *copy = malloc(buffer->length + 1);
    memcpy(copy, buffer->data, buffer->length);
    copy[buffer->length] = 0;
    int found = strstr(copy, needle) != NULL;
    free(copy);
    return found;
}

static int32_t call(EmmaRuntime runtime, const char *operation, const char *request, EmmaBuffer *json, EmmaBuffer *blob) {
    return p_call(runtime, (const uint8_t *)operation, strlen(operation), (const uint8_t *)request,
                  request ? strlen(request) : 0, json, blob);
}

/* ---- async: a callback signals a condition the main thread waits on ---- */
typedef struct {
#ifndef _WIN32
    pthread_mutex_t lock;
    pthread_cond_t done;
#endif
    int finished;
    int32_t status;
    EmmaBuffer json;
} Waiter;

static void on_done(intptr_t user_data, int32_t status, uint8_t *json, size_t json_length, uint8_t *blob, size_t blob_length) {
    (void)blob_length;
    Waiter *waiter = (Waiter *)user_data;
    p_free(blob);
#ifndef _WIN32
    pthread_mutex_lock(&waiter->lock);
#endif
    waiter->status = status;
    waiter->json.data = json; /* ownership moves to the waiter */
    waiter->json.length = json_length;
    waiter->finished = 1;
#ifndef _WIN32
    pthread_cond_signal(&waiter->done);
    pthread_mutex_unlock(&waiter->lock);
#endif
}

int main(int argc, char **argv) {
    if (argc < 3) {
        fprintf(stderr, "usage: harness <library> <data-dir>\n");
        return 2;
    }

    /* A host often has its own SQLite loaded already (a Flutter app that plays video gets the system libsqlite3 through
     * libmpv and ffmpeg). The runtime must not be disturbed by it, so load one first, globally, as such a host would.
     * Skipped where the system has none. */
#ifndef _WIN32
    if (dlopen("libsqlite3.so.0", RTLD_NOW | RTLD_GLOBAL) || dlopen("libsqlite3.dylib", RTLD_NOW | RTLD_GLOBAL)) {
        printf("native harness: the host process has its own SQLite loaded\n");
    }
#endif
    Lib lib = LOAD(argv[1]);
    if (!lib) {
        fprintf(stderr, "cannot load %s\n", argv[1]);
        return 2;
    }
    p_abi_version = (int32_t(*)(void))SYMBOL(lib, "emma_abi_version");
    p_create = (int32_t(*)(const uint8_t *, size_t, EmmaRuntime *, EmmaBuffer *))SYMBOL(lib, "emma_runtime_create");
    p_destroy = (void (*)(EmmaRuntime))SYMBOL(lib, "emma_runtime_destroy");
    p_call = (int32_t(*)(EmmaRuntime, const uint8_t *, size_t, const uint8_t *, size_t, EmmaBuffer *, EmmaBuffer *))SYMBOL(lib, "emma_call");
    p_call_async = (int32_t(*)(EmmaRuntime, const uint8_t *, size_t, const uint8_t *, size_t, EmmaCallback, intptr_t))SYMBOL(lib, "emma_call_async");
    p_free = (void (*)(uint8_t *))SYMBOL(lib, "emma_buffer_free");
    if (!p_abi_version || !p_create || !p_destroy || !p_call || !p_call_async || !p_free) {
        fprintf(stderr, "a required export is missing\n");
        return 2;
    }

    EXPECT(p_abi_version() == EMMA_ABI_VERSION, "ABI version %d", (int)p_abi_version());

    /* A bad configuration is refused with an error envelope, and no handle. */
    EmmaRuntime runtime = 123;
    EmmaBuffer error = {0};
    const char *bad = "{\"no_data_dir\": true}";
    int32_t status = p_create((const uint8_t *)bad, strlen(bad), &runtime, &error);
    EXPECT(status == EMMA_INVALID_ARGUMENT, "bad config status %d", (int)status);
    EXPECT(runtime == 0, "bad config left a handle");
    EXPECT(contains(&error, "invalid_argument"), "bad config error envelope");
    p_free(error.data);

    char config[2048];
    /* The library is loaded into this process, so its native dependencies are not next to the executable. */
    char native_dir[1024];
    snprintf(native_dir, sizeof native_dir, "%s", argv[1]);
    char *slash = strrchr(native_dir, '/');
    if (slash) *slash = 0;
    snprintf(config, sizeof config, "{\"data_dir\": \"%s\", \"user\": \"harness\", \"native_dir\": \"%s\"}", argv[2], native_dir);
    status = p_create((const uint8_t *)config, strlen(config), &runtime, &error);
    EXPECT(status == EMMA_OK && runtime != 0, "create status %d", (int)status);
    if (status != EMMA_OK) return 1;

    EmmaBuffer json = {0}, blob = {0};

    /* An operation that needs nothing. */
    status = call(runtime, "plugins.list", NULL, &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "\"ok\"") && contains(&json, "\"plugins\":[]"), "plugins.list");
    p_free(json.data);

    /* Progress round trip: write, read back (the unit is open: pages here, milliseconds for video). */
    status = call(runtime, "progress.set", "{\"ref\":\"com.example.demo:c1\",\"unit\":\"page\",\"value\":7,\"total\":20}", &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "\"ok\"") && contains(&json, "\"unit\":\"page\""), "progress.set");
    p_free(json.data);
    status = call(runtime, "progress.get", "{\"ref\":\"com.example.demo:c1\"}", &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "\"value\":7"), "progress.get");
    p_free(json.data);
    status = call(runtime, "progress.recent", "{\"limit\":5}", &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "com.example.demo:c1"), "progress.recent");
    p_free(json.data);

    /* Operation-level failures are an OK status carrying an error envelope. */
    status = call(runtime, "catalog.search", "{\"plugin\":\"com.example.missing\",\"query\":{\"text\":\"x\"}}", &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "\"error\"") && contains(&json, "not_found"), "missing plugin");
    p_free(json.data);
    status = call(runtime, "library.add", "{\"ref\":\"com.example.demo:unseen\"}", &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "not_found"), "unseen node");
    p_free(json.data);
    status = call(runtime, "no.such.operation", NULL, &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "unsupported"), "unknown operation");
    p_free(json.data);
    status = call(runtime, "progress.set", "{\"ref\":\"nope\"}", &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "invalid_argument"), "bad request");
    p_free(json.data);
    status = call(runtime, "progress.set", "{\"unknown_field\":1}", &json, &blob);
    EXPECT(status == EMMA_OK && contains(&json, "invalid_argument"), "unknown field refused");
    p_free(json.data);

    /* Misuse is reported, not fatal. */
    status = call(runtime + 1000, "plugins.list", NULL, &json, &blob);
    EXPECT(status == EMMA_INVALID_ARGUMENT && contains(&json, "invalid_handle"), "invented handle");
    p_free(json.data);
    status = p_call(runtime, NULL, 0, NULL, 0, &json, &blob);
    EXPECT(status == EMMA_INVALID_ARGUMENT, "missing operation");
    p_free(json.data);
    status = p_call(runtime, (const uint8_t *)"plugins.list", 12, NULL, 0, NULL, NULL);
    EXPECT(status == EMMA_INVALID_ARGUMENT, "missing output buffers");

    /* The asynchronous style delivers exactly once, with buffers the callback owns. */
    Waiter waiter;
    memset(&waiter, 0, sizeof waiter);
#ifndef _WIN32
    pthread_mutex_init(&waiter.lock, NULL);
    pthread_cond_init(&waiter.done, NULL);
#endif
    status = p_call_async(runtime, (const uint8_t *)"progress.get", 12, (const uint8_t *)"{\"ref\":\"com.example.demo:c1\"}", 29, on_done, (intptr_t)&waiter);
    EXPECT(status == EMMA_OK, "call_async start %d", (int)status);
#ifndef _WIN32
    pthread_mutex_lock(&waiter.lock);
    while (!waiter.finished) pthread_cond_wait(&waiter.done, &waiter.lock);
    pthread_mutex_unlock(&waiter.lock);
#else
    while (!waiter.finished) Sleep(10);
#endif
    EXPECT(waiter.status == EMMA_OK && contains(&waiter.json, "\"value\":7"), "async result");
    p_free(waiter.json.data);

    /* Close, then prove a closed handle is an error and a second close is harmless. */
    p_destroy(runtime);
    status = call(runtime, "plugins.list", NULL, &json, &blob);
    EXPECT(status == EMMA_INVALID_ARGUMENT, "closed handle");
    p_free(json.data);
    p_destroy(runtime);
    p_destroy(0);

    if (failures == 0) {
        printf("native harness: all checks passed\n");
        return 0;
    }
    fprintf(stderr, "native harness: %d check(s) failed\n", failures);
    return 1;
}
