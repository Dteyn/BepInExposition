#!/usr/bin/env python3
"""Build a queryable SQLite index from BepInExposition JSONL output."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import sqlite3
import sys
from pathlib import Path
from typing import Any, Iterable


KNOWN_FILES = {
    "scenes": "scenes.jsonl",
    "objects": "objects.jsonl",
    "component_types": "component_types.jsonl",
    "object_components": "object_components.jsonl",
    "types": "types.jsonl",
    "member_shapes": "member_shapes.jsonl",
    "member_values": "member_values.jsonl",
}

PERFORMANCE_INDEXES = {
    "idx_objects_scene_path": "CREATE INDEX IF NOT EXISTS idx_objects_scene_path ON objects(scene_name, transform_path)",
    "idx_components_type": "CREATE INDEX IF NOT EXISTS idx_components_type ON object_components(type_full_name)",
    "idx_components_slot": "CREATE INDEX IF NOT EXISTS idx_components_slot ON object_components(object_semantic_key, component_index, type_full_name)",
    "idx_member_shapes_observed": "CREATE INDEX IF NOT EXISTS idx_member_shapes_observed ON member_shapes(observed_type_full_name, member_name)",
    "idx_member_values_component": "CREATE INDEX IF NOT EXISTS idx_member_values_component ON member_values(component_type_full_name, member_name)",
    "idx_member_values_object": "CREATE INDEX IF NOT EXISTS idx_member_values_object ON member_values(object_semantic_key)",
    "idx_raw_records_type": "CREATE INDEX IF NOT EXISTS idx_raw_records_type ON raw_records(record_type)",
    "idx_raw_records_session": "CREATE INDEX IF NOT EXISTS idx_raw_records_session ON raw_records(session_id)",
    "idx_env_plugin_name": "CREATE INDEX IF NOT EXISTS idx_env_plugin_name ON environment_plugins(name, guid)",
    "idx_env_config_key": "CREATE INDEX IF NOT EXISTS idx_env_config_key ON environment_config(section, key)",
    "idx_env_interop_assembly": "CREATE INDEX IF NOT EXISTS idx_env_interop_assembly ON environment_interop_assemblies(assembly_name, file_name)",
    "idx_codex_search_kind": "CREATE INDEX IF NOT EXISTS idx_codex_search_kind ON codex_search(entity_kind, display_name)",
    "idx_codex_search_type": "CREATE INDEX IF NOT EXISTS idx_codex_search_type ON codex_search(type_name, member_name)",
}


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def get_value(data: dict[str, Any], *names: str, default: Any = None) -> Any:
    for name in names:
        if name in data:
            return data[name]
    return default


def as_bool(value: Any) -> int | None:
    if value is None:
        return None
    if isinstance(value, bool):
        return 1 if value else 0
    if isinstance(value, (int, float)):
        return 1 if value else 0
    if isinstance(value, str):
        lowered = value.strip().lower()
        if lowered in {"true", "1", "yes"}:
            return 1
        if lowered in {"false", "0", "no"}:
            return 0
    return None


def as_int(value: Any) -> int | None:
    if value is None or value == "":
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def json_text(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def relative_to(path: Path, root: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.as_posix()


def iter_jsonl(path: Path) -> Iterable[tuple[int, dict[str, Any] | None, str | None]]:
    with path.open("r", encoding="utf-8-sig") as handle:
        for line_number, line in enumerate(handle, start=1):
            stripped = line.strip()
            if not stripped:
                continue
            try:
                loaded = json.loads(stripped)
            except json.JSONDecodeError as ex:
                yield line_number, None, str(ex)
                continue
            if not isinstance(loaded, dict):
                yield line_number, None, "top-level JSON value is not an object"
                continue
            yield line_number, loaded, None


def connect_database(path: Path, rebuild: bool) -> sqlite3.Connection:
    if rebuild and path.exists():
        path.unlink()

    path.parent.mkdir(parents=True, exist_ok=True)
    connection = sqlite3.connect(path)
    connection.execute("PRAGMA journal_mode=OFF" if rebuild else "PRAGMA journal_mode=WAL")
    connection.execute("PRAGMA synchronous=OFF" if rebuild else "PRAGMA synchronous=NORMAL")
    connection.execute("PRAGMA temp_store=MEMORY")
    connection.execute("PRAGMA cache_size=-200000")
    connection.execute("PRAGMA foreign_keys=OFF")
    create_schema(connection)
    return connection


def create_schema(connection: sqlite3.Connection) -> None:
    connection.executescript(
        """
        CREATE TABLE IF NOT EXISTS import_runs (
            id INTEGER PRIMARY KEY,
            imported_at_utc TEXT NOT NULL,
            output_root TEXT NOT NULL,
            include_sessions INTEGER NOT NULL,
            known_records INTEGER NOT NULL DEFAULT 0,
            environment_records INTEGER NOT NULL DEFAULT 0,
            manifest_records INTEGER NOT NULL DEFAULT 0,
            raw_records INTEGER NOT NULL DEFAULT 0,
            parse_errors INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS parse_errors (
            id INTEGER PRIMARY KEY,
            import_run_id INTEGER NOT NULL,
            source_file TEXT NOT NULL,
            line_number INTEGER NOT NULL,
            message TEXT NOT NULL,
            FOREIGN KEY(import_run_id) REFERENCES import_runs(id)
        );

        CREATE TABLE IF NOT EXISTS known_records (
            category TEXT NOT NULL,
            key TEXT NOT NULL,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            source_file TEXT NOT NULL,
            line_number INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            imported_at_utc TEXT NOT NULL,
            PRIMARY KEY(category, key)
        );

        CREATE TABLE IF NOT EXISTS scenes (
            key TEXT PRIMARY KEY,
            scene_name TEXT,
            scene_path TEXT,
            build_index INTEGER,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS objects (
            key TEXT PRIMARY KEY,
            semantic_key TEXT,
            scene_name TEXT,
            transform_path TEXT,
            object_name TEXT,
            tag TEXT,
            layer INTEGER,
            child_count INTEGER,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS component_types (
            key TEXT PRIMARY KEY,
            type_full_name TEXT,
            assembly_name TEXT,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS object_components (
            key TEXT PRIMARY KEY,
            object_semantic_key TEXT,
            transform_path TEXT,
            component_index INTEGER,
            type_full_name TEXT,
            assembly_name TEXT,
            enabled_state TEXT,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS types (
            key TEXT PRIMARY KEY,
            snapshot_id TEXT,
            type_full_name TEXT,
            assembly_name TEXT,
            namespace TEXT,
            base_type_full_name TEXT,
            is_public INTEGER,
            is_sealed INTEGER,
            is_abstract INTEGER,
            is_interface INTEGER,
            is_enum INTEGER,
            inheritance_chain TEXT,
            enum_underlying_type TEXT,
            enum_values TEXT,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS member_shapes (
            key TEXT PRIMARY KEY,
            snapshot_id TEXT,
            observed_type_full_name TEXT,
            declaring_type_full_name TEXT,
            member_name TEXT,
            member_kind TEXT,
            value_type_full_name TEXT,
            is_static INTEGER,
            is_public INTEGER,
            can_read INTEGER,
            can_write INTEGER,
            parameter_count INTEGER,
            parameter_types TEXT,
            accessibility TEXT,
            getter_accessibility TEXT,
            setter_accessibility TEXT,
            is_init_only INTEGER,
            is_backing_field INTEGER,
            is_virtual INTEGER,
            is_abstract INTEGER,
            is_constructor INTEGER,
            parameters_json TEXT,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS member_values (
            key TEXT PRIMARY KEY,
            snapshot_id TEXT,
            object_semantic_key TEXT,
            transform_path TEXT,
            component_index INTEGER,
            component_type_full_name TEXT,
            assembly_name TEXT,
            declaring_type_full_name TEXT,
            member_name TEXT,
            member_kind TEXT,
            value_type_full_name TEXT,
            value_kind TEXT,
            serialized_value TEXT,
            referenced_object_runtime_id INTEGER,
            referenced_object_name TEXT,
            referenced_object_type_full_name TEXT,
            referenced_object_native_pointer TEXT,
            first_seen_session_id TEXT,
            first_seen_at_utc TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS raw_records (
            id INTEGER PRIMARY KEY,
            record_type TEXT,
            session_id TEXT,
            captured_at_utc TEXT,
            source_file TEXT NOT NULL,
            line_number INTEGER NOT NULL,
            payload_json TEXT NOT NULL,
            UNIQUE(source_file, line_number)
        );

        CREATE TABLE IF NOT EXISTS runtime_environments (
            session_id TEXT PRIMARY KEY,
            source_file TEXT NOT NULL,
            generated_at_utc TEXT,
            plugin_name TEXT,
            plugin_version TEXT,
            game_version TEXT,
            unity_version TEXT,
            platform TEXT,
            bepinex_core_assembly_version TEXT,
            bepinex_core_informational_version TEXT,
            bepinex_version TEXT,
            bepinex_build_commit TEXT,
            bepinex_system_platform TEXT,
            bepinex_process_bitness TEXT,
            bepinex_runtime_version TEXT,
            bepinex_runtime_information TEXT,
            dotnet_framework_description TEXT,
            dotnet_runtime_identifier TEXT,
            os_description TEXT,
            os_architecture TEXT,
            process_architecture TEXT,
            process_name TEXT,
            is_64_bit_process INTEGER,
            is_64_bit_operating_system INTEGER,
            output_root TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS environment_plugins (
            session_id TEXT NOT NULL,
            guid TEXT,
            name TEXT,
            version TEXT,
            location TEXT,
            type_name TEXT,
            payload_json TEXT NOT NULL,
            PRIMARY KEY(session_id, guid, name, location),
            FOREIGN KEY(session_id) REFERENCES runtime_environments(session_id)
        );

        CREATE TABLE IF NOT EXISTS environment_config (
            session_id TEXT NOT NULL,
            section TEXT NOT NULL,
            key TEXT NOT NULL,
            value TEXT,
            default_value TEXT,
            description TEXT,
            acceptable_values TEXT,
            payload_json TEXT NOT NULL,
            PRIMARY KEY(session_id, section, key),
            FOREIGN KEY(session_id) REFERENCES runtime_environments(session_id)
        );

        CREATE TABLE IF NOT EXISTS environment_interop_assemblies (
            session_id TEXT NOT NULL,
            file_name TEXT NOT NULL,
            path TEXT NOT NULL,
            directory TEXT,
            size_bytes INTEGER,
            last_write_utc TEXT,
            assembly_name TEXT,
            payload_json TEXT NOT NULL,
            PRIMARY KEY(session_id, path),
            FOREIGN KEY(session_id) REFERENCES runtime_environments(session_id)
        );

        CREATE TABLE IF NOT EXISTS environment_bepinex_assemblies (
            session_id TEXT NOT NULL,
            file_name TEXT NOT NULL,
            path TEXT NOT NULL,
            size_bytes INTEGER,
            last_write_utc TEXT,
            assembly_name TEXT,
            file_version TEXT,
            product_version TEXT,
            payload_json TEXT NOT NULL,
            PRIMARY KEY(session_id, path),
            FOREIGN KEY(session_id) REFERENCES runtime_environments(session_id)
        );

        CREATE TABLE IF NOT EXISTS manifests (
            session_id TEXT PRIMARY KEY,
            source_file TEXT NOT NULL,
            generated_at_utc TEXT,
            plugin_name TEXT,
            plugin_version TEXT,
            started_at_utc TEXT,
            ended_at_utc TEXT,
            end_reason TEXT,
            output_root TEXT,
            raw_session_jsonl TEXT,
            summary_report TEXT,
            runtime_environment TEXT,
            payload_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS codex_search (
            id INTEGER PRIMARY KEY,
            entity_kind TEXT NOT NULL,
            entity_key TEXT NOT NULL,
            display_name TEXT,
            type_name TEXT,
            scene_name TEXT,
            transform_path TEXT,
            member_name TEXT,
            value_text TEXT,
            search_text TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS codex_catalog (
            object_name TEXT PRIMARY KEY,
            object_type TEXT NOT NULL,
            purpose TEXT NOT NULL,
            example_query TEXT NOT NULL
        );

        DROP VIEW IF EXISTS resolved_object_components;
        DROP VIEW IF EXISTS object_component_context;
        DROP VIEW IF EXISTS component_type_locations;
        DROP VIEW IF EXISTS type_member_shapes;
        DROP VIEW IF EXISTS member_value_context;
        DROP VIEW IF EXISTS scene_summary;
        DROP VIEW IF EXISTS component_type_summary;
        DROP VIEW IF EXISTS database_summary;
        DROP VIEW IF EXISTS codex_search_ranked;

        CREATE VIEW resolved_object_components AS
        SELECT oc.*
        FROM object_components oc
        WHERE oc.type_full_name <> 'UnityEngine.Component'
           OR NOT EXISTS (
                SELECT 1
                FROM object_components better
                WHERE better.object_semantic_key = oc.object_semantic_key
                  AND better.component_index = oc.component_index
                  AND better.type_full_name <> 'UnityEngine.Component'
           );

        CREATE VIEW object_component_context AS
        SELECT
            o.scene_name,
            o.transform_path,
            o.object_name,
            o.tag,
            o.layer,
            o.child_count,
            oc.component_index,
            oc.type_full_name AS component_type_full_name,
            oc.assembly_name AS component_assembly_name,
            oc.enabled_state,
            o.semantic_key AS object_semantic_key,
            o.payload_json AS object_payload_json,
            oc.payload_json AS component_payload_json
        FROM objects o
        JOIN resolved_object_components oc
          ON oc.object_semantic_key = o.semantic_key;

        CREATE VIEW component_type_locations AS
        SELECT
            component_type_full_name,
            component_assembly_name,
            scene_name,
            transform_path,
            object_name,
            component_index,
            enabled_state,
            object_semantic_key
        FROM object_component_context;

        CREATE VIEW type_member_shapes AS
        SELECT
            t.type_full_name,
            t.assembly_name,
            t.namespace,
            t.base_type_full_name,
            ms.declaring_type_full_name,
            ms.member_name,
            ms.member_kind,
            ms.value_type_full_name,
            ms.accessibility,
            ms.getter_accessibility,
            ms.setter_accessibility,
            ms.is_static,
            ms.can_read,
            ms.can_write,
            ms.parameter_count,
            ms.is_backing_field,
            ms.is_virtual,
            ms.is_abstract,
            ms.is_constructor,
            ms.parameters_json,
            ms.payload_json
        FROM types t
        JOIN member_shapes ms
          ON ms.observed_type_full_name = t.type_full_name;

        CREATE VIEW member_value_context AS
        SELECT
            mv.component_type_full_name,
            mv.assembly_name,
            mv.declaring_type_full_name,
            mv.member_name,
            mv.member_kind,
            mv.value_type_full_name,
            mv.value_kind,
            mv.serialized_value,
            mv.referenced_object_name,
            mv.referenced_object_type_full_name,
            o.scene_name,
            mv.transform_path,
            o.object_name,
            mv.component_index,
            mv.object_semantic_key,
            mv.payload_json
        FROM member_values mv
        LEFT JOIN objects o
          ON o.semantic_key = mv.object_semantic_key;

        CREATE VIEW scene_summary AS
        WITH object_counts AS (
            SELECT scene_name, COUNT(*) AS object_count
            FROM objects
            GROUP BY scene_name
        ),
        component_counts AS (
            SELECT o.scene_name, COUNT(oc.key) AS component_count, COUNT(DISTINCT oc.type_full_name) AS component_type_count
            FROM objects o
            JOIN resolved_object_components oc ON oc.object_semantic_key = o.semantic_key
            GROUP BY o.scene_name
        )
        SELECT
            s.scene_name,
            s.scene_path,
            s.build_index,
            COALESCE(object_counts.object_count, 0) AS object_count,
            COALESCE(component_counts.component_count, 0) AS component_count,
            COALESCE(component_counts.component_type_count, 0) AS component_type_count,
            s.first_seen_session_id,
            s.first_seen_at_utc
        FROM scenes s
        LEFT JOIN object_counts ON object_counts.scene_name = s.scene_name
        LEFT JOIN component_counts ON component_counts.scene_name = s.scene_name;

        CREATE VIEW component_type_summary AS
        WITH component_counts AS (
            SELECT
                oc.type_full_name,
                COUNT(DISTINCT oc.object_semantic_key) AS object_count,
                COUNT(oc.key) AS component_count,
                COUNT(DISTINCT o.scene_name) AS scene_count
            FROM resolved_object_components oc
            LEFT JOIN objects o ON o.semantic_key = oc.object_semantic_key
            GROUP BY oc.type_full_name
        ),
        shape_counts AS (
            SELECT observed_type_full_name AS type_full_name, COUNT(*) AS member_shape_count
            FROM member_shapes
            GROUP BY observed_type_full_name
        ),
        value_counts AS (
            SELECT component_type_full_name AS type_full_name, COUNT(*) AS member_value_count
            FROM member_values
            GROUP BY component_type_full_name
        )
        SELECT
            ct.type_full_name,
            ct.assembly_name,
            COALESCE(component_counts.object_count, 0) AS object_count,
            COALESCE(component_counts.component_count, 0) AS component_count,
            COALESCE(component_counts.scene_count, 0) AS scene_count,
            COALESCE(shape_counts.member_shape_count, 0) AS member_shape_count,
            COALESCE(value_counts.member_value_count, 0) AS member_value_count,
            ct.first_seen_session_id,
            ct.first_seen_at_utc
        FROM component_types ct
        LEFT JOIN component_counts ON component_counts.type_full_name = ct.type_full_name
        LEFT JOIN shape_counts ON shape_counts.type_full_name = ct.type_full_name
        LEFT JOIN value_counts ON value_counts.type_full_name = ct.type_full_name;

        CREATE VIEW database_summary AS
        SELECT 'scenes' AS category, COUNT(*) AS record_count FROM scenes
        UNION ALL SELECT 'objects', COUNT(*) FROM objects
        UNION ALL SELECT 'component_types', COUNT(*) FROM component_types
        UNION ALL SELECT 'object_components', COUNT(*) FROM object_components
        UNION ALL SELECT 'types', COUNT(*) FROM types
        UNION ALL SELECT 'member_shapes', COUNT(*) FROM member_shapes
        UNION ALL SELECT 'member_values', COUNT(*) FROM member_values
        UNION ALL SELECT 'runtime_environments', COUNT(*) FROM runtime_environments
        UNION ALL SELECT 'manifests', COUNT(*) FROM manifests
        UNION ALL SELECT 'codex_search', COUNT(*) FROM codex_search;

        CREATE VIEW codex_search_ranked AS
        SELECT
            CASE entity_kind
                WHEN 'type' THEN 10
                WHEN 'member_shape' THEN 20
                WHEN 'member_value' THEN 30
                WHEN 'component' THEN 40
                WHEN 'object' THEN 50
                WHEN 'environment' THEN 60
                ELSE 100
            END AS entity_priority,
            *
        FROM codex_search;
        """
    )
    ensure_columns(
        connection,
        "import_runs",
        {
            "environment_records": "INTEGER NOT NULL DEFAULT 0",
            "manifest_records": "INTEGER NOT NULL DEFAULT 0",
        },
    )
    ensure_columns(
        connection,
        "types",
        {
            "is_public": "INTEGER",
            "is_sealed": "INTEGER",
            "is_abstract": "INTEGER",
            "is_interface": "INTEGER",
            "is_enum": "INTEGER",
            "inheritance_chain": "TEXT",
            "enum_underlying_type": "TEXT",
            "enum_values": "TEXT",
        },
    )
    ensure_columns(
        connection,
        "member_shapes",
        {
            "accessibility": "TEXT",
            "getter_accessibility": "TEXT",
            "setter_accessibility": "TEXT",
            "is_init_only": "INTEGER",
            "is_backing_field": "INTEGER",
            "is_virtual": "INTEGER",
            "is_abstract": "INTEGER",
            "is_constructor": "INTEGER",
            "parameters_json": "TEXT",
        },
    )
    populate_codex_catalog(connection)


def populate_codex_catalog(connection: sqlite3.Connection) -> None:
    rows = [
        (
            "database_summary",
            "view",
            "Fast record counts for the packed runtime database.",
            "SELECT * FROM database_summary ORDER BY category;",
        ),
        (
            "scene_summary",
            "view",
            "One row per observed scene with object/component totals.",
            "SELECT * FROM scene_summary ORDER BY object_count DESC;",
        ),
        (
            "component_type_summary",
            "view",
            "One row per observed component type with location/member/value counts.",
            "SELECT * FROM component_type_summary ORDER BY object_count DESC LIMIT 25;",
        ),
        (
            "object_component_context",
            "view",
            "Joined object plus component placement context.",
            "SELECT * FROM object_component_context WHERE component_type_full_name LIKE '%Room%' LIMIT 50;",
        ),
        (
            "component_type_locations",
            "view",
            "Compact locations for each component type.",
            "SELECT scene_name, transform_path, component_index FROM component_type_locations WHERE component_type_full_name = ?;",
        ),
        (
            "type_member_shapes",
            "view",
            "Reflected fields/properties/methods joined to observed wrapper type metadata.",
            "SELECT member_kind, member_name, value_type_full_name FROM type_member_shapes WHERE type_full_name = ?;",
        ),
        (
            "member_value_context",
            "view",
            "Observed safe member values with object/scene context when available.",
            "SELECT * FROM member_value_context WHERE member_name LIKE '%Prefab%' LIMIT 50;",
        ),
        (
            "codex_search",
            "table",
            "Unified search rows over objects, components, types, member shapes, member values, and environments.",
            "SELECT entity_kind, display_name, type_name, scene_name, transform_path FROM codex_search WHERE search_text LIKE '%Mutator%' LIMIT 50;",
        ),
        (
            "codex_search_ranked",
            "view",
            "Priority-ordered search surface that puts types and member evidence before generic object matches.",
            "SELECT entity_kind, display_name, type_name, scene_name, transform_path FROM codex_search_ranked WHERE search_text LIKE '%Mutator%' ORDER BY entity_priority, entity_kind, display_name LIMIT 50;",
        ),
        (
            "codex_search_fts",
            "virtual table",
            "FTS5 full-text search mirror of codex_search when the local SQLite build supports FTS5.",
            "SELECT entity_kind, display_name, type_name, scene_name, transform_path FROM codex_search_fts WHERE codex_search_fts MATCH 'Mutator' LIMIT 50;",
        ),
        (
            "runtime_environments",
            "table",
            "Per-session BepInEx, game, Unity, .NET, platform, and process context.",
            "SELECT session_id, plugin_version, unity_version, bepinex_version, bepinex_runtime_information FROM runtime_environments ORDER BY generated_at_utc DESC;",
        ),
        (
            "environment_config",
            "table",
            "Effective BepInExposition config values captured for each session.",
            "SELECT section, key, value FROM environment_config WHERE session_id = ? ORDER BY section, key;",
        ),
    ]
    connection.execute("DELETE FROM codex_catalog")
    connection.executemany(
        """
        INSERT INTO codex_catalog(object_name, object_type, purpose, example_query)
        VALUES (?, ?, ?, ?)
        """,
        rows,
    )


def drop_performance_indexes(connection: sqlite3.Connection) -> None:
    for name in PERFORMANCE_INDEXES:
        connection.execute(f"DROP INDEX IF EXISTS {name}")


def create_performance_indexes(connection: sqlite3.Connection) -> None:
    for statement in PERFORMANCE_INDEXES.values():
        connection.execute(statement)


def ensure_columns(connection: sqlite3.Connection, table: str, columns: dict[str, str]) -> None:
    existing = {row[1] for row in connection.execute(f"PRAGMA table_info({table})")}
    for name, declaration in columns.items():
        if name not in existing:
            connection.execute(f"ALTER TABLE {table} ADD COLUMN {name} {declaration}")


def record_parse_error(
    connection: sqlite3.Connection,
    import_run_id: int,
    source_file: str,
    line_number: int,
    message: str,
) -> None:
    connection.execute(
        """
        INSERT INTO parse_errors(import_run_id, source_file, line_number, message)
        VALUES (?, ?, ?, ?)
        """,
        (import_run_id, source_file, line_number, message),
    )


def import_known_file(
    connection: sqlite3.Connection,
    import_run_id: int,
    root: Path,
    path: Path,
    category: str,
    keep_known_payloads: bool,
) -> tuple[int, int]:
    imported = 0
    errors = 0
    source_file = relative_to(path, root)
    imported_at = utc_now()

    if not path.exists():
        return imported, errors

    for line_number, record, error in iter_jsonl(path):
        if error is not None or record is None:
            record_parse_error(connection, import_run_id, source_file, line_number, error or "unknown parse error")
            errors += 1
            continue

        key = get_value(record, "key", "Key")
        payload = get_value(record, "payload", "Payload", default={})
        if not isinstance(payload, dict):
            payload = {"value": payload}

        if not key:
            record_parse_error(connection, import_run_id, source_file, line_number, "known record is missing key")
            errors += 1
            continue

        first_seen_session_id = get_value(record, "firstSeenSessionId", "FirstSeenSessionId")
        first_seen_at_utc = get_value(record, "firstSeenAtUtc", "FirstSeenAtUtc")
        payload_json = json_text(payload)
        known_payload_json = payload_json if keep_known_payloads else "{}"

        cursor = connection.execute(
            """
            INSERT OR IGNORE INTO known_records(
                category, key, first_seen_session_id, first_seen_at_utc,
                source_file, line_number, payload_json, imported_at_utc
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                category,
                key,
                first_seen_session_id,
                first_seen_at_utc,
                source_file,
                line_number,
                known_payload_json,
                imported_at,
            ),
        )
        if cursor.rowcount > 0:
            imported += 1

        upsert_typed_known(connection, category, str(key), first_seen_session_id, first_seen_at_utc, payload, payload_json)

    return imported, errors


def upsert_typed_known(
    connection: sqlite3.Connection,
    category: str,
    key: str,
    first_seen_session_id: str | None,
    first_seen_at_utc: str | None,
    payload: dict[str, Any],
    payload_json: str,
) -> None:
    if category == "scenes":
        connection.execute(
            """
            INSERT OR REPLACE INTO scenes(key, scene_name, scene_path, build_index, first_seen_session_id, first_seen_at_utc, payload_json)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                key,
                get_value(payload, "sceneName", "SceneName"),
                get_value(payload, "scenePath", "ScenePath"),
                as_int(get_value(payload, "buildIndex", "BuildIndex")),
                first_seen_session_id,
                first_seen_at_utc,
                payload_json,
            ),
        )
    elif category == "objects":
        connection.execute(
            """
            INSERT OR REPLACE INTO objects(
                key, semantic_key, scene_name, transform_path, object_name, tag, layer, child_count,
                first_seen_session_id, first_seen_at_utc, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                key,
                get_value(payload, "semanticKey", "SemanticKey"),
                get_value(payload, "sceneName", "SceneName"),
                get_value(payload, "transformPath", "TransformPath"),
                get_value(payload, "objectName", "ObjectName"),
                get_value(payload, "tag", "Tag"),
                as_int(get_value(payload, "layer", "Layer")),
                as_int(get_value(payload, "childCount", "ChildCount")),
                first_seen_session_id,
                first_seen_at_utc,
                payload_json,
            ),
        )
    elif category == "component_types":
        connection.execute(
            """
            INSERT OR REPLACE INTO component_types(key, type_full_name, assembly_name, first_seen_session_id, first_seen_at_utc, payload_json)
            VALUES (?, ?, ?, ?, ?, ?)
            """,
            (
                key,
                get_value(payload, "typeFullName", "TypeFullName"),
                get_value(payload, "assemblyName", "AssemblyName"),
                first_seen_session_id,
                first_seen_at_utc,
                payload_json,
            ),
        )
    elif category == "object_components":
        connection.execute(
            """
            INSERT OR REPLACE INTO object_components(
                key, object_semantic_key, transform_path, component_index, type_full_name, assembly_name,
                enabled_state, first_seen_session_id, first_seen_at_utc, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                key,
                get_value(payload, "objectSemanticKey", "ObjectSemanticKey"),
                get_value(payload, "transformPath", "TransformPath"),
                as_int(get_value(payload, "componentIndex", "ComponentIndex")),
                get_value(payload, "typeFullName", "TypeFullName"),
                get_value(payload, "assemblyName", "AssemblyName"),
                get_value(payload, "enabledState", "EnabledState"),
                first_seen_session_id,
                first_seen_at_utc,
                payload_json,
            ),
        )
    elif category == "types":
        connection.execute(
            """
            INSERT OR REPLACE INTO types(
                key, snapshot_id, type_full_name, assembly_name, namespace, base_type_full_name,
                is_public, is_sealed, is_abstract, is_interface, is_enum, inheritance_chain,
                enum_underlying_type, enum_values,
                first_seen_session_id, first_seen_at_utc, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                key,
                get_value(payload, "snapshotId", "SnapshotId"),
                get_value(payload, "typeFullName", "TypeFullName"),
                get_value(payload, "assemblyName", "AssemblyName"),
                get_value(payload, "namespace", "Namespace"),
                get_value(payload, "baseTypeFullName", "BaseTypeFullName"),
                as_bool(get_value(payload, "isPublic", "IsPublic")),
                as_bool(get_value(payload, "isSealed", "IsSealed")),
                as_bool(get_value(payload, "isAbstract", "IsAbstract")),
                as_bool(get_value(payload, "isInterface", "IsInterface")),
                as_bool(get_value(payload, "isEnum", "IsEnum")),
                json_text(get_value(payload, "inheritanceChain", "InheritanceChain", default=[])),
                get_value(payload, "enumUnderlyingType", "EnumUnderlyingType"),
                json_text(get_value(payload, "enumValues", "EnumValues", default=[])),
                first_seen_session_id,
                first_seen_at_utc,
                payload_json,
            ),
        )
    elif category == "member_shapes":
        connection.execute(
            """
            INSERT OR REPLACE INTO member_shapes(
                key, snapshot_id, observed_type_full_name, declaring_type_full_name, member_name,
                member_kind, value_type_full_name, is_static, is_public, can_read, can_write,
                parameter_count, parameter_types, accessibility, getter_accessibility, setter_accessibility,
                is_init_only, is_backing_field, is_virtual, is_abstract, is_constructor, parameters_json,
                first_seen_session_id, first_seen_at_utc, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                key,
                get_value(payload, "snapshotId", "SnapshotId"),
                get_value(payload, "observedTypeFullName", "ObservedTypeFullName"),
                get_value(payload, "declaringTypeFullName", "DeclaringTypeFullName"),
                get_value(payload, "memberName", "MemberName"),
                get_value(payload, "memberKind", "MemberKind"),
                get_value(payload, "valueTypeFullName", "ValueTypeFullName"),
                as_bool(get_value(payload, "isStatic", "IsStatic")),
                as_bool(get_value(payload, "isPublic", "IsPublic")),
                as_bool(get_value(payload, "canRead", "CanRead")),
                as_bool(get_value(payload, "canWrite", "CanWrite")),
                as_int(get_value(payload, "parameterCount", "ParameterCount")),
                get_value(payload, "parameterTypes", "ParameterTypes"),
                get_value(payload, "accessibility", "Accessibility"),
                get_value(payload, "getterAccessibility", "GetterAccessibility"),
                get_value(payload, "setterAccessibility", "SetterAccessibility"),
                as_bool(get_value(payload, "isInitOnly", "IsInitOnly")),
                as_bool(get_value(payload, "isBackingField", "IsBackingField")),
                as_bool(get_value(payload, "isVirtual", "IsVirtual")),
                as_bool(get_value(payload, "isAbstract", "IsAbstract")),
                as_bool(get_value(payload, "isConstructor", "IsConstructor")),
                json_text(get_value(payload, "parameters", "Parameters", default=[])),
                first_seen_session_id,
                first_seen_at_utc,
                payload_json,
            ),
        )
    elif category == "member_values":
        connection.execute(
            """
            INSERT OR REPLACE INTO member_values(
                key, snapshot_id, object_semantic_key, transform_path, component_index,
                component_type_full_name, assembly_name, declaring_type_full_name,
                member_name, member_kind, value_type_full_name, value_kind, serialized_value,
                referenced_object_runtime_id, referenced_object_name, referenced_object_type_full_name,
                referenced_object_native_pointer, first_seen_session_id, first_seen_at_utc, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                key,
                get_value(payload, "snapshotId", "SnapshotId"),
                get_value(payload, "objectSemanticKey", "ObjectSemanticKey"),
                get_value(payload, "transformPath", "TransformPath"),
                as_int(get_value(payload, "componentIndex", "ComponentIndex")),
                get_value(payload, "componentTypeFullName", "ComponentTypeFullName"),
                get_value(payload, "assemblyName", "AssemblyName"),
                get_value(payload, "declaringTypeFullName", "DeclaringTypeFullName"),
                get_value(payload, "memberName", "MemberName"),
                get_value(payload, "memberKind", "MemberKind"),
                get_value(payload, "valueTypeFullName", "ValueTypeFullName"),
                get_value(payload, "valueKind", "ValueKind"),
                get_value(payload, "serializedValue", "SerializedValue"),
                as_int(get_value(payload, "referencedObjectRuntimeId", "ReferencedObjectRuntimeId")),
                get_value(payload, "referencedObjectName", "ReferencedObjectName"),
                get_value(payload, "referencedObjectTypeFullName", "ReferencedObjectTypeFullName"),
                get_value(payload, "referencedObjectNativePointer", "ReferencedObjectNativePointer"),
                first_seen_session_id,
                first_seen_at_utc,
                payload_json,
            ),
        )


def import_raw_sessions(connection: sqlite3.Connection, import_run_id: int, root: Path) -> tuple[int, int]:
    imported = 0
    errors = 0
    sessions_dir = root / "sessions"
    if not sessions_dir.exists():
        return imported, errors

    for path in sorted(sessions_dir.glob("*.jsonl")):
        source_file = relative_to(path, root)
        for line_number, record, error in iter_jsonl(path):
            if error is not None or record is None:
                record_parse_error(connection, import_run_id, source_file, line_number, error or "unknown parse error")
                errors += 1
                continue

            cursor = connection.execute(
                """
                INSERT OR IGNORE INTO raw_records(record_type, session_id, captured_at_utc, source_file, line_number, payload_json)
                VALUES (?, ?, ?, ?, ?, ?)
                """,
                (
                    get_value(record, "type", "Type"),
                    get_value(record, "sessionId", "SessionId"),
                    get_value(record, "capturedAtUtc", "CapturedAtUtc"),
                    source_file,
                    line_number,
                    json_text(get_value(record, "payload", "Payload", default={})),
                ),
            )
            if cursor.rowcount > 0:
                imported += 1

    return imported, errors


def iter_json_files(paths: Iterable[Path]) -> Iterable[Path]:
    seen: set[Path] = set()
    for path in paths:
        resolved = path.resolve()
        if resolved in seen or not path.exists():
            continue
        seen.add(resolved)
        yield path


def read_json_file(
    connection: sqlite3.Connection,
    import_run_id: int,
    root: Path,
    path: Path,
) -> dict[str, Any] | None:
    try:
        loaded = json.loads(path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as ex:
        record_parse_error(connection, import_run_id, relative_to(path, root), 1, str(ex))
        return None
    except OSError as ex:
        record_parse_error(connection, import_run_id, relative_to(path, root), 1, str(ex))
        return None

    if not isinstance(loaded, dict):
        record_parse_error(connection, import_run_id, relative_to(path, root), 1, "top-level JSON value is not an object")
        return None
    return loaded


def nested(data: dict[str, Any], *names: str) -> dict[str, Any]:
    current: Any = data
    for name in names:
        if not isinstance(current, dict):
            return {}
        current = get_value(current, name, name[:1].upper() + name[1:], default={})
    return current if isinstance(current, dict) else {}


def import_environment_files(connection: sqlite3.Connection, import_run_id: int, root: Path) -> tuple[int, int]:
    imported = 0
    errors = 0
    session_paths = sorted((root / "environment").glob("*.json"))
    paths = list(iter_json_files(session_paths if session_paths else [root / "environment.json"]))
    for path in paths:
        payload = read_json_file(connection, import_run_id, root, path)
        if payload is None:
            errors += 1
            continue

        session_id = get_value(payload, "sessionId", "SessionId")
        if not session_id:
            record_parse_error(connection, import_run_id, relative_to(path, root), 1, "environment snapshot is missing sessionId")
            errors += 1
            continue

        game = nested(payload, "game")
        bepinex = nested(payload, "bepInEx")
        log_header = nested(bepinex, "logHeader")
        dotnet = nested(payload, "dotNetRuntime")
        process = nested(payload, "process")
        payload_json = json_text(payload)

        cursor = connection.execute(
            """
            INSERT OR REPLACE INTO runtime_environments(
                session_id, source_file, generated_at_utc, plugin_name, plugin_version,
                game_version, unity_version, platform, bepinex_core_assembly_version,
                bepinex_core_informational_version, bepinex_version, bepinex_build_commit,
                bepinex_system_platform, bepinex_process_bitness, bepinex_runtime_version,
                bepinex_runtime_information, dotnet_framework_description, dotnet_runtime_identifier,
                os_description, os_architecture, process_architecture, process_name,
                is_64_bit_process, is_64_bit_operating_system, output_root, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                session_id,
                relative_to(path, root),
                get_value(payload, "generatedAtUtc", "GeneratedAtUtc"),
                get_value(payload, "pluginName", "PluginName"),
                get_value(payload, "pluginVersion", "PluginVersion"),
                get_value(game, "version", "Version"),
                get_value(game, "unityVersion", "UnityVersion"),
                get_value(game, "platform", "Platform"),
                get_value(bepinex, "coreAssemblyVersion", "CoreAssemblyVersion"),
                get_value(bepinex, "coreAssemblyInformationalVersion", "CoreAssemblyInformationalVersion"),
                get_value(log_header, "bepInExVersion", "BepInExVersion"),
                get_value(log_header, "buildCommit", "BuildCommit"),
                get_value(log_header, "systemPlatform", "SystemPlatform"),
                get_value(log_header, "processBitness", "ProcessBitness"),
                get_value(log_header, "runtimeVersion", "RuntimeVersion"),
                get_value(log_header, "runtimeInformation", "RuntimeInformation"),
                get_value(dotnet, "frameworkDescription", "FrameworkDescription"),
                get_value(dotnet, "runtimeIdentifier", "RuntimeIdentifier"),
                get_value(dotnet, "osDescription", "OSDescription"),
                get_value(dotnet, "osArchitecture", "OSArchitecture"),
                get_value(dotnet, "processArchitecture", "ProcessArchitecture"),
                get_value(process, "processName", "ProcessName"),
                as_bool(get_value(process, "is64BitProcess", "Is64BitProcess")),
                as_bool(get_value(process, "is64BitOperatingSystem", "Is64BitOperatingSystem")),
                get_value(payload, "outputRoot", "OutputRoot"),
                payload_json,
            ),
        )
        if cursor.rowcount > 0:
            imported += 1

        import_environment_children(connection, str(session_id), payload)

    return imported, errors


def import_environment_children(connection: sqlite3.Connection, session_id: str, payload: dict[str, Any]) -> None:
    bepinex = nested(payload, "bepInEx")
    for plugin in get_value(bepinex, "loadedPlugins", "LoadedPlugins", default=[]):
        if not isinstance(plugin, dict):
            continue
        connection.execute(
            """
            INSERT OR REPLACE INTO environment_plugins(session_id, guid, name, version, location, type_name, payload_json)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                session_id,
                get_value(plugin, "guid", "Guid", "GUID"),
                get_value(plugin, "name", "Name"),
                get_value(plugin, "version", "Version"),
                get_value(plugin, "location", "Location"),
                get_value(plugin, "typeName", "TypeName"),
                json_text(plugin),
            ),
        )

    for entry in get_value(payload, "effectiveConfig", "EffectiveConfig", default=[]):
        if not isinstance(entry, dict):
            continue
        section = get_value(entry, "section", "Section")
        key = get_value(entry, "key", "Key")
        if not section or not key:
            continue
        connection.execute(
            """
            INSERT OR REPLACE INTO environment_config(
                session_id, section, key, value, default_value, description, acceptable_values, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                session_id,
                section,
                key,
                get_value(entry, "value", "Value"),
                get_value(entry, "defaultValue", "DefaultValue"),
                get_value(entry, "description", "Description"),
                get_value(entry, "acceptableValues", "AcceptableValues"),
                json_text(entry),
            ),
        )

    for assembly in get_value(payload, "interopAssemblies", "InteropAssemblies", default=[]):
        if not isinstance(assembly, dict):
            continue
        path = get_value(assembly, "path", "Path")
        if not path:
            continue
        connection.execute(
            """
            INSERT OR REPLACE INTO environment_interop_assemblies(
                session_id, file_name, path, directory, size_bytes, last_write_utc, assembly_name, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                session_id,
                get_value(assembly, "fileName", "FileName") or Path(str(path)).name,
                path,
                get_value(assembly, "directory", "Directory"),
                as_int(get_value(assembly, "sizeBytes", "SizeBytes")),
                get_value(assembly, "lastWriteUtc", "LastWriteUtc"),
                get_value(assembly, "assemblyName", "AssemblyName"),
                json_text(assembly),
            ),
        )

    for assembly in get_value(bepinex, "assemblies", "Assemblies", default=[]):
        if not isinstance(assembly, dict):
            continue
        path = get_value(assembly, "path", "Path")
        if not path:
            continue
        connection.execute(
            """
            INSERT OR REPLACE INTO environment_bepinex_assemblies(
                session_id, file_name, path, size_bytes, last_write_utc, assembly_name,
                file_version, product_version, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                session_id,
                get_value(assembly, "fileName", "FileName") or Path(str(path)).name,
                path,
                as_int(get_value(assembly, "sizeBytes", "SizeBytes")),
                get_value(assembly, "lastWriteUtc", "LastWriteUtc"),
                get_value(assembly, "assemblyName", "AssemblyName"),
                get_value(assembly, "fileVersion", "FileVersion"),
                get_value(assembly, "productVersion", "ProductVersion"),
                json_text(assembly),
            ),
        )


def import_manifest_files(connection: sqlite3.Connection, import_run_id: int, root: Path) -> tuple[int, int]:
    imported = 0
    errors = 0
    session_paths = sorted((root / "manifests").glob("*.json"))
    paths = list(iter_json_files(session_paths if session_paths else [root / "manifest.json"]))
    for path in paths:
        payload = read_json_file(connection, import_run_id, root, path)
        if payload is None:
            errors += 1
            continue

        session_id = get_value(payload, "sessionId", "SessionId")
        if not session_id:
            record_parse_error(connection, import_run_id, relative_to(path, root), 1, "manifest is missing sessionId")
            errors += 1
            continue

        cursor = connection.execute(
            """
            INSERT OR REPLACE INTO manifests(
                session_id, source_file, generated_at_utc, plugin_name, plugin_version,
                started_at_utc, ended_at_utc, end_reason, output_root, raw_session_jsonl,
                summary_report, runtime_environment, payload_json
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                session_id,
                relative_to(path, root),
                get_value(payload, "generatedAtUtc", "GeneratedAtUtc"),
                get_value(payload, "pluginName", "PluginName"),
                get_value(payload, "pluginVersion", "PluginVersion"),
                get_value(payload, "startedAtUtc", "StartedAtUtc"),
                get_value(payload, "endedAtUtc", "EndedAtUtc"),
                get_value(payload, "endReason", "EndReason"),
                get_value(payload, "outputRoot", "OutputRoot"),
                get_value(payload, "rawSessionJsonl", "RawSessionJsonl"),
                get_value(payload, "summaryReport", "SummaryReport"),
                get_value(payload, "runtimeEnvironment", "RuntimeEnvironment"),
                json_text(payload),
            ),
        )
        if cursor.rowcount > 0:
            imported += 1

    return imported, errors


def rebuild_codex_search(connection: sqlite3.Connection, enable_fts: bool) -> int:
    connection.execute("DELETE FROM codex_search")
    connection.execute("DROP TABLE IF EXISTS codex_search_fts")
    inserts = 0

    object_rows = connection.execute(
        """
        SELECT key, scene_name, transform_path, object_name, tag, layer, child_count
        FROM objects
        """
    )
    for row in object_rows:
        key, scene_name, transform_path, object_name, tag, layer, child_count = row
        search_text = " ".join(
            str(part)
            for part in (key, scene_name, transform_path, object_name, tag, layer, child_count)
            if part is not None
        )
        insert_search_row(
            connection,
            "object",
            key,
            object_name,
            None,
            scene_name,
            transform_path,
            None,
            None,
            search_text,
        )
        inserts += 1

    component_rows = connection.execute(
        """
        SELECT oc.key, oc.type_full_name, oc.assembly_name, o.scene_name, oc.transform_path,
               o.object_name, oc.component_index, oc.enabled_state
        FROM resolved_object_components oc
        LEFT JOIN objects o ON o.semantic_key = oc.object_semantic_key
        """
    )
    for row in component_rows:
        key, type_full_name, assembly_name, scene_name, transform_path, object_name, component_index, enabled_state = row
        search_text = " ".join(
            str(part)
            for part in (
                key,
                type_full_name,
                assembly_name,
                scene_name,
                transform_path,
                object_name,
                component_index,
                enabled_state,
            )
            if part is not None
        )
        insert_search_row(
            connection,
            "component",
            key,
            object_name,
            type_full_name,
            scene_name,
            transform_path,
            None,
            None,
            search_text,
        )
        inserts += 1

    type_rows = connection.execute(
        """
        SELECT key, type_full_name, assembly_name, namespace, base_type_full_name
        FROM types
        """
    )
    for row in type_rows:
        key, type_full_name, assembly_name, namespace, base_type_full_name = row
        search_text = " ".join(
            str(part)
            for part in (key, type_full_name, assembly_name, namespace, base_type_full_name)
            if part is not None
        )
        insert_search_row(
            connection,
            "type",
            key,
            type_full_name,
            type_full_name,
            None,
            None,
            None,
            None,
            search_text,
        )
        inserts += 1

    member_shape_rows = connection.execute(
        """
        SELECT key, observed_type_full_name, declaring_type_full_name, member_name, member_kind,
               value_type_full_name, accessibility, parameter_types
        FROM member_shapes
        """
    )
    for row in member_shape_rows:
        key, observed_type, declaring_type, member_name, member_kind, value_type, accessibility, parameter_types = row
        search_text = " ".join(
            str(part)
            for part in (
                key,
                observed_type,
                declaring_type,
                member_name,
                member_kind,
                value_type,
                accessibility,
                parameter_types,
            )
            if part is not None
        )
        insert_search_row(
            connection,
            "member_shape",
            key,
            member_name,
            observed_type,
            None,
            None,
            member_name,
            None,
            search_text,
        )
        inserts += 1

    member_value_rows = connection.execute(
        """
        SELECT key, component_type_full_name, declaring_type_full_name, member_name, member_kind,
               value_type_full_name, value_kind, serialized_value, referenced_object_name,
               referenced_object_type_full_name, transform_path
        FROM member_values
        """
    )
    for row in member_value_rows:
        (
            key,
            component_type,
            declaring_type,
            member_name,
            member_kind,
            value_type,
            value_kind,
            serialized_value,
            referenced_object_name,
            referenced_object_type,
            transform_path,
        ) = row
        search_text = " ".join(
            str(part)
            for part in (
                key,
                component_type,
                declaring_type,
                member_name,
                member_kind,
                value_type,
                value_kind,
                serialized_value,
                referenced_object_name,
                referenced_object_type,
                transform_path,
            )
            if part is not None
        )
        insert_search_row(
            connection,
            "member_value",
            key,
            member_name,
            component_type,
            None,
            transform_path,
            member_name,
            serialized_value,
            search_text,
        )
        inserts += 1

    environment_rows = connection.execute(
        """
        SELECT session_id, source_file, plugin_version, game_version, unity_version, platform,
               bepinex_version, bepinex_build_commit, bepinex_runtime_version,
               bepinex_runtime_information, dotnet_framework_description, process_name
        FROM runtime_environments
        """
    )
    for row in environment_rows:
        (
            session_id,
            source_file,
            plugin_version,
            game_version,
            unity_version,
            platform,
            bepinex_version,
            build_commit,
            runtime_version,
            runtime_information,
            framework_description,
            process_name,
        ) = row
        search_text = " ".join(
            str(part)
            for part in (
                session_id,
                source_file,
                plugin_version,
                game_version,
                unity_version,
                platform,
                bepinex_version,
                build_commit,
                runtime_version,
                runtime_information,
                framework_description,
                process_name,
            )
            if part is not None
        )
        insert_search_row(
            connection,
            "environment",
            session_id,
            session_id,
            None,
            None,
            None,
            None,
            None,
            search_text,
        )
        inserts += 1

    if enable_fts:
        rebuild_fts(connection)
    return inserts


def insert_search_row(
    connection: sqlite3.Connection,
    entity_kind: str,
    entity_key: str,
    display_name: str | None,
    type_name: str | None,
    scene_name: str | None,
    transform_path: str | None,
    member_name: str | None,
    value_text: str | None,
    search_text: str,
) -> None:
    connection.execute(
        """
        INSERT INTO codex_search(
            entity_kind, entity_key, display_name, type_name, scene_name, transform_path,
            member_name, value_text, search_text
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
        (
            entity_kind,
            entity_key,
            display_name,
            type_name,
            scene_name,
            transform_path,
            member_name,
            value_text,
            search_text,
        ),
    )


def rebuild_fts(connection: sqlite3.Connection) -> None:
    try:
        connection.execute(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS codex_search_fts USING fts5(
                entity_kind,
                display_name,
                type_name,
                scene_name,
                transform_path,
                member_name,
                value_text,
                search_text,
                content='codex_search',
                content_rowid='id'
            )
            """
        )
        connection.execute("INSERT INTO codex_search_fts(codex_search_fts) VALUES ('rebuild')")
    except sqlite3.DatabaseError:
        # Some Python/SQLite builds omit FTS5. The regular codex_search table remains usable with LIKE queries.
        connection.execute("DROP TABLE IF EXISTS codex_search_fts")


def import_output(
    root: Path,
    database_path: Path,
    rebuild: bool,
    include_sessions: bool,
    keep_known_payloads: bool,
    build_search: bool,
    enable_fts: bool,
    vacuum: bool,
) -> int:
    root = root.resolve()
    database_path = database_path.resolve()
    if not root.exists():
        print(f"Output root does not exist: {root}", file=sys.stderr)
        return 2

    connection = connect_database(database_path, rebuild)
    with connection:
        drop_performance_indexes(connection)
        cursor = connection.execute(
            """
            INSERT INTO import_runs(imported_at_utc, output_root, include_sessions)
            VALUES (?, ?, ?)
            """,
            (utc_now(), str(root), 1 if include_sessions else 0),
        )
        import_run_id = int(cursor.lastrowid)

        known_total = 0
        environment_total = 0
        manifest_total = 0
        raw_total = 0
        parse_errors = 0

        for category, filename in KNOWN_FILES.items():
            imported, errors = import_known_file(
                connection,
                import_run_id,
                root,
                root / "database" / filename,
                category,
                keep_known_payloads,
            )
            known_total += imported
            parse_errors += errors
            print(f"Imported {category}: {imported} records, {errors} parse errors")

        environment_total, errors = import_environment_files(connection, import_run_id, root)
        parse_errors += errors
        print(f"Imported runtime environments: {environment_total} records, {errors} parse errors")

        manifest_total, errors = import_manifest_files(connection, import_run_id, root)
        parse_errors += errors
        print(f"Imported manifests: {manifest_total} records, {errors} parse errors")

        if include_sessions:
            raw_total, errors = import_raw_sessions(connection, import_run_id, root)
            parse_errors += errors
            print(f"Imported raw session records: {raw_total} records, {errors} parse errors")

        search_total = rebuild_codex_search(connection, enable_fts) if build_search else 0
        print(f"Built Codex search rows: {search_total}")
        print("Creating SQLite indexes...")
        create_performance_indexes(connection)
        connection.execute("ANALYZE")
        connection.execute("PRAGMA optimize")

        connection.execute(
            """
            UPDATE import_runs
            SET known_records = ?, environment_records = ?, manifest_records = ?, raw_records = ?, parse_errors = ?
            WHERE id = ?
            """,
            (known_total, environment_total, manifest_total, raw_total, parse_errors, import_run_id),
        )

    if vacuum:
        connection.execute("VACUUM")

    print(f"BepInExposition SQLite index: {database_path}")
    print(f"Known records imported: {known_total}")
    print(f"Environment snapshots imported: {environment_total}")
    print(f"Manifests imported: {manifest_total}")
    print(f"Codex search rows indexed: {search_total}")
    print(f"Codex FTS enabled: {enable_fts and build_search}")
    print(f"Raw session records imported: {raw_total}")
    print(f"Parse errors: {parse_errors}")
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build a SQLite index from BepInExposition JSONL output.")
    parser.add_argument("output_root", type=Path, help="Path to BepInEx/BepInExposition output directory.")
    parser.add_argument(
        "-o",
        "--database",
        type=Path,
        help="SQLite database path. Defaults to <output_root>/bepinexposition.sqlite.",
    )
    parser.add_argument("--rebuild", action="store_true", help="Delete and recreate the SQLite database before import.")
    parser.add_argument(
        "--include-sessions",
        action="store_true",
        help="Also import raw sessions/*.jsonl into raw_records. This can make the SQLite file very large.",
    )
    parser.add_argument(
        "--keep-known-payloads",
        action="store_true",
        help="Keep full payload_json copies in known_records. By default, typed tables keep payloads and known_records stays compact.",
    )
    parser.add_argument(
        "--skip-sessions",
        action="store_true",
        help="Deprecated no-op kept for older command lines. Raw sessions are skipped unless --include-sessions is set.",
    )
    parser.add_argument(
        "--no-search",
        action="store_true",
        help="Do not build the Codex search table. Use only when minimizing database size matters more than search convenience.",
    )
    parser.add_argument(
        "--fts",
        action="store_true",
        help="Build the optional FTS5 full-text index. This improves broad text search but increases import time and database size.",
    )
    parser.add_argument(
        "--no-fts",
        action="store_true",
        help="Deprecated no-op kept for older command lines. FTS is skipped unless --fts is set.",
    )
    parser.add_argument("--vacuum", action="store_true", help="Run VACUUM after import.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    output_root = args.output_root
    database_path = args.database or output_root / "bepinexposition.sqlite"
    return import_output(
        output_root,
        database_path,
        rebuild=args.rebuild,
        include_sessions=args.include_sessions and not args.skip_sessions,
        keep_known_payloads=args.keep_known_payloads,
        build_search=not args.no_search,
        enable_fts=args.fts and not args.no_fts,
        vacuum=args.vacuum,
    )


if __name__ == "__main__":
    raise SystemExit(main())
