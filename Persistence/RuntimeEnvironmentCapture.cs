using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using BepInExposition.Config;
using UnityEngine;

namespace BepInExposition.Persistence;

internal static class RuntimeEnvironmentCapture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Write(string outputRoot, string sessionId)
    {
        var directory = Path.Combine(outputRoot, "environment");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"session-{sessionId}-environment.json");
        var payload = Capture(outputRoot, sessionId);
        var json = JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine;
        File.WriteAllText(path, json);
        File.WriteAllText(Path.Combine(outputRoot, "environment.json"), json);
        return path;
    }

    private static object Capture(string outputRoot, string sessionId)
    {
        var paths = CaptureBepInExPaths();
        return new
        {
            SchemaVersion = 1,
            PluginName = PluginInfo.Name,
            PluginVersion = PluginInfo.Version,
            SessionId = sessionId,
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            Game = new
            {
                Version = SafeRead(() => Application.version),
                UnityVersion = SafeRead(() => Application.unityVersion),
                Platform = SafeRead(() => Application.platform.ToString())
            },
            BepInEx = new
            {
                CoreAssemblyVersion = typeof(Paths).Assembly.GetName().Version?.ToString() ?? string.Empty,
                CoreAssemblyInformationalVersion = GetInformationalVersion(typeof(Paths).Assembly),
                UnityIl2CppAssemblyVersion = GetAssemblyVersion("BepInEx.Unity.IL2CPP"),
                UnityIl2CppAssemblyInformationalVersion = GetAssemblyInformationalVersion("BepInEx.Unity.IL2CPP"),
                LogHeader = CaptureBepInExLogHeader(paths),
                Assemblies = CaptureBepInExAssemblies(paths),
                Paths = paths,
                LoadedPlugins = CaptureLoadedPlugins()
            },
            DotNetRuntime = CaptureDotNetRuntime(),
            Process = CaptureProcess(),
            OutputRoot = outputRoot,
            EffectiveConfig = CaptureSettings.GetEffectiveConfig(),
            InteropAssemblies = CaptureInteropAssemblies(paths)
        };
    }

    private static IReadOnlyDictionary<string, string> CaptureBepInExPaths()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in typeof(Paths).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            if (property.PropertyType != typeof(string))
            {
                continue;
            }

            try
            {
                result[property.Name] = property.GetValue(null) as string ?? string.Empty;
            }
            catch
            {
                result[property.Name] = string.Empty;
            }
        }

        return result;
    }

    private static object CaptureDotNetRuntime()
    {
        return new
        {
            EnvironmentVersion = Environment.Version.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            OSDescription = RuntimeInformation.OSDescription,
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString()
        };
    }

    private static object CaptureProcess()
    {
        return new
        {
            ProcessName = SafeRead(() => Process.GetCurrentProcess().ProcessName),
            Is64BitProcess = Environment.Is64BitProcess,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            ProcessorCount = Environment.ProcessorCount
        };
    }

    private static object CaptureBepInExLogHeader(IReadOnlyDictionary<string, string> paths)
    {
        var logPath = paths.TryGetValue("BepInExRootPath", out var root) && !string.IsNullOrWhiteSpace(root)
            ? Path.Combine(root, "LogOutput.log")
            : string.Empty;

        var lines = ReadLogHeaderLines(logPath);
        return new
        {
            LogPath = logPath,
            BepInExVersion = ParseLogValue(lines, @"BepInEx (?<value>\S+) -"),
            TargetProcess = ParseLogValue(lines, @"BepInEx \S+ - (?<value>.*?) \("),
            BuildTimestamp = ParseLogValue(lines, @"BepInEx \S+ - .*? \((?<value>.*?)\)"),
            BuildCommit = ParseLogValue(lines, @"Built from commit (?<value>\S+)"),
            SystemPlatform = ParseLogValue(lines, @"System platform: (?<value>.+)$"),
            ProcessBitness = ParseLogValue(lines, @"Process bitness: (?<value>.+)$"),
            UnityVersion = ParseLogValue(lines, @"Running under Unity (?<value>.+)$"),
            RuntimeVersion = ParseLogValue(lines, @"Runtime version: (?<value>.+)$"),
            RuntimeInformation = ParseLogValue(lines, @"Runtime information: (?<value>.+)$")
        };
    }

    private static IReadOnlyList<string> ReadLogHeaderLines(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count >= 32 || line.Contains("Chainloader initialized", StringComparison.Ordinal))
                {
                    break;
                }

                lines.Add(line);
            }
        }
        catch
        {
            return Array.Empty<string>();
        }

        return lines;
    }

    private static string ParseLogValue(IEnumerable<string> lines, string pattern)
    {
        foreach (var line in lines)
        {
            var match = Regex.Match(line, pattern, RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups["value"].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<object> CaptureBepInExAssemblies(IReadOnlyDictionary<string, string> paths)
    {
        var candidates = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.TryGetValue("BepInExAssemblyPath", out var corePath) && !string.IsNullOrWhiteSpace(corePath))
        {
            candidates.Add(corePath);
        }

        if (paths.TryGetValue("BepInExAssemblyDirectory", out var directory) && Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory, "BepInEx*.dll", SearchOption.TopDirectoryOnly))
            {
                candidates.Add(file);
            }
        }

        return candidates.Select(CaptureAssemblyFile).ToArray();
    }

    private static object CaptureAssemblyFile(string path)
    {
        var info = new FileInfo(path);
        var versionInfo = SafeFileVersionInfo(path);
        return new
        {
            Path = path,
            FileName = info.Name,
            SizeBytes = info.Exists ? info.Length : 0,
            LastWriteUtc = info.Exists ? info.LastWriteTimeUtc.ToString("O") : string.Empty,
            AssemblyName = TryReadAssemblyName(path),
            FileVersion = versionInfo?.FileVersion ?? string.Empty,
            ProductVersion = versionInfo?.ProductVersion ?? string.Empty
        };
    }

    private static FileVersionInfo? SafeFileVersionInfo(string path)
    {
        try
        {
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetAssemblyVersion(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        return assembly?.GetName().Version?.ToString() ?? string.Empty;
    }

    private static string GetAssemblyInformationalVersion(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        return assembly == null ? string.Empty : GetInformationalVersion(assembly);
    }

    private static string GetInformationalVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
    }

    private static IReadOnlyList<object> CaptureLoadedPlugins()
    {
        foreach (var source in EnumerateChainloaderSources())
        {
            if (TryReadPluginInfos(source, out var pluginInfos))
            {
                return CapturePluginInfos(pluginInfos);
            }
        }

        return Array.Empty<object>();
    }

    private static IEnumerable<object> EnumerateChainloaderSources()
    {
        var seen = new HashSet<object>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal))
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (!type.Name.Contains("Chainloader", StringComparison.OrdinalIgnoreCase)
                    && (type.FullName == null || !type.FullName.Contains("Chainloader", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (seen.Add(type))
                {
                    yield return type;
                }

                // BepInEx 6 IL2CPP exposes plugin inventory through runtime-specific chainloader types.
                foreach (var propertyName in new[] { "Instance", "Current", "Chainloader" })
                {
                    var value = ReadProperty(type, null, propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (value != null && seen.Add(value))
                    {
                        yield return value;
                    }
                }
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).Cast<Type>();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static bool TryReadPluginInfos(object source, out IEnumerable pluginInfos)
    {
        var sourceType = source as Type ?? source.GetType();
        var sourceInstance = source is Type ? null : source;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy
            | (sourceInstance == null ? BindingFlags.Static : BindingFlags.Instance);

        var value = ReadProperty(sourceType, sourceInstance, "PluginInfos", flags)
            ?? ReadField(sourceType, sourceInstance, "PluginInfos", flags);

        if (value is IEnumerable enumerable)
        {
            pluginInfos = enumerable;
            return true;
        }

        pluginInfos = Array.Empty<object>();
        return false;
    }

    private static IReadOnlyList<object> CapturePluginInfos(IEnumerable pluginInfos)
    {
        var plugins = new List<object>();
        foreach (var item in pluginInfos)
        {
            if (item == null)
            {
                plugins.Add(CapturePluginInfo(null));
                continue;
            }

            var pluginInfo = item;
            if (item is DictionaryEntry dictionaryEntry)
            {
                pluginInfo = dictionaryEntry.Value;
            }
            else
            {
                var valueProperty = item.GetType().GetProperty("Value");
                pluginInfo = valueProperty?.GetValue(item) ?? item;
            }

            plugins.Add(CapturePluginInfo(pluginInfo));
        }

        return plugins;
    }

    private static object? ReadProperty(Type type, object? instance, string name, BindingFlags flags)
    {
        try
        {
            return type.GetProperty(name, flags)?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadField(Type type, object? instance, string name, BindingFlags flags)
    {
        try
        {
            return type.GetField(name, flags)?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static object CapturePluginInfo(object? pluginInfo)
    {
        if (pluginInfo == null)
        {
            return new { Guid = string.Empty, Name = string.Empty, Version = string.Empty, Location = string.Empty };
        }

        var metadata = GetProperty(pluginInfo, "Metadata");
        return new
        {
            Guid = ReadNestedString(metadata, "GUID"),
            Name = ReadNestedString(metadata, "Name"),
            Version = ReadNestedString(metadata, "Version"),
            Location = ReadString(pluginInfo, "Location"),
            TypeName = ReadString(pluginInfo, "TypeName")
        };
    }

    private static IReadOnlyList<object> CaptureInteropAssemblies(IReadOnlyDictionary<string, string> paths)
    {
        var directories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in paths)
        {
            if (pair.Key.Contains("Interop", StringComparison.OrdinalIgnoreCase) && Directory.Exists(pair.Value))
            {
                directories.Add(pair.Value);
            }
        }

        if (paths.TryGetValue("BepInExRootPath", out var root) && !string.IsNullOrWhiteSpace(root))
        {
            var interop = Path.Combine(root, "interop");
            if (Directory.Exists(interop))
            {
                directories.Add(interop);
            }
        }

        var assemblies = new List<object>();
        foreach (var directory in directories)
        {
            foreach (var file in Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                assemblies.Add(new
                {
                    Directory = directory,
                    FileName = info.Name,
                    Path = file,
                    SizeBytes = info.Length,
                    LastWriteUtc = info.LastWriteTimeUtc.ToString("O"),
                    AssemblyName = TryReadAssemblyName(file)
                });
            }
        }

        return assemblies;
    }

    private static string TryReadAssemblyName(string path)
    {
        try
        {
            return AssemblyName.GetAssemblyName(path).FullName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static object? GetProperty(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadNestedString(object? target, string name)
    {
        if (target == null)
        {
            return string.Empty;
        }

        return ReadString(target, name);
    }

    private static string ReadString(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target)?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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
}
