# AGENTS.md

## Purpose

This workspace contains runtime capture data produced by BepInExposition. Use it as runtime evidence when planning or editing BepInEx 6 IL2CPP plugins.

The data answers questions such as:

- which Unity scenes were observed
- which GameObjects and transform paths existed at runtime
- which components appeared on those objects
- which IL2CPP wrapper types and assemblies were observed
- which fields, properties, and methods those wrappers exposed through reflection
- which observations were newly discovered in each capture session

Treat this data as a complement to static analysis, interop assemblies, decompilation, pseudocode, and manual notes. It is not a replacement for source review or in-game testing.

## Path Placeholders

Replace these placeholders before handing the workspace to an agent:

- `<BEPINEXPOSITION_ROOT>`: path to the BepInExposition output folder, usually `BepInEx/BepInExposition`
- `<SQLITE_DB>`: optional SQLite database built from the JSONL files, usually `<BEPINEXPOSITION_ROOT>/bepinexposition.sqlite`
- `<GAME_INTEROP_ASSEMBLIES>`: path to the target game's BepInEx interop/reference assemblies
- `<STATIC_ANALYSIS_ROOT>`: optional path to decompilation, pseudocode, dumps, or handwritten notes

## Core Files

Start here:

- `<BEPINEXPOSITION_ROOT>/manifest.json`
  - Latest machine-readable handoff manifest.
  - Lists the latest report, runtime environment file, raw session file, database files, schema version, plugin version, and recommended Codex inputs.

- `<BEPINEXPOSITION_ROOT>/reports/session-<id>-summary.md`
  - Human-readable summary for one capture session.
  - Shows what was observed, what was newly added to the cumulative database, and where the relevant files live.

- `<BEPINEXPOSITION_ROOT>/environment/session-<id>-environment.json`
  - Per-session runtime environment snapshot.
  - Useful fields: game version, Unity version, BepInEx log-header version/build details, .NET runtime information, process bitness/architecture, BepInEx path values, loaded plugin metadata, interop assembly inventory, and effective BepInExposition config.
  - Use to confirm which BepInEx/plugins/config produced the capture before making compatibility assumptions.

- `<BEPINEXPOSITION_ROOT>/sessions/session-<id>.jsonl`
  - Raw append-only event log for one game run.
  - Use when exact timing, event order, marker context, or per-snapshot details matter.
  - Detail depends on the capture config's `RawSessionDetailMode`; by default, repeated object/component/type/member records are omitted once they are already known.
  - May be omitted from a handoff when very large. The cumulative `database/*.jsonl` files remain the primary stable fact set.

## Cumulative JSONL Database

The `database/` folder contains deduplicated facts accumulated across sessions. These files are the best default input when asking an agent to reason about a game system.

Each line is one JSON object with this shape:

```json
{
  "key": "stable-deduplication-key",
  "firstSeenSessionId": "session-id",
  "firstSeenAtUtc": "timestamp",
  "payload": {}
}
```

Files:

- `<BEPINEXPOSITION_ROOT>/database/scenes.jsonl`
  - One record per known scene identity.
  - Useful fields: `sceneName`, `scenePath`, `buildIndex`.
  - Use to identify observed scenes and correlate scene names from static analysis or logs.

- `<BEPINEXPOSITION_ROOT>/database/objects.jsonl`
  - One record per known semantic GameObject path.
  - Useful fields: `semanticKey`, `sceneName`, `transformPath`, `objectName`, `tag`, `layer`, `childCount`.
  - Use to find runtime hierarchy locations for UI, managers, gameplay objects, and prefab instances.

- `<BEPINEXPOSITION_ROOT>/database/component_types.jsonl`
  - One record per observed component type.
  - Useful fields: `typeFullName`, `assemblyName`.
  - Use to discover which game or Unity component types appeared at runtime. `typeFullName` may come from the native IL2CPP class when a managed wrapper collapses to `UnityEngine.Component`.

- `<BEPINEXPOSITION_ROOT>/database/object_components.jsonl`
  - One record per known object/component relationship.
  - Useful fields: `objectSemanticKey`, `transformPath`, `componentIndex`, `typeFullName`, `assemblyName`, `enabledState`.
  - Use to answer "which component is on this object?" or "where does this component type appear?"

- `<BEPINEXPOSITION_ROOT>/database/types.jsonl`
  - One record per observed reflected type.
  - Useful fields: `snapshotId`, `typeFullName`, `assemblyName`, `namespace`, `baseTypeFullName`, `isPublic`, `isSealed`, `isAbstract`, `isInterface`, `isEnum`, `inheritanceChain`, `enumUnderlyingType`, `enumValues`.
  - Use to understand wrapper inheritance and assembly ownership.

- `<BEPINEXPOSITION_ROOT>/database/member_shapes.jsonl`
  - One record per observed reflected field, property, or method shape.
  - Useful fields: `observedTypeFullName`, `declaringTypeFullName`, `memberName`, `memberKind`, `valueTypeFullName`, `isStatic`, `isPublic`, `canRead`, `canWrite`, `parameterCount`, `parameterTypes`, `accessibility`, `getterAccessibility`, `setterAccessibility`, `isInitOnly`, `isBackingField`, `isVirtual`, `isAbstract`, `parameters`.
  - Use to plan reflection access, identify backing fields/properties, and avoid incorrect assumptions about IL2CPP wrapper surfaces.

- `<BEPINEXPOSITION_ROOT>/database/member_values.jsonl`
  - One record per deduplicated safe field/property value or Unity object reference observed on a component instance.
  - Useful fields: `objectSemanticKey`, `componentIndex`, `componentTypeFullName`, `declaringTypeFullName`, `memberName`, `valueTypeFullName`, `valueKind`, `serializedValue`, `referencedObjectName`, `referencedObjectTypeFullName`.
  - Use to understand live component configuration without recursively serializing arbitrary object graphs.

## Optional SQLite Index

If `<SQLITE_DB>` exists, it was generated from the JSONL output with `tools/jsonl_to_sqlite.py`.

Use SQLite as the primary runtime data source for broad searches, joins, and Codex-oriented summaries. Prefer JSONL only for small handoffs, backup verification, or when the user asks for raw files.

By default, the SQLite index contains only cumulative `database/*.jsonl` facts. The `raw_records` table is populated only when the indexer was run with `--include-sessions`.

Main tables:

- `codex_catalog`
  - Start here inside SQLite.
  - Describes the important tables/views and includes example queries.

- `database_summary`
  - Fast row counts by category.

- `codex_search`
  - Unified search rows over objects, components, types, member shapes, member values, and runtime environments.
  - Use `LIKE` queries here when unsure which table contains a term.

- `codex_search_ranked`
  - Priority-ordered search view over `codex_search`.
  - Prefer this for broad first-pass searches because types and member evidence appear before generic object path matches.

- `codex_search_fts`
  - Optional FTS5 search table when the local SQLite build supports FTS5 and the indexer was run with `--fts`.
  - Use `MATCH` queries for faster broad text search.

- `scenes`
  - Typed index of `database/scenes.jsonl`.

- `objects`
  - Typed index of `database/objects.jsonl`.
  - Common queries: search by `scene_name`, `transform_path`, `object_name`.

- `component_types`
  - Typed index of observed component type names.

- `object_components`
  - Typed index of object/component relationships.
  - Common joins: `objects.semantic_key = object_components.object_semantic_key`.

- `resolved_object_components`
  - SQLite view over `object_components`.
  - Prefer this view when older generic `UnityEngine.Component` rows may coexist with newer concrete component rows for the same object/component slot.

- `object_component_context`
  - Joined object plus component placement context.
  - Prefer this when answering where a component appears in the hierarchy.

- `component_type_locations`
  - Compact per-component placement view with scene and transform path.

- `scene_summary`
  - One row per scene with object/component counts.

- `component_type_summary`
  - One row per component type with object count, component count, scene count, reflected member-shape count, and observed member-value count.

- `types`
  - Typed index of reflected wrapper types, including flags, inheritance chain JSON, and enum metadata.

- `member_shapes`
  - Typed index of reflected fields, properties, and methods.
  - Common filters: `observed_type_full_name`, `member_name`, `member_kind`, `value_type_full_name`, `accessibility`, `is_backing_field`.

- `member_values`
  - Typed index of safe field/property values and Unity object references.
  - Common filters: `component_type_full_name`, `member_name`, `value_kind`, `object_semantic_key`.

- `type_member_shapes`
  - Reflected member shapes joined to observed type metadata.

- `member_value_context`
  - Observed member values with object/scene context where available.

- `runtime_environments`
  - Per-session game, Unity, BepInEx, .NET, platform, and process context.

- `environment_config`
  - Effective BepInExposition config values captured per session.

- `environment_interop_assemblies`
  - Interop assembly file inventory captured per session.

- `environment_bepinex_assemblies`
  - BepInEx assembly/file version inventory captured per session when available.

- `manifests`
  - Imported per-session manifest metadata.

- `known_records`
  - Generic copy of every cumulative known-data line.
  - Contains `category`, `key`, first-seen metadata, source file, line number, and raw payload JSON.
  - In compact databases, `payload_json` may be `{}` because the typed tables retain payloads and the JSONL files remain the raw backup.

- `raw_records`
  - Optional generic copy of raw session events from `sessions/*.jsonl`.
  - Contains `record_type`, `session_id`, `captured_at_utc`, source file, line number, and raw payload JSON.
  - Use only when timing, marker context, event ordering, or raw snapshot detail matters.

- `parse_errors`
  - JSONL parse/import errors encountered during SQLite indexing.

- `import_runs`
  - History of SQLite import runs and counts.

Example SQLite queries:

```sql
-- Find observed objects whose path mentions "Canvas".
SELECT scene_name, transform_path, object_name
FROM objects
WHERE transform_path LIKE '%Canvas%'
ORDER BY scene_name, transform_path;

-- Find where a component type appeared.
SELECT object_semantic_key, transform_path, enabled_state
FROM object_components
WHERE type_full_name LIKE '%SomeComponentName%'
ORDER BY transform_path;

-- Inspect reflected members for a wrapper type.
SELECT member_kind, member_name, value_type_full_name, accessibility, can_read, can_write, parameter_count
FROM member_shapes
WHERE observed_type_full_name LIKE '%SomeWrapperType%'
ORDER BY declaring_type_full_name, member_kind, member_name;

-- Inspect observed field/property values for components under a path.
SELECT component_type_full_name, member_name, value_kind, serialized_value
FROM member_values
WHERE object_semantic_key LIKE '%SomeObjectPath%'
ORDER BY component_index, member_name;
```

## Blind Query Workflow

When starting from only the SQLite database:

1. Inspect `codex_catalog` and `database_summary`.
2. Use `scene_summary` to confirm scene coverage.
3. Use `codex_search_ranked` for broad terms, ordered by `entity_priority`.
4. Move from search results into `component_type_locations`, `type_member_shapes`, and `member_value_context`.
5. Check `runtime_environments` before making BepInEx, Unity, or runtime compatibility assumptions.

Broad `LIKE` searches can produce substring false positives. For example, `Mission` may also match art names containing `Emission`. Prefer `codex_search_ranked`, then narrow by `entity_kind`, `type_name`, `scene_name`, or exact component/member names.

## Reasoning Rules

- Prefer cumulative `database/*.jsonl` or typed SQLite tables for stable facts.
- Use raw `sessions/*.jsonl` when timing, marker labels, snapshot IDs, or exact event order matters. Check the session summary's raw detail mode before assuming every repeated object/component record was written.
- Treat native pointers and Unity instance IDs as session-local identities, not cross-session stable IDs.
- Treat `semanticKey`, `sceneName`, and `transformPath` as best-effort cross-session identities.
- Treat component native type names as placement evidence. Member-shape records describe reflected managed wrappers only, so native-only component names may still require static analysis or interop lookup before direct member access.
- Do not assume a member shape means a getter is safe to invoke. BepInExposition records reflected shapes without reading arbitrary member values.
- Treat `member_values` as observed snapshots, not stable configuration truth. Values may vary by scene, difficulty, player state, platform, or timing.
- IL2CPP wrappers can expose fields/properties/methods inconsistently across game versions. Cross-check with interop assemblies and static analysis before writing production patches.
- If the game was force-closed, reports/manifests may be stale or missing. Raw JSONL and cumulative database files may still contain useful data, but final session summaries may be incomplete.

## Recommended Agent Workflow

1. Read `<BEPINEXPOSITION_ROOT>/manifest.json`.
2. Read the latest session summary listed by the manifest.
3. Use `database/scenes.jsonl`, `database/objects.jsonl`, and `database/object_components.jsonl` to understand scene hierarchy and component placement.
4. Use `database/types.jsonl` and `database/member_shapes.jsonl` to understand IL2CPP wrapper access options.
5. Use `<SQLITE_DB>` for broad searches or joins when available.
6. Cross-check runtime observations against `<GAME_INTEROP_ASSEMBLIES>` and `<STATIC_ANALYSIS_ROOT>` before implementing patches.
7. When citing conclusions, distinguish between directly observed runtime facts and inferences from naming or static analysis.
