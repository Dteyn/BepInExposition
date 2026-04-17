using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BepInExposition;
using BepInExposition.Config;
using BepInExposition.Model;
using BepInEx;
using UnityEngine;

namespace BepInExposition.Persistence;

internal sealed class JsonlCaptureStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly StreamWriter _writer;
    private readonly string _sessionId;
    private readonly KnownDatabase _knownDatabase;
    private readonly SessionCaptureStats _stats;
    private bool _disposed;

    private JsonlCaptureStore(string sessionId, string outputRoot, string filePath, StreamWriter writer)
    {
        _sessionId = sessionId;
        FilePath = filePath;
        _writer = writer;
        _knownDatabase = KnownDatabase.Open(outputRoot, sessionId);
        _stats = new SessionCaptureStats(sessionId, DateTime.UtcNow.ToString("O"), outputRoot, filePath);
        _stats.RawSessionDetailMode = NormalizeRawSessionDetailMode(CaptureSettings.RawSessionDetailMode.Value);
    }

    public string FilePath { get; }

    public string CommandFilePath => Path.Combine(_stats.OutputRoot, CaptureSettings.CommandFileName.Value);

    public SessionCaptureStats Stats => _stats;

    public IReadOnlyList<KnownDatabaseFileStats> GetKnownFileStats()
    {
        return _knownDatabase.GetFileStats();
    }

    public static JsonlCaptureStore Open()
    {
        var outputDirectory = CaptureSettings.OutputDirectory.Value;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            outputDirectory = Path.Combine(Paths.BepInExRootPath, "BepInExposition");
        }

        Directory.CreateDirectory(outputDirectory);
        // Raw sessions stay separate from the deduplicated database so Codex can choose between
        // compact known facts and full event history.
        var sessionsDirectory = Path.Combine(outputDirectory, "sessions");
        Directory.CreateDirectory(sessionsDirectory);

        var sessionId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var filePath = Path.Combine(sessionsDirectory, $"session-{sessionId}.jsonl");
        var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream) { AutoFlush = false };
        return new JsonlCaptureStore(sessionId, outputDirectory, filePath, writer);
    }

    public void WriteSessionStart()
    {
        try
        {
            _stats.EnvironmentFilePath = RuntimeEnvironmentCapture.Write(_stats.OutputRoot, _sessionId);
        }
        catch (Exception ex)
        {
            Write("error", new ErrorRecord("write_environment", ex.Message, ex.GetType().FullName, Truncate(ex.StackTrace, 2000)));
        }

        Write("session_start", new SessionStartRecord(
            _sessionId,
            _stats.StartedAtUtc,
            PluginInfo.Version,
            SafeRead(() => Application.version),
            SafeRead(() => Application.unityVersion),
            SafeRead(() => Application.platform.ToString())));
    }

    public void WriteSessionEnd(string reason)
    {
        Write("session_end", new SessionEndRecord(_sessionId, DateTime.UtcNow.ToString("O"), reason));
        Flush();

        try
        {
            // Reports and manifests are generated after the final flush so their counts match disk.
            _knownDatabase.WriteSessionOutputs(_stats, reason);
        }
        catch (Exception ex)
        {
            Write("error", new ErrorRecord("write_session_outputs", ex.Message, ex.GetType().FullName, Truncate(ex.StackTrace, 2000)));
            Flush();
        }
    }

    public bool WriteSceneEvent(SceneRecord record)
    {
        Write("scene", record);
        return _knownDatabase.ObserveScene(record, _stats);
    }

    public void WriteSnapshot(SnapshotRecord record)
    {
        Write("snapshot", record);
        _stats.SnapshotsWritten++;
    }

    public void WriteObject(GameObjectRecord record)
    {
        var added = _knownDatabase.ObserveObject(record, _stats);
        if (ShouldWriteRawDetail(added))
        {
            Write("game_object", record);
        }
    }

    public bool WriteComponent(ComponentRecord record)
    {
        var added = _knownDatabase.ObserveComponent(record, _stats);
        if (ShouldWriteRawDetail(added))
        {
            Write("component", record);
        }

        return added;
    }

    public void WriteTypeCatalog(TypeCatalogRecord record)
    {
        var added = _knownDatabase.ObserveType(record, _stats);
        if (ShouldWriteRawDetail(added))
        {
            Write("type_catalog", record);
        }
    }

    public void WriteMemberShape(MemberShapeRecord record)
    {
        var added = _knownDatabase.ObserveMemberShape(record, _stats);
        if (ShouldWriteRawDetail(added))
        {
            Write("member_shape", record);
        }
    }

    public void WriteMemberValue(MemberValueRecord record)
    {
        var added = _knownDatabase.ObserveMemberValue(record, _stats);
        if (ShouldWriteRawDetail(added))
        {
            Write("member_value", record);
        }
    }

    public void WriteSnapshotSummary(SnapshotSummaryRecord record) => Write("snapshot_summary", record);

    public void WriteMarker(string label, string source)
    {
        Write("marker", new MarkerRecord(label, source));
        _stats.MarkersWritten++;
    }

    public void WriteError(string stage, string message, string? typeName, string? stackHint)
    {
        Write("error", new ErrorRecord(stage, message, typeName, Truncate(stackHint, 2000)));
        _stats.ErrorsWritten++;
    }

    public void Flush()
    {
        if (!_disposed)
        {
            _writer.Flush();
            _knownDatabase.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _knownDatabase.Dispose();
        _writer.Dispose();
    }

    private void Write<T>(string recordType, T payload)
    {
        if (_disposed)
        {
            return;
        }

        var envelope = new CaptureEnvelope<T>(
            recordType,
            _sessionId,
            DateTime.UtcNow.ToString("O"),
            payload);

        _writer.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private bool ShouldWriteRawDetail(bool addedKnownRecord)
    {
        return _stats.RawSessionDetailMode switch
        {
            "All" => true,
            "SummaryOnly" => false,
            _ => addedKnownRecord
        };
    }

    private static string NormalizeRawSessionDetailMode(string? value)
    {
        if (string.Equals(value, "All", StringComparison.OrdinalIgnoreCase))
        {
            return "All";
        }

        if (string.Equals(value, "SummaryOnly", StringComparison.OrdinalIgnoreCase))
        {
            return "SummaryOnly";
        }

        return "NewKnownRecords";
    }

    private static string SafeRead(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private sealed record CaptureEnvelope<T>(string Type, string SessionId, string CapturedAtUtc, T Payload);
}
