# BepInExposition

![Latest release](https://img.shields.io/github/v/release/Dteyn/BepInExposition?include_prereleases)
![Downloads](https://img.shields.io/github/downloads/Dteyn/BepInExposition/total)

BepInExposition is a quiet runtime data capture plugin for BepInEx 6 IL2CPP games. It records scene, hierarchy, component, wrapper type, and reflected member-shape information while you play, then turns that into Codex-friendly JSONL files that can be reused when planning future plugins.

The goal is practical runtime truth: what scenes loaded, which objects existed, where UI and gameplay components lived, which IL2CPP wrapper types appeared, and what fields/properties/methods those wrappers exposed.

## Status

Early development. The current implementation focuses on safe, universal capture using only BepInEx, Il2CppInterop, and UnityEngine Core references. It does not depend on game-specific assemblies.

BepInExposition requires BepInEx [6.0.0-pre.2](https://github.com/BepInEx/BepInEx/releases/tag/v6.0.0-pre.2) or newer for IL2CPP games. It has been built and tested against BepInEx 6 bleeding-edge version [`6.0.0-be.755+3fab71a`](https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip).


## Strengths and Weaknesses

BepInExposition is meant to be another tool in the toolbox of a plugin developer, to aid with plugin development for IL2CPP games.

Currently, it's geared mostly for Codex and agentic work, although I plan to add more tools in the future for manual inspection and viewing.

For the time being, the tool is useful for work with Codex and other agents, to provide additional context during plugin development.

### Pros

The biggest pro BepInExposition offers is runtime truth. Decompiled code can be incomplete, pseudocode can lie, and generated wrappers can differ from expectation. BepInExposition tells you what the running game actually exposed.

It is game-agnostic. The plugin only depends on BepInEx, Il2CppInterop, and Unity core references.

As you play the game, BepInExposition builds a cumulative memory. Deduped database/*.jsonl means every game session continually improves captured reference data.

It is agent-friendly. The manifest, summary reports, SQLite catalog, and search views are structured in a way Codex can use efficiently.

### Cons

BepInExposition is observational, not causal. It can say "this method exists" or "this component appeared here," but it cannot by itself say "this callback
fired before that one" unless the raw session has enough markers/events.

It only captures what you visit or do during gameplay. Any actions you do not perform won't be captured, by the nature of how the plugin captures 'as-you-go'.

Member values are intentionally shallow. This is good for safety, but it means some useful state remains hidden, especially lists, dictionaries, delegates, event subscribers, and nested model objects.

Deduplication can hide temporal changes. The cumulative database is excellent for stable facts, but if a component’s value changes during a UI interaction, the known-record model may not preserve enough "before/after unless raw session mode and markers are used deliberately.

BepInExposition also does not currently connect runtime evidence back to static call flow. For example, it may show that a property exists, but not whether patching that property would be safe.

### What It's Useful For

BepInExposition is currently best at building a runtime inventory for IL2CPP plugin development. It is useful for:

- Showing which scenes were observed during actual gameplay.
- Mapping GameObject hierarchy paths and component placement in those scenes.
- Recording runtime component type names, assemblies, and native IL2CPP type hints where available.
- Capturing reflected wrapper type/member surfaces without broadly invoking risky getters.
- Sampling simple member values and Unity object references within conservative safety limits.

### What It's NOT Useful For

BepInExposition is not a replacement for decompilation, manual interop assembly review, or in-game testing.

The database is simply observational: it can prove that a component, object path, method name, or sampled value was seen.

It usually cannot prove why it changed, which callback fired first, or whether invoking a reflected getter/method is safe. It also only knows about flows the player actually visited during capture.

### What needs improvement:

The main areas that still need improvement are focused capture and behavior evidence. Useful future work includes:

- Path/type-filtered snapshots so focused captures can go deeper without scanning an entire scene.
- Opt-in timeline or method tracing for selected callbacks.
- Per-snapshot value history for selected members when state transitions matter.
- Better diagnostics for members skipped by safety rules or capture caps.
- Game-specific capture profiles that temporarily raise detail around one UI screen, manager object, or gameplay system without making every scene scan heavier.

## Main Features

BepInExposition writes a compact cumulative knowledge base while keeping raw per-session detail available when timing or marker context matters. The main capture path is conservative by default, with bounded scene snapshots, deduplicated known facts, and offline SQLite indexing for fast Codex-friendly queries after collection.

- One raw append-only JSONL file per game session, with configurable detail to avoid repeating already-known snapshot facts.
- Cumulative deduplicated JSONL database that grows across sessions.
- Session start/end metadata with game version, Unity version, platform, and plugin version.
- Scene load, scene unload when supported by the target Unity build, and active-scene change observations.
- Bounded hierarchy snapshots after scene-settle events and at a low periodic interval.
- GameObject names, transform paths, active state, tag, layer, child count, Unity instance IDs, and native IL2CPP pointers.
- Component type, native IL2CPP type when available, assembly/image name, order, enabled state, component instance ID, and native pointer capture.
- Reflected type catalog and member-shape capture without invoking risky member getters.
- Reflection type/member metadata for observed runtime types, including inheritance, enum values, accessibility, and parameter shape.
- Supports command-file triggers for input of commands including manual snapshots, markers, and flushes.
- Per-session runtime environment snapshots with BepInEx versions, log-header build details, process/runtime information, paths, loaded plugin inventory, interop assembly inventory, and effective BepInExposition config.
- Human-readable session summary reports.
- Latest and per-session manifests for Codex handoff.
- Compact BepInEx summary boxes in `DEBUG` builds on scene changes and important snapshots.

## Output Layout

Default output root:

```text
BepInEx/BepInExposition/
```

Generated files:

```text
BepInEx/BepInExposition/
  capture_commands.txt
  environment.json
  manifest.json
  database/
    scenes.jsonl
    objects.jsonl
    component_types.jsonl
    object_components.jsonl
    types.jsonl
    member_shapes.jsonl
    member_values.jsonl
  environment/
    session-<id>-environment.json
  manifests/
    session-<id>-manifest.json
  reports/
    session-<id>-summary.md
  sessions/
    session-<id>.jsonl
```

Use `manifest.json` as the starting point when handing data to Codex. The `database/*.jsonl` files are deduplicated cumulative knowledge. The `sessions/*.jsonl` files preserve raw per-run event detail when exact ordering, marker context, or per-snapshot payloads matter.

## Human-Readable Session Summary

Each normal shutdown writes a Markdown summary report in `reports/session-<id>-summary.md` and a companion machine-readable manifest in `manifests/session-<id>-manifest.json`. The summary is meant for quick human review: it shows what the run observed, what was newly added to the cumulative database, sample discoveries, and the database files Codex should inspect next.

Generic summary excerpt:

```markdown
# BepInExposition Session Summary

- Session: `20260417-120000-example`
- End reason: `application_quit`
- Raw session JSONL: `sessions/session-20260417-120000-example.jsonl`
- Runtime environment: `environment/session-20260417-120000-example-environment.json`

## Observed This Run

- Scenes observed: 2; new known scenes: 1
- Snapshots written: 2
- GameObjects observed: 1240; new known objects: 310
- Components observed: 2485; new object/component links: 620; new component types: 8
- Member shapes observed: 420; new member shapes: 95
- Member values observed: 80; new member values: 22
- Errors captured: 0

## New Component Types

- `Example.Gameplay.ManagerBehaviour`
- `Example.UI.ScreenController`

## Known Database Files

- `database/objects.jsonl`: 12000 known records, 310 added this run
- `database/object_components.jsonl`: 24000 known records, 620 added this run
```

## Shutdown Matters

Exit the game normally when you want complete reports and manifests. Do not force-close the game by pressing the `X` on the BepInEx console window unless you are intentionally aborting the process.

BepInExposition writes raw JSONL data throughout the session, but the human-readable summary report and manifest are finalized during normal Unity shutdown. Force-closing the process can prevent those final files from being generated or updated.

## Manual Commands

BepInExposition polls a small command inbox file during the Unity update loop. By default the file is:

```text
BepInEx/BepInExposition/capture_commands.txt
```

If `Storage.OutputDirectory` is configured, place the command file in that output directory instead. If `Manual.CommandFileName` is changed, use that filename instead of `capture_commands.txt`.

The command file is one-shot: create or overwrite it, the plugin processes every supported non-empty line, then deletes the file. To run another command later, create the file again. This keeps command handling simple and avoids repeatedly executing the same command every frame.

Commands are case-insensitive. A command may be followed by a free-form label. Labels are optional, but they are useful because they are written into the raw session JSONL and help correlate a capture with what you were doing in-game.

| Command | Arguments | What it does | When to use |
| --- | --- | --- | --- |
| `snapshot` | Optional label | Takes a full active-scene hierarchy/component/type/member capture immediately. | Use for intentional collection points, especially for known scenes that are not auto-rescanned by default. |
| `marker` | Optional label | Writes a lightweight marker event to the raw session JSONL without walking the scene. | Use to mark context such as opening a menu, starting combat, picking up an item, or reaching a room. |
| `flush` | None | Flushes pending session and cumulative database writes to disk. | Use before copying files while the game is still running, or after a sequence of markers/snapshots. |
| `reload_config` | None | Reloads the BepInExposition config file from disk. Alias: `reloadconfig`. | Use after editing the `.cfg` while the game is running. |
| `save_config` | None | Saves the current effective BepInExposition config to disk. Alias: `saveconfig`. | Use after a reload or before copying config files for a reproducible capture package. |

Minimal example:

```text
snapshot optional_label
marker optional_label
flush
```

Capture a known scene after you have moved to a useful location:

```text
snapshot level_starting_area_after_door
```

Mark several gameplay moments without forcing a full scan:

```text
marker opened_game_menu
marker selected_desired_option
marker entered_second_area
flush
```

Reload config after changing caps or snapshot settings:

```text
reload_config
marker config_reloaded_for_manual_scan
snapshot after_config_reload
flush
```

Capture, mark context, and flush in one command file:

```text
marker before_interaction
snapshot item_visible
marker after_interaction
flush
```

For Windows PowerShell, this creates a one-line snapshot command in the default output root:

```powershell
Set-Content -Path "BepInEx\BepInExposition\capture_commands.txt" -Value "snapshot manual_scan"
```

For Command Prompt:

```bat
echo snapshot manual_scan>BepInEx\BepInExposition\capture_commands.txt
```

Manual snapshots can pause large IL2CPP games while the scene is walked. Trigger them when the game is stationary, in a menu, or otherwise safe to stall briefly. Use `marker` for high-frequency notes during live gameplay.

## SQLite Indexing

After you are finished collecting data, you can build an offline SQLite index from the JSONL output:

```text
python tools/jsonl_to_sqlite.py "C:\Path\To\BepInEx\BepInExposition" --rebuild
```

By default this creates:

```text
BepInEx/BepInExposition/bepinexposition.sqlite
```

The SQLite database contains typed tables for scenes, objects, component types, object/component links, reflected types, member shapes, member values, runtime environments, manifests, parse errors, and import history. It also builds Codex-oriented views and a unified `codex_search` table so the SQLite file can be used as the primary runtime data source.

Raw `sessions/*.jsonl` files are not imported by default because they can make the SQLite file much larger. Use `--include-sessions` only when exact event order, marker context, capture errors, or per-snapshot raw payloads matter. The generic `known_records` table is compact by default because typed tables retain payload JSON; use `--keep-known-payloads` only if you want full duplicated payloads there too. Use `--vacuum` after large rebuilds if you want SQLite to compact the file.

Useful entry points:

```sql
SELECT * FROM codex_catalog ORDER BY object_name;
SELECT * FROM database_summary ORDER BY category;
SELECT * FROM scene_summary ORDER BY object_count DESC;
SELECT * FROM component_type_summary ORDER BY object_count DESC LIMIT 25;
```

Search examples:

```sql
SELECT entity_kind, display_name, type_name, scene_name, transform_path
FROM codex_search_ranked
WHERE search_text LIKE '%LevelID%'
ORDER BY entity_priority, entity_kind, display_name
LIMIT 50;
```

If the local SQLite build supports FTS5, `--fts` also creates `codex_search_fts`:

```sql
SELECT entity_kind, display_name, type_name, scene_name, transform_path
FROM codex_search_fts
WHERE codex_search_fts MATCH 'LevelID'
LIMIT 50;
```

Use `--fts` when faster broad text search is worth extra import time and database size. Use `--no-search` for the most compact database when search convenience is not needed.

## Codex Handoff Template

Use [templates/AGENTS.md](templates/AGENTS.md) as the starting `AGENTS.md` when giving captured data to Codex or another coding agent. Fill in the placeholder paths so the agent knows where the manifest, JSONL database, optional SQLite index, interop assemblies, and static-analysis notes live.

This template also makes a good guide if you prefer not to use any AI coding agents - simply follow the instructions and you'll end up with the same results.

## Runtime Environment Snapshot

Each session writes an environment snapshot:

```text
environment/session-<id>-environment.json
environment.json
```

The snapshot records the game version, Unity version, BepInEx assembly/file versions, parsed BepInEx log-header details when `LogOutput.log` is readable, .NET runtime information, process architecture/bitness, BepInEx path values exposed by the runtime, loaded plugin metadata when available, interop assembly file inventory, and the effective BepInExposition config values. Include this file when handing data to Codex so conclusions can be tied to the exact BepInEx/plugin/config environment that produced the capture.

## Configuration

BepInEx generates the config file on first launch. Important settings:

Several numeric and mode settings include BepInEx config metadata such as acceptable ranges or allowed values. Config managers may display this metadata; invalid manual edits may be clamped or rejected by BepInEx.

- `General.Enabled`: enable or disable capture.
- `Storage.OutputDirectory`: override the default output root.
- `Capture.SceneSettleSeconds`: delay before a settled scene snapshot.
- `Capture.EnableAutomaticSettledSnapshots`: automatically take one full-scene snapshot for newly discovered scenes after scene loads/changes. On by default so fresh installs collect data without manual commands.
- `Capture.EnablePeriodicSnapshots`: enable repeated full-scene snapshots during gameplay. Off by default because large scenes can stall while walking tens of thousands of objects/components.
- `Capture.PeriodicSnapshotSeconds`: periodic snapshot interval when periodic snapshots are enabled. Set `0` to disable.
- `Capture.AutoSnapshotKnownScenes`: automatically take settled snapshots for scenes already present in the cumulative database. Off by default to avoid repeated long scene walks; use manual `snapshot` commands when you intentionally want another full scan.
- `Capture.RawSessionDetailMode`: controls detail in `sessions/*.jsonl`. `NewKnownRecords` writes session events, summaries, and detailed records only when they add to the cumulative database. `All` preserves the old full raw snapshot stream. `SummaryOnly` writes only session events, markers, errors, snapshots, and snapshot summaries.
- `Capture.CaptureInitialScene`: capture the active scene shortly after startup.
- `Capture.CaptureMemberShapes`: record reflected member shapes for observed component types.
- `Capture.CaptureMemberValues`: record safe field/property values and Unity object references from observed component instances.
- `Capture.CaptureMemberValuesForKnownComponents`: read member values from already-known object/component links. Off by default to avoid repeat getter work across sessions; enable only when backfilling values into an older database.
- `Capture.CaptureUnityMemberValues`: include UnityEngine component member values. Off by default because Unity properties are numerous and can be expensive.
- `Capture.CaptureInheritedMemberShapes`: include members declared by base types. Off by default to avoid noisy Unity and Il2CppInterop base members.
- `Capture.CaptureInteropInternalMemberShapes`: include generated Il2CppInterop `Native*` pointer members. Off by default.
- `Safety.MaxHierarchyDepth`: maximum transform recursion depth.
- `Safety.MaxObjectsPerSnapshot`: maximum GameObjects per snapshot. Raise this for stationary collection runs if DEBUG logs report `Truncated: True`; lower it if full-scene snapshots still pause gameplay.
- `Safety.MaxComponentsPerObject`: maximum components per GameObject.
- `Safety.MaxMemberValueComponentsPerSnapshot`: maximum components whose member values may be read in one snapshot.
- `Safety.MaxMemberValuesPerComponent`: maximum safe field/property values per component instance.
- `Safety.MaxMembersPerType`: maximum reflected members per observed type. Raise this in an existing config if DEBUG logs report nonzero `shape caps`.
- `Manual.CommandFileName`: command inbox filename.
- `Diagnostics.VerboseLogging`: extra diagnostics. In `DEBUG` builds, this also allows periodic snapshot summary boxes.

## Building

The project targets `net6.0` and BepInEx 6 IL2CPP. Use BepInEx 6.0.0-pre.2 or newer; the current source was built and tested with BepInEx bleeding-edge build `6.0.0-be.755`.

By default, the project file looks for reference DLLs at:

```text
./libs
```

Required reference DLLs (copy from your game's BepInEx folder after assembly generation):

- `BepInEx.Core.dll`
- `BepInEx.Unity.IL2CPP.dll`
- `Il2CppInterop.Runtime.dll`
- `Il2Cppmscorlib.dll`
- `UnityEngine.CoreModule.dll`


## Installation

Copy `BepInExposition.dll` into the target game's BepInEx plugins folder:

```text
BepInEx/plugins/
```

Launch the game to generate config and start a capture session. Each subsequent game session will add more to the capture database.

## Design Notes

BepInExposition intentionally uses JSONL as the primary store. JSONL is append-friendly, easy to inspect, easy to chunk for Codex, and avoids shipping native database dependencies inside the game process.

The cumulative database is split by category so Codex can ingest only the slices relevant to a future plugin. A later offline SQLite indexer can still be added for larger datasets.

Automatic full-scene capture is limited by default because large IL2CPP scenes can stall while walking tens of thousands of GameObjects and components. Fresh scenes get one settled snapshot automatically. Known scenes and periodic rescans are skipped unless enabled. Use `capture_commands.txt` with `snapshot <label>` when you want an intentional full rescan.

Member-value capture is intentionally sampled on settled and manual snapshots, not periodic snapshots. By default it only reads values for newly discovered object/component links. This keeps periodic discovery useful without repeatedly invoking IL2CPP property getters during gameplay.

## License

MIT. See [LICENSE](LICENSE).

## Support the Developer

If you find this project useful and feel like sending a few bucks my way, I have a Ko-Fi page here: https://ko-fi.com/Dteyn

---

You are visitor: ![Page views](https://dteyn-rad-page.netlify.app/.netlify/functions/pageviews?repo=BepInExposition)