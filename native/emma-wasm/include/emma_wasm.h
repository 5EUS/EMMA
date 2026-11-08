/* The C ABI of the EMMA Wasm plugin host (libemma_wasm). Hand-written; keep in step with src/ffi.rs. */
#ifndef EMMA_WASM_H
#define EMMA_WASM_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define EMMA_OK 0
#define EMMA_ERR_ARGUMENT 1
#define EMMA_ERR_RUNTIME 2
#define EMMA_ERR_PANIC 3

/* An owned byte buffer. Free with emma_buffer_free. ptr is NULL when len is 0. */
typedef struct EmmaBuffer { uint8_t *ptr; size_t len; size_t cap; } EmmaBuffer;

typedef struct EmmaRuntime EmmaRuntime;
typedef struct EmmaPlugin EmmaPlugin;

void emma_buffer_free(EmmaBuffer buffer);

/* options: optional JSON such as {"target":"pulley64"}; may be empty. */
int32_t emma_runtime_create(const uint8_t *options, size_t options_len, EmmaRuntime **out_runtime, EmmaBuffer *out_error);
void emma_runtime_destroy(EmmaRuntime *runtime);
int32_t emma_runtime_key(const EmmaRuntime *runtime, EmmaBuffer *out_key);
int32_t emma_precompile(const EmmaRuntime *runtime, const uint8_t *wasm, size_t wasm_len, EmmaBuffer *out_cwasm, EmmaBuffer *out_error);

/* config: JSON, see PluginConfig. out_info receives {"searchAssist":bool}. */
int32_t emma_plugin_open(const EmmaRuntime *runtime, const uint8_t *config, size_t config_len, EmmaPlugin **out_plugin, EmmaBuffer *out_info, EmmaBuffer *out_error);
void emma_plugin_close(EmmaPlugin *plugin);

/* Runs one operation. out_json gets the envelope (plugin errors and traps are inside it); out_blob the bytes of a read. */
int32_t emma_plugin_call(const EmmaPlugin *plugin, const uint8_t *operation, size_t operation_len, const uint8_t *request, size_t request_len, EmmaBuffer *out_json, EmmaBuffer *out_blob, EmmaBuffer *out_error);

#ifdef __cplusplus
}
#endif
#endif
