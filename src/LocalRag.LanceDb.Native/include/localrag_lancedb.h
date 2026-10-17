#ifndef LOCALRAG_LANCEDB_H
#define LOCALRAG_LANCEDB_H

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#define LRAG_API __declspec(dllexport)
#else
#define LRAG_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define LRAG_ABI_V1 1u
#define LRAG_MAX_BATCH_ROWS 100u
#define LRAG_MAX_IPC_BYTES (16u * 1024u * 1024u)
#define LRAG_VECTOR_DIMENSIONS 384u

typedef enum lrag_status {
    LRAG_OK = 0,
    LRAG_INVALID_ARGUMENT = 1,
    LRAG_ABI_MISMATCH = 2,
    LRAG_NOT_READY = 3,
    LRAG_LOCKED = 4,
    LRAG_NOT_OWNED = 5,
    LRAG_CANCELLED = 6,
    LRAG_NATIVE_FAILURE = 7,
    LRAG_PANIC = 8
} lrag_status;

typedef struct lrag_db lrag_db;
typedef struct lrag_cancel lrag_cancel;
typedef struct lrag_buffer lrag_buffer;
typedef struct lrag_error lrag_error;

typedef struct lrag_open_request_v1 {
    uint32_t struct_size;
    const uint8_t *path;
    size_t path_len;
    const uint8_t *table;
    size_t table_len;
    const uint8_t *ownership_id;
    size_t ownership_id_len;
} lrag_open_request_v1;

typedef struct lrag_search_request_v1 {
    uint32_t struct_size;
    const uint8_t *query;
    size_t query_len;
    const uint8_t *language;
    size_t language_len;
    const uint8_t *path_prefix;
    size_t path_prefix_len;
    const uint8_t *source_ids_ipc;
    size_t source_ids_ipc_len;
    const float *vector;
    size_t vector_len;
    uint32_t limit;
    float alpha;
    uint32_t mode;
} lrag_search_request_v1;

LRAG_API lrag_status lrag_abi_negotiate(uint32_t requested, uint32_t *selected, lrag_error **error);
LRAG_API lrag_status lrag_db_open(const lrag_open_request_v1 *request, lrag_db **db, lrag_error **error);
LRAG_API lrag_status lrag_db_close(lrag_db *db, lrag_error **error);
LRAG_API lrag_status lrag_cancel_new(lrag_cancel **cancel, lrag_error **error);
LRAG_API lrag_status lrag_cancel_request(lrag_cancel *cancel, lrag_error **error);
LRAG_API lrag_status lrag_cancel_release(lrag_cancel *cancel, lrag_error **error);
LRAG_API lrag_status lrag_db_ensure_ready(lrag_db *db, lrag_cancel *cancel, lrag_error **error);
LRAG_API lrag_status lrag_db_upsert_ipc(lrag_db *db, const uint8_t *ipc, size_t ipc_len, lrag_cancel *cancel, lrag_error **error);
LRAG_API lrag_status lrag_db_delete_chunks(lrag_db *db, const uint8_t *ipc, size_t ipc_len, lrag_cancel *cancel, lrag_error **error);
LRAG_API lrag_status lrag_db_delete_source(lrag_db *db, const uint8_t *source_id, size_t source_id_len, lrag_cancel *cancel, lrag_error **error);
LRAG_API lrag_status lrag_db_search(lrag_db *db, const lrag_search_request_v1 *request, lrag_cancel *cancel, lrag_buffer **result, lrag_error **error);
LRAG_API lrag_status lrag_db_verify_ownership(lrag_db *db, const uint8_t *ownership_id, size_t ownership_id_len, uint8_t *owned, lrag_error **error);
LRAG_API lrag_status lrag_db_reset_owned(lrag_db *db, const uint8_t *ownership_id, size_t ownership_id_len, lrag_cancel *cancel, lrag_error **error);
LRAG_API lrag_status lrag_db_optimize(lrag_db *db, lrag_cancel *cancel, lrag_error **error);
LRAG_API const uint8_t *lrag_buffer_data(const lrag_buffer *buffer);
LRAG_API size_t lrag_buffer_len(const lrag_buffer *buffer);
LRAG_API void lrag_buffer_release(lrag_buffer *buffer);
LRAG_API uint32_t lrag_error_code(const lrag_error *error);
LRAG_API const uint8_t *lrag_error_message(const lrag_error *error);
LRAG_API size_t lrag_error_message_len(const lrag_error *error);
LRAG_API void lrag_error_release(lrag_error *error);

#ifdef __cplusplus
}
#endif
#endif

