using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BepInExposition;
using BepInExposition.Model;

namespace BepInExposition.Persistence;

internal sealed class KnownDatabase : IDisposable
{
    private const int SampleLimit = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _sessionId;
    private readonly string _databaseDirectory;
    private readonly string _reportsDirectory;
    private readonly string _manifestsDirectory;
    private readonly Dictionary<string, KnownFile> _files = new(StringComparer.Ordinal);

    private KnownDatabase(string outputRoot, string sessionId)
    {
        OutputRoot = outputRoot;
        _sessionId = sessionId;
        _databaseDirectory = Path.Combine(outputRoot, "database");
        _reportsDirectory = Path.Combine(outputRoot, "reports");
        _manifestsDirectory = Path.Combine(outputRoot, "manifests");

        Directory.CreateDirectory(_databaseDirectory);
        Directory.CreateDirectory(_reportsDirectory);
        Directory.CreateDirectory(_manifestsDirectory);

        Register("scenes", "scenes.jsonl");
        Register("objects", "objects.jsonl");
        Register("component_types", "component_types.jsonl");
        Register("object_components", "object_components.jsonl");
        Register("types", "types.jsonl");
        Register("member_shapes", "member_shapes.jsonl");
        Register("member_values", "member_values.jsonl");
    }

    public string OutputRoot { get; }

    public static KnownDatabase Open(string outputRoot, string sessionId)
    {
        return new KnownDatabase(outputRoot, sessionId);
    }

    public IReadOnlyList<KnownDatabaseFileStats> GetFileStats()
    {
        return _files.Values
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => new KnownDatabaseFileStats(file.Name, MakeRelative(file.Path), file.RecordCount, file.AddedThisRun))
            .ToArray();
    }

    public bool ObserveScene(SceneRecord record, SessionCaptureStats stats)
    {
        stats.ScenesObserved++;

        if (string.IsNullOrWhiteSpace(record.SceneName) && string.IsNullOrWhiteSpace(record.ScenePath))
        {
            return false;
        }

        var key = string.Join("|", record.SceneName, record.ScenePath, record.BuildIndex);
        var payload = new
        {
            record.SceneName,
            record.ScenePath,
            record.BuildIndex
        };

        if (!WriteIfNew("scenes", key, payload))
        {
            return false;
        }

        stats.NewScenes++;
        AddSample(stats.NewSceneSamples, string.IsNullOrWhiteSpace(record.SceneName) ? record.ScenePath : record.SceneName);
        return true;
    }

    public bool ObserveObject(GameObjectRecord record, SessionCaptureStats stats)
    {
        stats.ObjectsObserved++;

        if (string.IsNullOrWhiteSpace(record.SemanticKey))
        {
            return false;
        }

        var payload = new
        {
            record.SemanticKey,
            record.SceneName,
            record.TransformPath,
            record.ObjectName,
            record.Tag,
            record.Layer,
            record.ChildCount
        };

        if (!WriteIfNew("objects", record.SemanticKey, payload))
        {
            return false;
        }

        stats.NewObjects++;
        AddSample(stats.NewObjectSamples, record.SemanticKey);
        return true;
    }

    public bool ObserveComponent(ComponentRecord record, SessionCaptureStats stats)
    {
        stats.ComponentsObserved++;

        if (record.TypeFullName == "<missing>")
        {
            return false;
        }

        var typeKey = string.Join("|", record.AssemblyName, record.TypeFullName);
        var typePayload = new
        {
            record.TypeFullName,
            record.AssemblyName
        };

        var addedType = WriteIfNew("component_types", typeKey, typePayload);
        if (addedType)
        {
            stats.NewComponentTypes++;
            AddSample(stats.NewComponentTypeSamples, record.TypeFullName);
        }

        var objectComponentKey = string.Join("|", record.ObjectSemanticKey, record.ComponentIndex, record.TypeFullName);
        var objectComponentPayload = new
        {
            record.ObjectSemanticKey,
            record.TransformPath,
            record.ComponentIndex,
            record.TypeFullName,
            record.AssemblyName,
            record.EnabledState
        };

        var addedLink = WriteIfNew("object_components", objectComponentKey, objectComponentPayload);
        if (addedLink)
        {
            stats.NewObjectComponents++;
        }

        return addedType || addedLink;
    }

    public bool ObserveType(TypeCatalogRecord record, SessionCaptureStats stats)
    {
        stats.TypesObserved++;

        var key = string.Join("|", record.AssemblyName, record.TypeFullName);
        if (WriteIfNew("types", key, record))
        {
            stats.NewTypes++;
            return true;
        }

        return false;
    }

    public bool ObserveMemberShape(MemberShapeRecord record, SessionCaptureStats stats)
    {
        stats.MemberShapesObserved++;

        var key = string.Join(
            "|",
            record.ObservedTypeFullName,
            record.DeclaringTypeFullName,
            record.MemberKind,
            record.MemberName,
            record.ValueTypeFullName,
            record.ParameterTypes);

        if (!WriteIfNew("member_shapes", key, record))
        {
            return false;
        }

        stats.NewMemberShapes++;
        AddSample(stats.NewMemberShapeSamples, $"{record.ObservedTypeFullName}.{record.MemberName}");
        return true;
    }

    public bool ObserveMemberValue(MemberValueRecord record, SessionCaptureStats stats)
    {
        stats.MemberValuesObserved++;

        var key = string.Join(
            "|",
            record.ObjectSemanticKey,
            record.ComponentIndex,
            record.ComponentTypeFullName,
            record.DeclaringTypeFullName,
            record.MemberKind,
            record.MemberName);

        if (!WriteIfNew("member_values", key, record))
        {
            return false;
        }

        stats.NewMemberValues++;
        AddSample(stats.NewMemberValueSamples, $"{record.ComponentTypeFullName}.{record.MemberName} = {record.SerializedValue}");
        return true;
    }

    public void WriteSessionOutputs(SessionCaptureStats stats, string reason)
    {
        Flush();
        stats.EndedAtUtc = DateTime.UtcNow.ToString("O");
        stats.ReportFilePath = WriteReport(stats, reason);
        stats.ManifestFilePath = WriteManifest(stats, reason);
    }

    public void Flush()
    {
        foreach (var file in _files.Values)
        {
            file.Flush();
        }
    }

    public void Dispose()
    {
        foreach (var file in _files.Values)
        {
            file.Dispose();
        }
    }

    private void Register(string name, string fileName)
    {
        var path = Path.Combine(_databaseDirectory, fileName);
        _files[name] = KnownFile.Load(name, path);
    }

    private bool WriteIfNew<T>(string fileName, string key, T payload)
    {
        if (!_files.TryGetValue(fileName, out var file))
        {
            throw new InvalidOperationException($"Unknown known database file: {fileName}");
        }

        if (!file.Keys.Add(key))
        {
            return false;
        }

        // Known-data files are append-only and keyed. This keeps the handoff set compact even when
        // raw session detail is configured to capture only newly discovered facts.
        var line = JsonSerializer.Serialize(new KnownEnvelope<T>(key, _sessionId, DateTime.UtcNow.ToString("O"), payload), JsonOptions);
        file.AppendLine(line);
        file.RecordCount++;
        file.AddedThisRun++;
        return true;
    }

    private string WriteReport(SessionCaptureStats stats, string reason)
    {
        var path = Path.Combine(_reportsDirectory, $"session-{stats.SessionId}-summary.md");
        var builder = new StringBuilder();
        builder.AppendLine("# BepInExposition Session Summary");
        builder.AppendLine();
        builder.AppendLine($"- Session: `{stats.SessionId}`");
        builder.AppendLine($"- Started UTC: `{stats.StartedAtUtc}`");
        builder.AppendLine($"- Ended UTC: `{stats.EndedAtUtc}`");
        builder.AppendLine($"- End reason: `{reason}`");
        builder.AppendLine($"- Raw session JSONL: `{MakeRelative(stats.SessionFilePath)}`");
        builder.AppendLine($"- Raw session detail mode: `{stats.RawSessionDetailMode}`");
        builder.AppendLine($"- Runtime environment: `{MakeRelative(stats.EnvironmentFilePath ?? string.Empty)}`");
        builder.AppendLine();
        builder.AppendLine("## Observed This Run");
        builder.AppendLine();
        builder.AppendLine($"- Scenes observed: {stats.ScenesObserved}; new known scenes: {stats.NewScenes}");
        builder.AppendLine($"- Snapshots written: {stats.SnapshotsWritten}");
        builder.AppendLine($"- GameObjects observed: {stats.ObjectsObserved}; new known objects: {stats.NewObjects}");
        builder.AppendLine($"- Components observed: {stats.ComponentsObserved}; new object/component links: {stats.NewObjectComponents}; new component types: {stats.NewComponentTypes}");
        builder.AppendLine($"- Types observed: {stats.TypesObserved}; new known types: {stats.NewTypes}");
        builder.AppendLine($"- Member shapes observed: {stats.MemberShapesObserved}; new member shapes: {stats.NewMemberShapes}");
        builder.AppendLine($"- Member values observed: {stats.MemberValuesObserved}; new member values: {stats.NewMemberValues}");
        builder.AppendLine($"- Markers written: {stats.MarkersWritten}");
        builder.AppendLine($"- Errors captured: {stats.ErrorsWritten}");
        builder.AppendLine();
        AppendSamples(builder, "New Scenes", stats.NewSceneSamples);
        AppendSamples(builder, "New Objects", stats.NewObjectSamples);
        AppendSamples(builder, "New Component Types", stats.NewComponentTypeSamples);
        AppendSamples(builder, "New Member Shapes", stats.NewMemberShapeSamples);
        AppendSamples(builder, "New Member Values", stats.NewMemberValueSamples);
        builder.AppendLine("## Known Database Files");
        builder.AppendLine();

        foreach (var file in _files.Values.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            builder.AppendLine($"- `{MakeRelative(file.Path)}`: {file.RecordCount} known records, {file.AddedThisRun} added this run");
        }

        builder.AppendLine();
        builder.AppendLine("## Codex Handoff");
        builder.AppendLine();
        builder.AppendLine("Start with `manifest.json`, this report, and the specific `database/*.jsonl` files relevant to the plugin being planned. Use the raw session JSONL only when event ordering or exact snapshot context matters.");

        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private string WriteManifest(SessionCaptureStats stats, string reason)
    {
        var path = Path.Combine(_manifestsDirectory, $"session-{stats.SessionId}-manifest.json");
        var payload = new
        {
            SchemaVersion = 1,
            PluginName = PluginInfo.Name,
            PluginVersion = PluginInfo.Version,
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            SessionId = stats.SessionId,
            StartedAtUtc = stats.StartedAtUtc,
            EndedAtUtc = stats.EndedAtUtc,
            EndReason = reason,
            OutputRoot,
            RawSessionJsonl = stats.SessionFilePath,
            SummaryReport = stats.ReportFilePath,
            RuntimeEnvironment = stats.EnvironmentFilePath,
            DatabaseFiles = _files.Values.OrderBy(file => file.Name, StringComparer.Ordinal).Select(file => new
            {
                file.Name,
                file.Path,
                file.RecordCount,
                file.AddedThisRun
            }).ToArray(),
            CodexRecommendedInputs = new[]
            {
                "manifest.json",
                MakeRelative(stats.ReportFilePath ?? string.Empty),
                MakeRelative(stats.EnvironmentFilePath ?? string.Empty),
                "database/scenes.jsonl",
                "database/objects.jsonl",
                "database/component_types.jsonl",
                "database/object_components.jsonl",
                "database/types.jsonl",
                "database/member_shapes.jsonl",
                "database/member_values.jsonl"
            }
        };

        var manifestJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        File.WriteAllText(path, manifestJson + Environment.NewLine);
        File.WriteAllText(Path.Combine(OutputRoot, "manifest.json"), manifestJson + Environment.NewLine);
        return path;
    }

    private string MakeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetRelativePath(OutputRoot, path).Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    private static void AppendSamples(StringBuilder builder, string title, IReadOnlyCollection<string> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {title}");
        builder.AppendLine();

        foreach (var sample in samples)
        {
            builder.AppendLine($"- `{sample}`");
        }

        builder.AppendLine();
    }

    private static void AddSample(ICollection<string> samples, string value)
    {
        if (samples.Count < SampleLimit && !string.IsNullOrWhiteSpace(value))
        {
            samples.Add(value);
        }
    }

    internal sealed class KnownFile : IDisposable
    {
        private StreamWriter? _writer;

        private KnownFile(string name, string path, HashSet<string> keys, int recordCount)
        {
            Name = name;
            Path = path;
            Keys = keys;
            RecordCount = recordCount;
        }

        public string Name { get; }
        public string Path { get; }
        public HashSet<string> Keys { get; }
        public int RecordCount { get; set; }
        public int AddedThisRun { get; set; }

        public void AppendLine(string line)
        {
            if (_writer == null)
            {
                // Open lazily so runs that discover nothing new do not rewrite database files.
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream) { AutoFlush = false };
            }

            _writer.WriteLine(line);
        }

        public void Flush()
        {
            _writer?.Flush();
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _writer = null;
        }

        public static KnownFile Load(string name, string path)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;

            if (!File.Exists(path))
            {
                return new KnownFile(name, path, keys, 0);
            }

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                count++;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("key", out var keyElement))
                    {
                        var key = keyElement.GetString();
                        if (!string.IsNullOrEmpty(key))
                        {
                            keys.Add(key);
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            return new KnownFile(name, path, keys, count);
        }
    }

    private sealed record KnownEnvelope<T>(string Key, string FirstSeenSessionId, string FirstSeenAtUtc, T Payload);
}
