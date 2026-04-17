using System;
using System.IO;
using System.Text;
using BepInExposition;
using BepInExposition.Config;
using BepInExposition.Model;
using BepInExposition.Persistence;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace BepInExposition.Capture;

internal sealed class CaptureCoordinator
{
    private readonly ObjectSnapshotter _snapshotter = new();
    private JsonlCaptureStore? _store;
    private bool _started;
    private bool _shutdown;
    private float _nextPeriodicSnapshotAt;
    private float _pendingSettledSnapshotAt = -1f;
    private string? _pendingSettledReason;
    private string? _lastCommandText;
    private UnityAction<Scene, LoadSceneMode>? _sceneLoadedHandler;
    private UnityAction<Scene>? _sceneUnloadedHandler;
    private UnityAction<Scene, Scene>? _activeSceneChangedHandler;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _store = JsonlCaptureStore.Open();
        _store.WriteSessionStart();

        // IL2CPP UnityAction wrappers are not normal managed delegates. Keep the converted delegates
        // rooted so Unity can invoke them later and so unsubscribe uses the same wrapper instance.
        _sceneLoadedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene, LoadSceneMode>>(new Action<Scene, LoadSceneMode>(OnSceneLoaded));
        _sceneUnloadedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene>>(new Action<Scene>(OnSceneUnloaded));
        _activeSceneChangedHandler = DelegateSupport.ConvertDelegate<UnityAction<Scene, Scene>>(new Action<Scene, Scene>(OnActiveSceneChanged));

        TrySubscribeSceneEvents();

        _nextPeriodicSnapshotAt = Time.realtimeSinceStartup + Math.Max(0f, CaptureSettings.PeriodicSnapshotSeconds.Value);

        var activeScene = SceneManager.GetActiveScene();
        if (CaptureSettings.CaptureInitialScene.Value && activeScene.IsValid())
        {
            var sceneAdded = RecordScene("initial_active_scene", activeScene);
            ScheduleSettledSnapshotIfNeeded("initial_active_scene", sceneAdded);
        }

        LogDebug($"Capture session opened at {_store.FilePath}");
#if DEBUG
        LogPrettySummary("Session Opened", null, null);
#endif
    }

    public void Tick(float now)
    {
        if (_shutdown || _store == null)
        {
            return;
        }

        TryProcessCommands();

        if (_pendingSettledSnapshotAt >= 0f && now >= _pendingSettledSnapshotAt)
        {
            var reason = _pendingSettledReason ?? "scene_settled";
            _pendingSettledSnapshotAt = -1f;
            _pendingSettledReason = null;
            CaptureSnapshot(reason, null);
        }

        var interval = CaptureSettings.PeriodicSnapshotSeconds.Value;
        if (CaptureSettings.EnablePeriodicSnapshots.Value && interval > 0f && now >= _nextPeriodicSnapshotAt)
        {
            CaptureSnapshot("periodic", null);
            _nextPeriodicSnapshotAt = now + interval;
        }
    }

    public void Shutdown(string reason)
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;

        try
        {
            TryUnsubscribeSceneEvents();

            _store?.WriteSessionEnd(reason);
#if DEBUG
            LogPrettySummary($"Session Closed: {reason}", null, null);
#endif
            _store?.Dispose();
        }
        catch (Exception ex)
        {
            Main.Logger?.LogWarning($"Capture shutdown failed: {ex.Message}");
        }
        finally
        {
            _store = null;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        var sceneAdded = RecordScene($"scene_loaded:{mode}", scene);
        ScheduleSettledSnapshotIfNeeded("scene_loaded_settled", sceneAdded);
    }

    private void TrySubscribeSceneEvents()
    {
        if (_store == null)
        {
            return;
        }

        if (_sceneLoadedHandler != null)
        {
            try
            {
                SceneManager.add_sceneLoaded(_sceneLoadedHandler);
            }
            catch (Exception ex)
            {
                _store.WriteError("subscribe_scene_loaded", ex.Message, ex.GetType().FullName, ex.StackTrace);
                _sceneLoadedHandler = null;
            }
        }

        if (_sceneUnloadedHandler != null)
        {
            try
            {
                // Some stripped Unity builds expose this wrapper but fail method unstripping at runtime.
                // Treat every scene event subscription as optional so capture can continue.
                SceneManager.add_sceneUnloaded(_sceneUnloadedHandler);
            }
            catch (Exception ex)
            {
                LogDebug($"Optional sceneUnloaded subscription unavailable: {ex.Message}");
                _sceneUnloadedHandler = null;
            }
        }

        if (_activeSceneChangedHandler != null)
        {
            try
            {
                SceneManager.add_activeSceneChanged(_activeSceneChangedHandler);
            }
            catch (Exception ex)
            {
                _store.WriteError("subscribe_active_scene_changed", ex.Message, ex.GetType().FullName, ex.StackTrace);
                _activeSceneChangedHandler = null;
            }
        }

        _store.Flush();
    }

    private void TryUnsubscribeSceneEvents()
    {
        if (_store == null)
        {
            return;
        }

        if (_sceneLoadedHandler != null)
        {
            try
            {
                SceneManager.remove_sceneLoaded(_sceneLoadedHandler);
            }
            catch (Exception ex)
            {
                _store.WriteError("unsubscribe_scene_loaded", ex.Message, ex.GetType().FullName, ex.StackTrace);
            }
            finally
            {
                _sceneLoadedHandler = null;
            }
        }

        if (_sceneUnloadedHandler != null)
        {
            try
            {
                SceneManager.remove_sceneUnloaded(_sceneUnloadedHandler);
            }
            catch (Exception ex)
            {
                _store.WriteError("unsubscribe_scene_unloaded", ex.Message, ex.GetType().FullName, ex.StackTrace);
            }
            finally
            {
                _sceneUnloadedHandler = null;
            }
        }

        if (_activeSceneChangedHandler != null)
        {
            try
            {
                SceneManager.remove_activeSceneChanged(_activeSceneChangedHandler);
            }
            catch (Exception ex)
            {
                _store.WriteError("unsubscribe_active_scene_changed", ex.Message, ex.GetType().FullName, ex.StackTrace);
            }
            finally
            {
                _activeSceneChangedHandler = null;
            }
        }
    }

    private void OnSceneUnloaded(Scene scene)
    {
        if (_store == null)
        {
            return;
        }

        _store.WriteSceneEvent(SceneRecord.FromScene("scene_unloaded", scene));
        _store.Flush();
#if DEBUG
        LogPrettySummary("Scene Event: scene_unloaded", scene, null);
#endif
    }

    private void OnActiveSceneChanged(Scene previous, Scene next)
    {
        var sceneAdded = RecordScene("active_scene_changed", next, previous.name);
        ScheduleSettledSnapshotIfNeeded("active_scene_changed_settled", sceneAdded);
    }

    private bool RecordScene(string eventKind, Scene scene, string? previousSceneName = null)
    {
        if (_store == null)
        {
            return false;
        }

        var sceneAdded = _store.WriteSceneEvent(SceneRecord.FromScene(eventKind, scene, previousSceneName));
        _store.Flush();
#if DEBUG
        LogPrettySummary($"Scene Event: {eventKind}", scene, null);
#endif
        return sceneAdded;
    }

    private void ScheduleSettledSnapshotIfNeeded(string reason, bool sceneAdded)
    {
        if (CaptureSettings.EnableAutomaticSettledSnapshots.Value && (sceneAdded || CaptureSettings.AutoSnapshotKnownScenes.Value))
        {
            ScheduleSettledSnapshot(reason);
        }
    }

    private void ScheduleSettledSnapshot(string reason)
    {
        // Scene callbacks often fire before additive objects and UI have finished settling.
        // Delaying the walk gives the hierarchy a better chance to represent the final state.
        _pendingSettledSnapshotAt = Time.realtimeSinceStartup + Math.Max(0f, CaptureSettings.SceneSettleSeconds.Value);
        _pendingSettledReason = reason;
    }

    private void CaptureSnapshot(string kind, string? markerLabel)
    {
        if (_store == null)
        {
            return;
        }

        try
        {
            var scene = SceneManager.GetActiveScene();
            var snapshotId = Guid.NewGuid().ToString("N");
            var stats = _store.Stats;
            var previousObjects = stats.NewObjects;
            var previousObjectComponents = stats.NewObjectComponents;
            var previousComponentTypes = stats.NewComponentTypes;
            var previousTypes = stats.NewTypes;
            var previousMemberShapes = stats.NewMemberShapes;
            var previousMemberValues = stats.NewMemberValues;
            _store.WriteSnapshot(new SnapshotRecord(snapshotId, kind, scene.name, markerLabel));

            var captureMemberValues = !string.Equals(kind, "periodic", StringComparison.OrdinalIgnoreCase);
            var summary = _snapshotter.Capture(scene, snapshotId, _store, captureMemberValues);
            summary.NewObjects = stats.NewObjects - previousObjects;
            summary.NewObjectComponents = stats.NewObjectComponents - previousObjectComponents;
            summary.NewComponentTypes = stats.NewComponentTypes - previousComponentTypes;
            summary.NewTypes = stats.NewTypes - previousTypes;
            summary.NewMemberShapes = stats.NewMemberShapes - previousMemberShapes;
            summary.NewMemberValues = stats.NewMemberValues - previousMemberValues;
            _store.WriteSnapshotSummary(summary);
            _store.Flush();

            LogDebug($"Snapshot {snapshotId} captured {summary.ObjectsCaptured} objects, {summary.ComponentsCaptured} components.");
#if DEBUG
            if (!string.Equals(kind, "periodic", StringComparison.OrdinalIgnoreCase) || CaptureSettings.VerboseLogging.Value)
            {
                LogPrettySummary($"Snapshot: {kind}", scene, summary);
            }
#endif
        }
        catch (Exception ex)
        {
            _store.WriteError("capture_snapshot", ex.Message, ex.GetType().FullName, ex.StackTrace);
            _store.Flush();
            Main.Logger?.LogWarning($"Snapshot failed: {ex.Message}");
        }
    }

    private void TryProcessCommands()
    {
        if (_store == null)
        {
            return;
        }

        var commandFile = _store.CommandFilePath;
        if (!File.Exists(commandFile))
        {
            return;
        }

        string commandText;
        try
        {
            commandText = File.ReadAllText(commandFile);
        }
        catch (Exception ex)
        {
            _store.WriteError("read_command_file", ex.Message, ex.GetType().FullName, null);
            return;
        }

        if (string.IsNullOrWhiteSpace(commandText) || commandText == _lastCommandText)
        {
            return;
        }

        _lastCommandText = commandText;

        foreach (var rawLine in commandText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            ProcessCommand(line);
        }

        try
        {
            File.Delete(commandFile);
        }
        catch (Exception ex)
        {
            _store.WriteError("delete_command_file", ex.Message, ex.GetType().FullName, null);
        }
    }

    private void ProcessCommand(string line)
    {
        if (_store == null)
        {
            return;
        }

        var splitAt = line.IndexOf(' ');
        var command = splitAt >= 0 ? line[..splitAt].Trim() : line;
        var argument = splitAt >= 0 ? line[(splitAt + 1)..].Trim() : string.Empty;

        switch (command.ToLowerInvariant())
        {
            case "snapshot":
                CaptureSnapshot("manual_command", string.IsNullOrWhiteSpace(argument) ? null : argument);
                break;
            case "marker":
                _store.WriteMarker(string.IsNullOrWhiteSpace(argument) ? "manual_marker" : argument, "command_file");
                _store.Flush();
                break;
            case "flush":
                _store.Flush();
                break;
            case "reload_config":
            case "reloadconfig":
                CaptureSettings.Reload();
                _store.WriteMarker("config_reloaded", "command_file");
                _store.Flush();
                break;
            case "save_config":
            case "saveconfig":
                CaptureSettings.Save();
                _store.WriteMarker("config_saved", "command_file");
                _store.Flush();
                break;
            default:
                _store.WriteError("command_file", $"Unknown command: {command}", null, null);
                break;
        }
    }

    private static void LogDebug(string message)
    {
        if (CaptureSettings.VerboseLogging.Value)
        {
            Main.Logger?.LogInfo(message);
        }
    }

#if DEBUG
    private void LogPrettySummary(string title, Scene? scene, SnapshotSummaryRecord? snapshot)
    {
        if (_store == null)
        {
            return;
        }

        var stats = _store.Stats;
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("[BepInExposition]");
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine($"| {TrimForBox(title, 58),-58} |");

        if (scene.HasValue)
        {
            var sceneName = string.IsNullOrWhiteSpace(scene.Value.name) ? "<unnamed>" : scene.Value.name;
            builder.AppendLine($"| Scene: {TrimForBox(sceneName, 51),-51} |");
        }

        if (snapshot != null)
        {
            builder.AppendLine($"| Snapshot objects: {snapshot.ObjectsCaptured,-8} components: {snapshot.ComponentsCaptured,-8} |");
            builder.AppendLine($"| Snapshot new: obj {snapshot.NewObjects,-6} links {snapshot.NewObjectComponents,-6} types {snapshot.NewComponentTypes,-6} |");
            builder.AppendLine($"| Snapshot new: shapes {snapshot.NewMemberShapes,-6} values {snapshot.NewMemberValues,-6} |");
            builder.AppendLine($"| Value reads: components {snapshot.MemberValueComponentsCaptured,-6} skipped {snapshot.MemberValueComponentsSkipped,-6} |");
            builder.AppendLine($"| Truncated: {snapshot.Truncated,-5} shape caps: {snapshot.MemberListsTruncated,-6} value caps: {snapshot.MemberValueListsTruncated,-6} |");
        }

        builder.AppendLine($"| Run scenes: {stats.ScenesObserved,-6} new: {stats.NewScenes,-6} snapshots: {stats.SnapshotsWritten,-6} |");
        builder.AppendLine($"| Run objects: {stats.ObjectsObserved,-6} new: {stats.NewObjects,-6} components: {stats.ComponentsObserved,-6} |");
        builder.AppendLine($"| New component types: {stats.NewComponentTypes,-6} shapes: {stats.NewMemberShapes,-6} values: {stats.NewMemberValues,-6} |");
        builder.AppendLine("+------------------------------------------------------------+");
        builder.AppendLine("| Known Database                                             |");

        foreach (var file in _store.GetKnownFileStats())
        {
            builder.AppendLine($"| {TrimForBox(file.Name, 20),-20} total {file.RecordCount,7}  added {file.AddedThisRun,7} |");
        }

        builder.AppendLine("+------------------------------------------------------------+");
        Main.Logger?.LogInfo(builder.ToString());
    }

    private static string TrimForBox(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
#endif
}
