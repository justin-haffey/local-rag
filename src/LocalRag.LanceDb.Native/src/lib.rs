//! LocalRAG's synchronous, callback-free ABI boundary for embedded LanceDB.
//!
//! These exported C entry points necessarily receive raw pointers. They validate
//! every nullable pointer, byte length, struct size, and row bound before access;
//! this crate does not expose them as a safe Rust API.
#![allow(clippy::not_unsafe_ptr_arg_deref)]

use arrow::array::{
    Array, Float32Array, Float64Array, Int32Array, RecordBatch, RecordBatchIterator, StringArray,
};
use arrow::datatypes::{DataType, Field, Schema, SchemaRef};
use arrow::ipc::reader::StreamReader;
use arrow::ipc::writer::StreamWriter;
use futures::TryStreamExt;
use lance_index::scalar::FullTextSearchQuery;
use lancedb::index::Index;
use lancedb::index::scalar::FtsIndexBuilder;
use lancedb::index::vector::IvfFlatIndexBuilder;
use lancedb::query::{ExecutableQuery, QueryBase};
use lancedb::table::OptimizeAction;
use lancedb::{Connection, DistanceType, Table, connect};
use std::collections::{HashMap, HashSet};
use std::ffi::c_void;
use std::io::Cursor;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::slice;
use std::str;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use tokio::runtime::{Builder, Runtime};

const ABI_V1: u32 = 1;
const OK: u32 = 0;
const INVALID_ARGUMENT: u32 = 1;
const ABI_MISMATCH: u32 = 2;
const NOT_READY: u32 = 3;
const NOT_OWNED: u32 = 5;
const CANCELLED: u32 = 6;
const NATIVE_FAILURE: u32 = 7;
const PANIC: u32 = 8;
const MAX_ROWS: usize = 100;
const MAX_IPC_BYTES: usize = 16 * 1024 * 1024;
const VECTOR_DIMENSIONS: usize = 384;
const MAX_PATH_BYTES: usize = 32 * 1024;
const MAX_IDENTIFIER_BYTES: usize = 128;
const MAX_QUERY_BYTES: usize = 32 * 1024;
const SCHEMA_VERSION: &str = "1";
const SCHEMA_VERSION_KEY: &str = "localrag.schemaVersion";
const OWNERSHIP_KEY: &str = "localrag.ownershipId";

#[repr(C)]
pub struct LragError {
    code: u32,
    message: Vec<u8>,
}

#[repr(C)]
pub struct LragBuffer {
    bytes: Vec<u8>,
}

#[repr(C)]
pub struct LragCancel {
    requested: AtomicBool,
}

#[repr(C)]
pub struct LragOpenRequestV1 {
    struct_size: u32,
    path: *const u8,
    path_len: usize,
    table: *const u8,
    table_len: usize,
    ownership_id: *const u8,
    ownership_id_len: usize,
}

#[repr(C)]
pub struct LragSearchRequestV1 {
    struct_size: u32,
    query: *const u8,
    query_len: usize,
    language: *const u8,
    language_len: usize,
    path_prefix: *const u8,
    path_prefix_len: usize,
    source_ids_ipc: *const u8,
    source_ids_ipc_len: usize,
    vector: *const f32,
    vector_len: usize,
    limit: u32,
    alpha: f32,
    mode: u32,
}

pub struct LragDb {
    runtime: Runtime,
    connection: Connection,
    table_name: String,
    ownership_id: String,
    operations: Mutex<()>,
}

#[derive(Debug)]
struct NativeError {
    status: u32,
    message: &'static str,
}

type NativeResult<T> = Result<T, NativeError>;

impl NativeError {
    fn invalid(message: &'static str) -> Self {
        Self {
            status: INVALID_ARGUMENT,
            message,
        }
    }

    fn failure(message: &'static str) -> Self {
        Self {
            status: NATIVE_FAILURE,
            message,
        }
    }

    fn not_owned() -> Self {
        Self {
            status: NOT_OWNED,
            message: "The configured LanceDB table is not owned by this LocalRAG installation.",
        }
    }

    fn cancelled() -> Self {
        Self {
            status: CANCELLED,
            message: "The embedded LanceDB operation was cancelled.",
        }
    }
}

fn set_error(out: *mut *mut LragError, code: u32, message: &str) -> u32 {
    if !out.is_null() {
        unsafe {
            *out = Box::into_raw(Box::new(LragError {
                code,
                message: message.as_bytes().to_vec(),
            }));
        }
    }
    code
}

fn boundary(error: *mut *mut LragError, body: impl FnOnce() -> NativeResult<()>) -> u32 {
    if !error.is_null() {
        unsafe { *error = ptr::null_mut() };
    }
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(Ok(())) => OK,
        Ok(Err(native)) => set_error(error, native.status, native.message),
        Err(_) => set_error(error, PANIC, "The native LanceDB boundary trapped a panic."),
    }
}

fn native<T>(result: lancedb::Result<T>) -> NativeResult<T> {
    result.map_err(|_| NativeError::failure("The embedded LanceDB operation failed."))
}

fn arrow<T>(result: arrow::error::Result<T>) -> NativeResult<T> {
    result.map_err(|_| NativeError::invalid("The Arrow IPC payload was invalid."))
}

fn cancelled(cancel: *mut LragCancel) -> NativeResult<()> {
    if cancel.is_null() {
        return Ok(());
    }
    let requested = unsafe { &*cancel }.requested.load(Ordering::Acquire);
    if requested {
        Err(NativeError::cancelled())
    } else {
        Ok(())
    }
}

fn read_utf8(
    pointer: *const u8,
    length: usize,
    maximum: usize,
    empty_allowed: bool,
    message: &'static str,
) -> NativeResult<String> {
    if length > maximum || (length > 0 && pointer.is_null()) {
        return Err(NativeError::invalid(message));
    }
    if length == 0 {
        return if empty_allowed {
            Ok(String::new())
        } else {
            Err(NativeError::invalid(message))
        };
    }
    let bytes = unsafe { slice::from_raw_parts(pointer, length) };
    let value = str::from_utf8(bytes).map_err(|_| NativeError::invalid(message))?;
    if value.contains('\0') || (!empty_allowed && value.trim().is_empty()) {
        return Err(NativeError::invalid(message));
    }
    Ok(value.to_owned())
}

fn read_bytes<'a>(pointer: *const u8, length: usize) -> NativeResult<&'a [u8]> {
    if length == 0 || length > MAX_IPC_BYTES || pointer.is_null() {
        return Err(NativeError::invalid(
            "The Arrow IPC payload was empty or exceeded its bound.",
        ));
    }
    Ok(unsafe { slice::from_raw_parts(pointer, length) })
}

fn validate_identifier(value: &str) -> NativeResult<()> {
    if value.len() > MAX_IDENTIFIER_BYTES
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
    {
        return Err(NativeError::invalid(
            "The LanceDB table name must contain only ASCII letters, digits, or underscores.",
        ));
    }
    Ok(())
}

fn document_fields() -> Vec<Field> {
    vec![
        Field::new("chunkId", DataType::Utf8, false),
        Field::new("sourceId", DataType::Utf8, false),
        Field::new("fileId", DataType::Utf8, false),
        Field::new("relativePath", DataType::Utf8, false),
        Field::new("language", DataType::Utf8, false),
        Field::new("symbolName", DataType::Utf8, true),
        Field::new("startLine", DataType::Int32, false),
        Field::new("endLine", DataType::Int32, false),
        Field::new("ordinal", DataType::Int32, false),
        Field::new("content", DataType::Utf8, false),
        Field::new("contentHash", DataType::Utf8, false),
        Field::new("tokenCount", DataType::Int32, false),
        Field::new("embeddingProfileId", DataType::Utf8, false),
        Field::new("lastIndexedUtc", DataType::Utf8, false),
        Field::new("chunkKind", DataType::Utf8, false),
        Field::new("qualifiedSymbolName", DataType::Utf8, true),
        Field::new("structuralLocator", DataType::Utf8, false),
        Field::new("chunkerId", DataType::Utf8, false),
        Field::new("chunkerVersion", DataType::Utf8, false),
        Field::new("chunkProfileFingerprint", DataType::Utf8, false),
        Field::new(
            "vector",
            DataType::FixedSizeList(
                Arc::new(Field::new("item", DataType::Float32, true)),
                VECTOR_DIMENSIONS as i32,
            ),
            false,
        ),
    ]
}

fn owned_schema(ownership_id: &str) -> SchemaRef {
    let metadata = HashMap::from([
        (SCHEMA_VERSION_KEY.to_owned(), SCHEMA_VERSION.to_owned()),
        (OWNERSHIP_KEY.to_owned(), ownership_id.to_owned()),
    ]);
    Arc::new(Schema::new_with_metadata(document_fields(), metadata))
}

fn validate_schema(schema: &SchemaRef, ownership_id: &str) -> NativeResult<()> {
    if schema.fields().len() != document_fields().len()
        || schema
            .fields()
            .iter()
            .zip(document_fields())
            .any(|(actual, expected)| actual.as_ref() != &expected)
    {
        return Err(NativeError {
            status: NOT_READY,
            message: "The embedded LanceDB table schema is incompatible.",
        });
    }
    if schema
        .metadata()
        .get(SCHEMA_VERSION_KEY)
        .map(String::as_str)
        != Some(SCHEMA_VERSION)
        || schema.metadata().get(OWNERSHIP_KEY).map(String::as_str) != Some(ownership_id)
    {
        return Err(NativeError::not_owned());
    }
    Ok(())
}

async fn ensure_table(db: &LragDb) -> NativeResult<Table> {
    let names = native(db.connection.table_names().execute().await)?;
    let table = if names.iter().any(|name| name == &db.table_name) {
        native(db.connection.open_table(&db.table_name).execute().await)?
    } else {
        native(
            db.connection
                .create_empty_table(&db.table_name, owned_schema(&db.ownership_id))
                .execute()
                .await,
        )?
    };
    validate_schema(&native(table.schema().await)?, &db.ownership_id)?;
    Ok(table)
}

async fn ensure_fts(table: &Table) -> NativeResult<()> {
    if native(table.count_rows(None).await)? == 0 {
        return Ok(());
    }
    let indices = native(table.list_indices().await)?;
    if !indices
        .iter()
        .any(|index| index.columns == ["content".to_owned()])
    {
        native(
            table
                .create_index(&["content"], Index::FTS(FtsIndexBuilder::default()))
                .replace(false)
                .execute()
                .await,
        )?;
    }
    Ok(())
}

async fn ensure_vector_index(table: &Table) -> NativeResult<()> {
    if native(table.count_rows(None).await)? == 0 {
        return Ok(());
    }
    let indices = native(table.list_indices().await)?;
    if !indices
        .iter()
        .any(|index| index.columns == ["vector".to_owned()])
    {
        native(
            table
                .create_index(
                    &["vector"],
                    Index::IvfFlat(
                        IvfFlatIndexBuilder::default()
                            .distance_type(DistanceType::Cosine)
                            .num_partitions(1),
                    ),
                )
                .replace(false)
                .execute()
                .await,
        )?;
    }
    Ok(())
}

fn db_ref<'a>(database: *mut c_void) -> NativeResult<&'a LragDb> {
    if database.is_null() {
        Err(NativeError::invalid("The LanceDB handle is required."))
    } else {
        Ok(unsafe { &*(database.cast::<LragDb>()) })
    }
}

fn decode_batches(bytes: &[u8], expected_fields: &[Field]) -> NativeResult<Vec<RecordBatch>> {
    let reader = arrow(StreamReader::try_new(Cursor::new(bytes), None))?;
    if reader.schema().fields().len() != expected_fields.len()
        || reader
            .schema()
            .fields()
            .iter()
            .zip(expected_fields)
            .any(|(actual, expected)| actual.as_ref() != expected)
    {
        return Err(NativeError::invalid(
            "The Arrow IPC payload schema was incompatible.",
        ));
    }
    let mut rows = 0usize;
    let mut batches = Vec::new();
    for batch in reader {
        let batch = arrow(batch)?;
        rows = rows
            .checked_add(batch.num_rows())
            .ok_or_else(|| NativeError::invalid("The Arrow IPC row count overflowed."))?;
        if rows > MAX_ROWS {
            return Err(NativeError::invalid(
                "The Arrow IPC payload exceeded the 100 row bound.",
            ));
        }
        batches.push(batch);
    }
    Ok(batches)
}

fn decode_ids(bytes: &[u8]) -> NativeResult<Vec<String>> {
    let fields = [Field::new("id", DataType::Utf8, false)];
    let batches = decode_batches(bytes, &fields)?;
    let mut values = Vec::new();
    for batch in batches {
        let array = batch
            .column(0)
            .as_any()
            .downcast_ref::<StringArray>()
            .ok_or_else(|| NativeError::invalid("The identifier IPC column was incompatible."))?;
        for row in 0..array.len() {
            if array.is_null(row)
                || array.value(row).trim().is_empty()
                || array.value(row).contains('\0')
            {
                return Err(NativeError::invalid(
                    "The identifier IPC column contained an invalid value.",
                ));
            }
            values.push(array.value(row).to_owned());
        }
    }
    Ok(values)
}

fn sql_string(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

fn sql_like_prefix(value: &str) -> String {
    let escaped = value
        .replace('\\', "\\\\")
        .replace('%', "\\%")
        .replace('_', "\\_")
        .replace('\'', "''");
    format!("'{escaped}%'")
}

fn build_filter(source_ids: &[String], language: &str, path_prefix: &str) -> String {
    let mut terms = Vec::new();
    if !source_ids.is_empty() {
        terms.push(format!(
            "sourceId IN ({})",
            source_ids
                .iter()
                .map(|value| sql_string(value))
                .collect::<Vec<_>>()
                .join(",")
        ));
    }
    if !language.is_empty() {
        terms.push(format!("language = {}", sql_string(language)));
    }
    if !path_prefix.is_empty() {
        terms.push(format!(
            "relativePath LIKE {} ESCAPE '\\'",
            sql_like_prefix(path_prefix)
        ));
    }
    terms.join(" AND ")
}

#[derive(Clone)]
struct Candidate {
    chunk_id: String,
    source_id: String,
    relative_path: String,
    language: String,
    symbol_name: Option<String>,
    start_line: i32,
    end_line: i32,
    content: String,
    content_hash: String,
    last_indexed_utc: String,
    chunk_kind: String,
    qualified_symbol_name: Option<String>,
    structural_locator: String,
    chunker_id: String,
    chunker_version: String,
    chunk_profile_fingerprint: String,
}

struct RankedCandidate {
    value: Candidate,
    vector_rank: Option<usize>,
    lexical_rank: Option<usize>,
    score: f64,
}

fn string_value(batch: &RecordBatch, name: &str, row: usize) -> NativeResult<String> {
    let array = batch
        .column_by_name(name)
        .and_then(|value| value.as_any().downcast_ref::<StringArray>())
        .ok_or_else(|| NativeError::failure("The LanceDB result schema was incompatible."))?;
    if array.is_null(row) {
        Err(NativeError::failure(
            "The LanceDB result contained an invalid null.",
        ))
    } else {
        Ok(array.value(row).to_owned())
    }
}

fn optional_string_value(
    batch: &RecordBatch,
    name: &str,
    row: usize,
) -> NativeResult<Option<String>> {
    let array = batch
        .column_by_name(name)
        .and_then(|value| value.as_any().downcast_ref::<StringArray>())
        .ok_or_else(|| NativeError::failure("The LanceDB result schema was incompatible."))?;
    Ok((!array.is_null(row)).then(|| array.value(row).to_owned()))
}

fn int_value(batch: &RecordBatch, name: &str, row: usize) -> NativeResult<i32> {
    let array = batch
        .column_by_name(name)
        .and_then(|value| value.as_any().downcast_ref::<Int32Array>())
        .ok_or_else(|| NativeError::failure("The LanceDB result schema was incompatible."))?;
    if array.is_null(row) {
        Err(NativeError::failure(
            "The LanceDB result contained an invalid null.",
        ))
    } else {
        Ok(array.value(row))
    }
}

fn candidate(batch: &RecordBatch, row: usize) -> NativeResult<Candidate> {
    Ok(Candidate {
        chunk_id: string_value(batch, "chunkId", row)?,
        source_id: string_value(batch, "sourceId", row)?,
        relative_path: string_value(batch, "relativePath", row)?,
        language: string_value(batch, "language", row)?,
        symbol_name: optional_string_value(batch, "symbolName", row)?,
        start_line: int_value(batch, "startLine", row)?,
        end_line: int_value(batch, "endLine", row)?,
        content: string_value(batch, "content", row)?,
        content_hash: string_value(batch, "contentHash", row)?,
        last_indexed_utc: string_value(batch, "lastIndexedUtc", row)?,
        chunk_kind: string_value(batch, "chunkKind", row)?,
        qualified_symbol_name: optional_string_value(batch, "qualifiedSymbolName", row)?,
        structural_locator: string_value(batch, "structuralLocator", row)?,
        chunker_id: string_value(batch, "chunkerId", row)?,
        chunker_version: string_value(batch, "chunkerVersion", row)?,
        chunk_profile_fingerprint: string_value(batch, "chunkProfileFingerprint", row)?,
    })
}

fn merge_candidates(
    target: &mut HashMap<String, RankedCandidate>,
    batches: &[RecordBatch],
    vector: bool,
) -> NativeResult<()> {
    let mut ordered = Vec::new();
    for batch in batches {
        let scores = batch
            .column_by_name(if vector { "_distance" } else { "_score" })
            .and_then(|column| column.as_any().downcast_ref::<Float32Array>())
            .ok_or_else(|| NativeError::failure("The search ranking column was invalid."))?;
        for row in 0..batch.num_rows() {
            let value = candidate(batch, row)?;
            ordered.push((value, scores.value(row)));
        }
    }
    // Provider iteration order is not a tie-break contract. Assign equal raw
    // scores equal ranks and let the final chunk-ID ordering resolve ties.
    ordered.sort_by(|left, right| {
        let order = left.1.total_cmp(&right.1);
        (if vector { order } else { order.reverse() })
            .then_with(|| left.0.chunk_id.cmp(&right.0.chunk_id))
    });
    let mut previous_score = None;
    let mut rank = 0;
    for (index, (value, score)) in ordered.into_iter().enumerate() {
        if previous_score != Some(score) {
            rank = index + 1;
            previous_score = Some(score);
        }
        let entry = target
            .entry(value.chunk_id.clone())
            .or_insert(RankedCandidate {
                value,
                vector_rank: None,
                lexical_rank: None,
                score: 0.0,
            });
        if vector {
            entry.vector_rank = Some(rank);
        } else {
            entry.lexical_rank = Some(rank);
        }
    }
    Ok(())
}

async fn search_candidates(
    table: &Table,
    query: &str,
    vector: &[f32],
    filter: &str,
    mode: u32,
    alpha: f32,
    limit: usize,
) -> NativeResult<Vec<RankedCandidate>> {
    let candidate_limit = (limit * 4).clamp(limit, 200);
    let mut candidates = HashMap::new();

    if mode == 2 || (mode == 0 && alpha > 0.0) {
        let base = if filter.is_empty() {
            table.query()
        } else {
            table.query().only_if(filter)
        };
        let stream = native(
            base.limit(candidate_limit)
                .nearest_to(vector)
                .map_err(|_| NativeError::invalid("The search vector was invalid."))?
                .distance_type(DistanceType::Cosine)
                .execute()
                .await,
        )?;
        let batches = native(stream.try_collect::<Vec<_>>().await)?;
        merge_candidates(&mut candidates, &batches, true)?;
    }

    if (mode == 1 || (mode == 0 && alpha < 1.0)) && !query.trim().is_empty() {
        ensure_fts(table).await?;
        let base = if filter.is_empty() {
            table.query()
        } else {
            table.query().only_if(filter)
        };
        let stream = native(
            base.full_text_search(FullTextSearchQuery::new(query.to_owned()))
                .limit(candidate_limit)
                .execute()
                .await,
        )?;
        let batches = native(stream.try_collect::<Vec<_>>().await)?;
        merge_candidates(&mut candidates, &batches, false)?;
    }

    for value in candidates.values_mut() {
        let vector_score = value
            .vector_rank
            .map_or(0.0, |rank| 1.0 / (60.0 + rank as f64));
        let lexical_score = value
            .lexical_rank
            .map_or(0.0, |rank| 1.0 / (60.0 + rank as f64));
        value.score = match mode {
            1 => lexical_score,
            2 => vector_score,
            _ => f64::from(alpha) * vector_score + f64::from(1.0 - alpha) * lexical_score,
        };
    }
    let mut values = candidates.into_values().collect::<Vec<_>>();
    values.sort_by(|left, right| {
        right
            .score
            .total_cmp(&left.score)
            .then_with(|| left.value.chunk_id.cmp(&right.value.chunk_id))
    });
    values.truncate(limit);
    Ok(values)
}

fn encode_results(results: &[RankedCandidate]) -> NativeResult<Vec<u8>> {
    let fields = vec![
        Field::new("chunkId", DataType::Utf8, false),
        Field::new("sourceId", DataType::Utf8, false),
        Field::new("relativePath", DataType::Utf8, false),
        Field::new("language", DataType::Utf8, false),
        Field::new("symbolName", DataType::Utf8, true),
        Field::new("startLine", DataType::Int32, false),
        Field::new("endLine", DataType::Int32, false),
        Field::new("score", DataType::Float64, false),
        Field::new("content", DataType::Utf8, false),
        Field::new("contentHash", DataType::Utf8, false),
        Field::new("lastIndexedUtc", DataType::Utf8, false),
        Field::new("chunkKind", DataType::Utf8, false),
        Field::new("qualifiedSymbolName", DataType::Utf8, true),
        Field::new("structuralLocator", DataType::Utf8, false),
        Field::new("chunkerId", DataType::Utf8, false),
        Field::new("chunkerVersion", DataType::Utf8, false),
        Field::new("chunkProfileFingerprint", DataType::Utf8, false),
    ];
    let schema = Arc::new(Schema::new(fields));
    let batch = arrow(RecordBatch::try_new(
        schema.clone(),
        vec![
            Arc::new(StringArray::from_iter_values(
                results.iter().map(|result| result.value.chunk_id.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results.iter().map(|result| result.value.source_id.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.relative_path.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results.iter().map(|result| result.value.language.as_str()),
            )),
            Arc::new(StringArray::from(
                results
                    .iter()
                    .map(|result| result.value.symbol_name.as_deref())
                    .collect::<Vec<_>>(),
            )),
            Arc::new(Int32Array::from_iter_values(
                results.iter().map(|result| result.value.start_line),
            )),
            Arc::new(Int32Array::from_iter_values(
                results.iter().map(|result| result.value.end_line),
            )),
            Arc::new(Float64Array::from_iter_values(
                results.iter().map(|result| result.score),
            )),
            Arc::new(StringArray::from_iter_values(
                results.iter().map(|result| result.value.content.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.content_hash.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.last_indexed_utc.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.chunk_kind.as_str()),
            )),
            Arc::new(StringArray::from(
                results
                    .iter()
                    .map(|result| result.value.qualified_symbol_name.as_deref())
                    .collect::<Vec<_>>(),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.structural_locator.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.chunker_id.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.chunker_version.as_str()),
            )),
            Arc::new(StringArray::from_iter_values(
                results
                    .iter()
                    .map(|result| result.value.chunk_profile_fingerprint.as_str()),
            )),
        ],
    ))?;
    let mut bytes = Vec::new();
    {
        let mut writer = arrow(StreamWriter::try_new(&mut bytes, &schema))?;
        arrow(writer.write(&batch))?;
        arrow(writer.finish())?;
    }
    if bytes.len() > MAX_IPC_BYTES {
        return Err(NativeError::failure(
            "The embedded LanceDB result exceeded its IPC bound.",
        ));
    }
    Ok(bytes)
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_abi_negotiate(
    requested: u32,
    selected: *mut u32,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        if selected.is_null() {
            return Err(NativeError::invalid("selected is required."));
        }
        if requested != ABI_V1 {
            return Err(NativeError {
                status: ABI_MISMATCH,
                message: "No compatible LocalRAG LanceDB ABI is available.",
            });
        }
        unsafe { *selected = ABI_V1 };
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_cancel_new(cancel: *mut *mut LragCancel, error: *mut *mut LragError) -> u32 {
    boundary(error, || {
        if cancel.is_null() {
            return Err(NativeError::invalid("cancel is required."));
        }
        unsafe {
            *cancel = Box::into_raw(Box::new(LragCancel {
                requested: AtomicBool::new(false),
            }))
        };
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_cancel_request(cancel: *mut LragCancel, error: *mut *mut LragError) -> u32 {
    boundary(error, || {
        let Some(cancel) = (unsafe { cancel.as_ref() }) else {
            return Err(NativeError::invalid("cancel is required."));
        };
        cancel.requested.store(true, Ordering::Release);
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_cancel_release(cancel: *mut LragCancel, error: *mut *mut LragError) -> u32 {
    boundary(error, || {
        if !cancel.is_null() {
            unsafe { drop(Box::from_raw(cancel)) };
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_open(
    request: *const LragOpenRequestV1,
    database: *mut *mut c_void,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        if request.is_null() || database.is_null() {
            return Err(NativeError::invalid(
                "The open request and database output are required.",
            ));
        }
        unsafe { *database = ptr::null_mut() };
        let request = unsafe { &*request };
        if request.struct_size != size_of::<LragOpenRequestV1>() as u32 {
            return Err(NativeError::invalid(
                "The LanceDB open request size was incompatible.",
            ));
        }
        let path = read_utf8(
            request.path,
            request.path_len,
            MAX_PATH_BYTES,
            false,
            "The LanceDB path was invalid.",
        )?;
        let table_name = read_utf8(
            request.table,
            request.table_len,
            MAX_IDENTIFIER_BYTES,
            false,
            "The LanceDB table name was invalid.",
        )?;
        validate_identifier(&table_name)?;
        let ownership_id = read_utf8(
            request.ownership_id,
            request.ownership_id_len,
            MAX_IDENTIFIER_BYTES,
            false,
            "The LanceDB ownership ID was invalid.",
        )?;
        let runtime = Builder::new_multi_thread()
            .enable_all()
            .thread_name("localrag-lancedb")
            .build()
            .map_err(|_| NativeError::failure("The embedded LanceDB runtime could not start."))?;
        let connection = runtime.block_on(async { native(connect(&path).execute().await) })?;
        let handle = Box::new(LragDb {
            runtime,
            connection,
            table_name,
            ownership_id,
            operations: Mutex::new(()),
        });
        unsafe { *database = Box::into_raw(handle).cast::<c_void>() };
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_close(database: *mut c_void, error: *mut *mut LragError) -> u32 {
    boundary(error, || {
        if !database.is_null() {
            unsafe { drop(Box::from_raw(database.cast::<LragDb>())) };
        }
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_ensure_ready(
    database: *mut c_void,
    cancel: *mut LragCancel,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        cancelled(cancel)?;
        let db = db_ref(database)?;
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        db.runtime.block_on(async { ensure_table(db).await })?;
        cancelled(cancel)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_upsert_ipc(
    database: *mut c_void,
    ipc: *const u8,
    ipc_len: usize,
    cancel: *mut LragCancel,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        cancelled(cancel)?;
        let bytes = read_bytes(ipc, ipc_len)?;
        let batches = decode_batches(bytes, &document_fields())?;
        if batches.is_empty() {
            return Err(NativeError::invalid(
                "The upsert Arrow IPC payload contained no rows.",
            ));
        }
        let schema = batches[0].schema();
        let reader = RecordBatchIterator::new(batches.into_iter().map(Ok), schema);
        let db = db_ref(database)?;
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        db.runtime.block_on(async {
            let table = ensure_table(db).await?;
            let mut merge = table.merge_insert(&["chunkId"]);
            merge
                .when_matched_update_all(None)
                .when_not_matched_insert_all();
            native(merge.execute(Box::new(reader)).await)?;
            ensure_fts(&table).await?;
            ensure_vector_index(&table).await
        })?;
        cancelled(cancel)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_delete_chunks(
    database: *mut c_void,
    ipc: *const u8,
    ipc_len: usize,
    cancel: *mut LragCancel,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        cancelled(cancel)?;
        let ids = decode_ids(read_bytes(ipc, ipc_len)?)?;
        let unique = ids.into_iter().collect::<HashSet<_>>();
        if unique.is_empty() {
            return Ok(());
        }
        let predicate = format!(
            "chunkId IN ({})",
            unique
                .iter()
                .map(|value| sql_string(value))
                .collect::<Vec<_>>()
                .join(",")
        );
        let db = db_ref(database)?;
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        db.runtime.block_on(async {
            let table = ensure_table(db).await?;
            native(table.delete(&predicate).await)?;
            Ok(())
        })?;
        cancelled(cancel)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_delete_source(
    database: *mut c_void,
    source_id: *const u8,
    source_id_len: usize,
    cancel: *mut LragCancel,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        cancelled(cancel)?;
        let source = read_utf8(
            source_id,
            source_id_len,
            MAX_IDENTIFIER_BYTES,
            false,
            "The source ID was invalid.",
        )?;
        let db = db_ref(database)?;
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        db.runtime.block_on(async {
            let table = ensure_table(db).await?;
            native(
                table
                    .delete(&format!("sourceId = {}", sql_string(&source)))
                    .await,
            )?;
            Ok(())
        })?;
        cancelled(cancel)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_search(
    database: *mut c_void,
    request: *const LragSearchRequestV1,
    cancel: *mut LragCancel,
    result: *mut *mut LragBuffer,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        cancelled(cancel)?;
        if request.is_null() || result.is_null() {
            return Err(NativeError::invalid(
                "The search request and result output are required.",
            ));
        }
        unsafe { *result = ptr::null_mut() };
        let request = unsafe { &*request };
        if request.struct_size != size_of::<LragSearchRequestV1>() as u32
            || request.limit == 0
            || request.limit > 50
            || request.mode > 2
            || !request.alpha.is_finite()
            || !(0.0..=1.0).contains(&request.alpha)
        {
            return Err(NativeError::invalid(
                "The LanceDB search request was invalid.",
            ));
        }
        let query = read_utf8(
            request.query,
            request.query_len,
            MAX_QUERY_BYTES,
            true,
            "The search query was invalid.",
        )?;
        let language = read_utf8(
            request.language,
            request.language_len,
            MAX_IDENTIFIER_BYTES,
            true,
            "The language filter was invalid.",
        )?;
        let path_prefix = read_utf8(
            request.path_prefix,
            request.path_prefix_len,
            MAX_PATH_BYTES,
            true,
            "The path-prefix filter was invalid.",
        )?;
        let source_ids = if request.source_ids_ipc_len == 0 {
            Vec::new()
        } else {
            decode_ids(read_bytes(
                request.source_ids_ipc,
                request.source_ids_ipc_len,
            )?)?
        };
        if request.vector_len != VECTOR_DIMENSIONS || request.vector.is_null() {
            return Err(NativeError::invalid(
                "The search vector must contain exactly 384 values.",
            ));
        }
        let vector = unsafe { slice::from_raw_parts(request.vector, request.vector_len) };
        if vector.iter().any(|value| !value.is_finite()) {
            return Err(NativeError::invalid(
                "The search vector contained a non-finite value.",
            ));
        }
        let filter = build_filter(&source_ids, &language, &path_prefix);
        let db = db_ref(database)?;
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        let candidates = db.runtime.block_on(async {
            let table = ensure_table(db).await?;
            if native(table.count_rows(None).await)? == 0 {
                Ok(Vec::new())
            } else {
                search_candidates(
                    &table,
                    &query,
                    vector,
                    &filter,
                    request.mode,
                    request.alpha,
                    request.limit as usize,
                )
                .await
            }
        })?;
        let bytes = encode_results(&candidates)?;
        cancelled(cancel)?;
        unsafe { *result = Box::into_raw(Box::new(LragBuffer { bytes })) };
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_verify_ownership(
    database: *mut c_void,
    ownership_id: *const u8,
    ownership_id_len: usize,
    owned: *mut u8,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        if owned.is_null() {
            return Err(NativeError::invalid("The ownership output is required."));
        }
        unsafe { *owned = 0 };
        let ownership = read_utf8(
            ownership_id,
            ownership_id_len,
            MAX_IDENTIFIER_BYTES,
            false,
            "The ownership ID was invalid.",
        )?;
        let db = db_ref(database)?;
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        let is_owned =
            db.runtime.block_on(async {
                let names = native(db.connection.table_names().execute().await)?;
                if !names.iter().any(|name| name == &db.table_name) {
                    return Ok(false);
                }
                let table = native(db.connection.open_table(&db.table_name).execute().await)?;
                let schema = native(table.schema().await)?;
                Ok(schema.metadata().get(OWNERSHIP_KEY).map(String::as_str)
                    == Some(ownership.as_str())
                    && schema
                        .metadata()
                        .get(SCHEMA_VERSION_KEY)
                        .map(String::as_str)
                        == Some(SCHEMA_VERSION))
            })?;
        unsafe { *owned = u8::from(is_owned) };
        Ok(())
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_reset_owned(
    database: *mut c_void,
    ownership_id: *const u8,
    ownership_id_len: usize,
    cancel: *mut LragCancel,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        cancelled(cancel)?;
        let ownership = read_utf8(
            ownership_id,
            ownership_id_len,
            MAX_IDENTIFIER_BYTES,
            false,
            "The ownership ID was invalid.",
        )?;
        let db = db_ref(database)?;
        if ownership != db.ownership_id {
            return Err(NativeError::not_owned());
        }
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        db.runtime.block_on(async {
            let table = ensure_table(db).await?;
            validate_schema(&native(table.schema().await)?, &ownership)?;
            native(db.connection.drop_table(&db.table_name, &[]).await)?;
            native(
                db.connection
                    .create_empty_table(&db.table_name, owned_schema(&ownership))
                    .execute()
                    .await,
            )?;
            Ok(())
        })?;
        cancelled(cancel)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_db_optimize(
    database: *mut c_void,
    cancel: *mut LragCancel,
    error: *mut *mut LragError,
) -> u32 {
    boundary(error, || {
        cancelled(cancel)?;
        let db = db_ref(database)?;
        let _operation = db
            .operations
            .lock()
            .map_err(|_| NativeError::failure("The embedded LanceDB operation lock failed."))?;
        db.runtime.block_on(async {
            let table = ensure_table(db).await?;
            ensure_fts(&table).await?;
            ensure_vector_index(&table).await?;
            native(table.optimize(OptimizeAction::All).await)?;
            Ok(())
        })?;
        cancelled(cancel)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_buffer_data(buffer: *const LragBuffer) -> *const u8 {
    unsafe { buffer.as_ref() }.map_or(ptr::null(), |value| value.bytes.as_ptr())
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_buffer_len(buffer: *const LragBuffer) -> usize {
    unsafe { buffer.as_ref() }.map_or(0, |value| value.bytes.len())
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_buffer_release(buffer: *mut LragBuffer) {
    if !buffer.is_null() {
        unsafe { drop(Box::from_raw(buffer)) };
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_error_code(error: *const LragError) -> u32 {
    unsafe { error.as_ref() }.map_or(0, |value| value.code)
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_error_message(error: *const LragError) -> *const u8 {
    unsafe { error.as_ref() }.map_or(ptr::null(), |value| value.message.as_ptr())
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_error_message_len(error: *const LragError) -> usize {
    unsafe { error.as_ref() }.map_or(0, |value| value.message.len())
}

#[unsafe(no_mangle)]
pub extern "C" fn lrag_error_release(error: *mut LragError) {
    if !error.is_null() {
        unsafe { drop(Box::from_raw(error)) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use arrow::array::{FixedSizeListArray, Float32Array};
    use tempfile::TempDir;

    fn write_ipc(batch: &RecordBatch) -> Vec<u8> {
        let mut bytes = Vec::new();
        {
            let mut writer = StreamWriter::try_new(&mut bytes, &batch.schema()).unwrap();
            writer.write(batch).unwrap();
            writer.finish().unwrap();
        }
        bytes
    }

    fn document_ipc(chunk_id: &str, source_id: &str, content: &str) -> Vec<u8> {
        let fields = document_fields();
        let schema = Arc::new(Schema::new(fields));
        let vector_values = Arc::new(Float32Array::from_iter_values(
            (0..VECTOR_DIMENSIONS).map(|index| if index == 0 { 1.0 } else { 0.0 }),
        ));
        let vector = FixedSizeListArray::new(
            Arc::new(Field::new("item", DataType::Float32, true)),
            VECTOR_DIMENSIONS as i32,
            vector_values,
            None,
        );
        let string = |value: &str| Arc::new(StringArray::from(vec![value])) as Arc<dyn Array>;
        let nullable =
            |value: Option<&str>| Arc::new(StringArray::from(vec![value])) as Arc<dyn Array>;
        let integer = |value: i32| Arc::new(Int32Array::from(vec![value])) as Arc<dyn Array>;
        let batch = RecordBatch::try_new(
            schema,
            vec![
                string(chunk_id),
                string(source_id),
                string("file-1"),
                string("src/example.cs"),
                string("csharp"),
                nullable(Some("Example")),
                integer(1),
                integer(4),
                integer(0),
                string(content),
                string("hash-1"),
                integer(8),
                string("embedding-v1"),
                string("2026-07-26T12:00:00.0000000+00:00"),
                string("symbol"),
                nullable(Some("Example.Run")),
                string("symbol:Example.Run"),
                string("csharp"),
                string("1"),
                string("profile-1"),
                Arc::new(vector),
            ],
        )
        .unwrap();
        write_ipc(&batch)
    }

    fn ids_ipc(ids: &[&str]) -> Vec<u8> {
        let schema = Arc::new(Schema::new(vec![Field::new("id", DataType::Utf8, false)]));
        let batch =
            RecordBatch::try_new(schema, vec![Arc::new(StringArray::from(ids.to_vec()))]).unwrap();
        write_ipc(&batch)
    }

    fn open_database(root: &TempDir) -> (*mut c_void, Vec<u8>, Vec<u8>) {
        let path = root.path().to_string_lossy().as_bytes().to_vec();
        let table = b"rag_chunks_v1".to_vec();
        let owner = b"owner_1".to_vec();
        let request = LragOpenRequestV1 {
            struct_size: size_of::<LragOpenRequestV1>() as u32,
            path: path.as_ptr(),
            path_len: path.len(),
            table: table.as_ptr(),
            table_len: table.len(),
            ownership_id: owner.as_ptr(),
            ownership_id_len: owner.len(),
        };
        let mut database = ptr::null_mut();
        let mut error = ptr::null_mut();
        assert_eq!(OK, lrag_db_open(&request, &mut database, &mut error));
        assert!(!database.is_null());
        (database, owner, table)
    }

    #[test]
    fn negotiates_only_v1() {
        let mut selected = 0;
        let mut error = ptr::null_mut();
        assert_eq!(OK, lrag_abi_negotiate(ABI_V1, &mut selected, &mut error));
        assert_eq!(ABI_V1, selected);
        assert!(error.is_null());
        assert_eq!(
            ABI_MISMATCH,
            lrag_abi_negotiate(2, &mut selected, &mut error)
        );
        lrag_error_release(error);
    }

    #[test]
    fn ffi_error_accessors_return_the_original_error() {
        let mut selected = 0;
        let mut error = ptr::null_mut();
        assert_eq!(
            ABI_MISMATCH,
            lrag_abi_negotiate(2, &mut selected, &mut error)
        );
        assert!(!error.is_null());
        assert_eq!(ABI_MISMATCH, lrag_error_code(error));
        let message = unsafe {
            slice::from_raw_parts(lrag_error_message(error), lrag_error_message_len(error))
        };
        assert_eq!(
            "No compatible LocalRAG LanceDB ABI is available.",
            std::str::from_utf8(message).unwrap()
        );
        lrag_error_release(error);
    }

    #[test]
    fn cancellation_handle_is_owned_and_releasable() {
        let mut cancel = ptr::null_mut();
        let mut error = ptr::null_mut();
        assert_eq!(OK, lrag_cancel_new(&mut cancel, &mut error));
        assert_eq!(OK, lrag_cancel_request(cancel, &mut error));
        assert_eq!(OK, lrag_cancel_release(cancel, &mut error));
    }

    #[test]
    fn filter_values_are_escaped_as_data() {
        let filter = build_filter(&["source' OR true --".to_owned()], "c#", r"src\100%_done");
        assert_eq!(
            "sourceId IN ('source'' OR true --') AND language = 'c#' AND relativePath LIKE 'src\\\\100\\%\\_done%' ESCAPE '\\'",
            filter
        );
    }

    #[test]
    fn ffi_crud_search_and_owned_reset_preserve_unrelated_tables() {
        let root = tempfile::tempdir().unwrap();
        let (database, owner, _) = open_database(&root);
        let mut error = ptr::null_mut();
        assert_eq!(
            OK,
            lrag_db_ensure_ready(database, ptr::null_mut(), &mut error)
        );

        let db = unsafe { &*(database.cast::<LragDb>()) };
        db.runtime
            .block_on(async {
                db.connection
                    .create_empty_table(
                        "unrelated",
                        Arc::new(Schema::new(vec![Field::new(
                            "value",
                            DataType::Utf8,
                            false,
                        )])),
                    )
                    .execute()
                    .await
            })
            .unwrap();

        let documents = document_ipc(
            "chunk-1",
            "source-1",
            "The embedded database can search the world locally.",
        );
        assert_eq!(
            OK,
            lrag_db_upsert_ipc(
                database,
                documents.as_ptr(),
                documents.len(),
                ptr::null_mut(),
                &mut error
            )
        );
        assert_eq!(OK, lrag_db_optimize(database, ptr::null_mut(), &mut error));
        let indices = db
            .runtime
            .block_on(async {
                db.connection
                    .open_table("rag_chunks_v1")
                    .execute()
                    .await
                    .unwrap()
                    .list_indices()
                    .await
            })
            .unwrap();
        assert!(indices.iter().any(|index| index.columns == ["content"]));
        assert!(indices.iter().any(|index| index.columns == ["vector"]));

        let tail = document_ipc(
            "chunk-tail",
            "source-1",
            "The zebracorn token exists only in the unindexed tail.",
        );
        assert_eq!(
            OK,
            lrag_db_upsert_ipc(
                database,
                tail.as_ptr(),
                tail.len(),
                ptr::null_mut(),
                &mut error
            )
        );

        let query = b"search world";
        let language = b"csharp";
        let prefix = b"src/";
        let sources = ids_ipc(&["source-1"]);
        let vector = [1.0f32; VECTOR_DIMENSIONS];
        let request = LragSearchRequestV1 {
            struct_size: size_of::<LragSearchRequestV1>() as u32,
            query: query.as_ptr(),
            query_len: query.len(),
            language: language.as_ptr(),
            language_len: language.len(),
            path_prefix: prefix.as_ptr(),
            path_prefix_len: prefix.len(),
            source_ids_ipc: sources.as_ptr(),
            source_ids_ipc_len: sources.len(),
            vector: vector.as_ptr(),
            vector_len: vector.len(),
            limit: 5,
            alpha: 0.65,
            mode: 0,
        };
        let mut result = ptr::null_mut();
        assert_eq!(
            OK,
            lrag_db_search(database, &request, ptr::null_mut(), &mut result, &mut error)
        );
        let result_bytes = unsafe {
            slice::from_raw_parts(lrag_buffer_data(result), lrag_buffer_len(result)).to_vec()
        };
        lrag_buffer_release(result);
        let mut reader = StreamReader::try_new(Cursor::new(result_bytes), None).unwrap();
        let batch = reader.next().unwrap().unwrap();
        let chunk_ids = batch
            .column_by_name("chunkId")
            .unwrap()
            .as_any()
            .downcast_ref::<StringArray>()
            .unwrap();
        assert!(chunk_ids.iter().flatten().any(|value| value == "chunk-1"));

        let tail_query = b"zebracorn";
        let lexical_request = LragSearchRequestV1 {
            query: tail_query.as_ptr(),
            query_len: tail_query.len(),
            mode: 1,
            ..request
        };
        result = ptr::null_mut();
        assert_eq!(
            OK,
            lrag_db_search(
                database,
                &lexical_request,
                ptr::null_mut(),
                &mut result,
                &mut error
            )
        );
        let tail_result = unsafe {
            slice::from_raw_parts(lrag_buffer_data(result), lrag_buffer_len(result)).to_vec()
        };
        lrag_buffer_release(result);
        let mut reader = StreamReader::try_new(Cursor::new(tail_result), None).unwrap();
        let batch = reader.next().unwrap().unwrap();
        let chunk_ids = batch
            .column_by_name("chunkId")
            .unwrap()
            .as_any()
            .downcast_ref::<StringArray>()
            .unwrap();
        assert_eq!("chunk-tail", chunk_ids.value(0));

        let delete = ids_ipc(&["chunk-1"]);
        assert_eq!(
            OK,
            lrag_db_delete_chunks(
                database,
                delete.as_ptr(),
                delete.len(),
                ptr::null_mut(),
                &mut error
            )
        );

        let wrong_owner = b"someone_else";
        assert_eq!(
            NOT_OWNED,
            lrag_db_reset_owned(
                database,
                wrong_owner.as_ptr(),
                wrong_owner.len(),
                ptr::null_mut(),
                &mut error
            )
        );
        lrag_error_release(error);
        error = ptr::null_mut();
        assert_eq!(
            OK,
            lrag_db_reset_owned(
                database,
                owner.as_ptr(),
                owner.len(),
                ptr::null_mut(),
                &mut error
            )
        );
        let names = db
            .runtime
            .block_on(async { db.connection.table_names().execute().await })
            .unwrap();
        assert!(names.iter().any(|name| name == "unrelated"));
        assert!(names.iter().any(|name| name == "rag_chunks_v1"));
        assert_eq!(OK, lrag_db_close(database, &mut error));
    }

    #[test]
    fn cancelled_operation_fails_before_mutation() {
        let root = tempfile::tempdir().unwrap();
        let (database, _, _) = open_database(&root);
        let mut error = ptr::null_mut();
        let mut cancel = ptr::null_mut();
        assert_eq!(OK, lrag_cancel_new(&mut cancel, &mut error));
        assert_eq!(OK, lrag_cancel_request(cancel, &mut error));
        let documents = document_ipc("chunk-cancelled", "source-1", "cancel me");
        assert_eq!(
            CANCELLED,
            lrag_db_upsert_ipc(
                database,
                documents.as_ptr(),
                documents.len(),
                cancel,
                &mut error
            )
        );
        lrag_error_release(error);
        error = ptr::null_mut();
        let db = unsafe { &*(database.cast::<LragDb>()) };
        let names = db
            .runtime
            .block_on(async { db.connection.table_names().execute().await })
            .unwrap();
        assert!(!names.iter().any(|name| name == "rag_chunks_v1"));
        assert_eq!(OK, lrag_cancel_release(cancel, &mut error));
        assert_eq!(OK, lrag_db_close(database, &mut error));
    }
}
