using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;

namespace BepInExposition.Config;

internal static class CaptureSettings
{
    private static ConfigFile? _config;

    public static ConfigEntry<bool> Enabled { get; private set; } = null!;
    public static ConfigEntry<string> OutputDirectory { get; private set; } = null!;
    public static ConfigEntry<float> SceneSettleSeconds { get; private set; } = null!;
    public static ConfigEntry<bool> EnableAutomaticSettledSnapshots { get; private set; } = null!;
    public static ConfigEntry<bool> EnablePeriodicSnapshots { get; private set; } = null!;
    public static ConfigEntry<float> PeriodicSnapshotSeconds { get; private set; } = null!;
    public static ConfigEntry<bool> AutoSnapshotKnownScenes { get; private set; } = null!;
    public static ConfigEntry<string> RawSessionDetailMode { get; private set; } = null!;
    public static ConfigEntry<int> MaxHierarchyDepth { get; private set; } = null!;
    public static ConfigEntry<int> MaxObjectsPerSnapshot { get; private set; } = null!;
    public static ConfigEntry<int> MaxComponentsPerObject { get; private set; } = null!;
    public static ConfigEntry<bool> CaptureMemberShapes { get; private set; } = null!;
    public static ConfigEntry<bool> CaptureMemberValues { get; private set; } = null!;
    public static ConfigEntry<bool> CaptureMemberValuesForKnownComponents { get; private set; } = null!;
    public static ConfigEntry<bool> CaptureInheritedMemberShapes { get; private set; } = null!;
    public static ConfigEntry<bool> CaptureInteropInternalMemberShapes { get; private set; } = null!;
    public static ConfigEntry<bool> CaptureUnityMemberValues { get; private set; } = null!;
    public static ConfigEntry<int> MaxMemberValueComponentsPerSnapshot { get; private set; } = null!;
    public static ConfigEntry<int> MaxMemberValuesPerComponent { get; private set; } = null!;
    public static ConfigEntry<int> MaxMembersPerType { get; private set; } = null!;
    public static ConfigEntry<string> CommandFileName { get; private set; } = null!;
    public static ConfigEntry<bool> CaptureInitialScene { get; private set; } = null!;
    public static ConfigEntry<bool> VerboseLogging { get; private set; } = null!;

    public static void Load(ConfigFile config)
    {
        _config = config;

        Enabled = config.Bind("General", "Enabled", true, "Enable runtime data capture.");
        OutputDirectory = config.Bind(
            "Storage",
            "OutputDirectory",
            string.Empty,
            "Directory for JSONL capture files. Empty uses BepInEx/BepInExposition.");
        SceneSettleSeconds = config.Bind(
            "Capture",
            "SceneSettleSeconds",
            5f,
            new ConfigDescription(
                "Seconds to wait after a scene event before taking a settled snapshot.",
                new AcceptableValueRange<float>(0f, 120f)));
        EnableAutomaticSettledSnapshots = config.Bind(
            "Capture",
            "EnableAutomaticSettledSnapshots",
            true,
            "Automatically take one full-scene snapshot for newly discovered scenes after scene loads/changes. Disable for manual-only collection.");
        EnablePeriodicSnapshots = config.Bind(
            "Capture",
            "EnablePeriodicSnapshots",
            false,
            "Take periodic full-scene snapshots during gameplay. Off by default because large IL2CPP scenes can stall while walking objects/components.");
        PeriodicSnapshotSeconds = config.Bind(
            "Capture",
            "PeriodicSnapshotSeconds",
            0f,
            new ConfigDescription(
                "Seconds between periodic snapshots when EnablePeriodicSnapshots is true. Set 0 to disable.",
                new AcceptableValueRange<float>(0f, 3600f)));
        AutoSnapshotKnownScenes = config.Bind(
            "Capture",
            "AutoSnapshotKnownScenes",
            false,
            "Automatically take settled snapshots for scenes already present in the cumulative database. Off by default to avoid repeated long scene walks.");
        RawSessionDetailMode = config.Bind(
            "Capture",
            "RawSessionDetailMode",
            "NewKnownRecords",
            new ConfigDescription(
                "Controls detail written to sessions/*.jsonl. The cumulative database is still updated either way.",
                new AcceptableValueList<string>("NewKnownRecords", "All", "SummaryOnly")));
        MaxHierarchyDepth = config.Bind(
            "Safety",
            "MaxHierarchyDepth",
            32,
            new ConfigDescription(
                "Maximum transform hierarchy depth captured from each scene root.",
                new AcceptableValueRange<int>(1, 256)));
        MaxObjectsPerSnapshot = config.Bind(
            "Safety",
            "MaxObjectsPerSnapshot",
            25000,
            new ConfigDescription(
                "Maximum GameObjects captured in one snapshot.",
                new AcceptableValueRange<int>(1, 250000)));
        MaxComponentsPerObject = config.Bind(
            "Safety",
            "MaxComponentsPerObject",
            96,
            new ConfigDescription(
                "Maximum components recorded per GameObject.",
                new AcceptableValueRange<int>(1, 512)));
        CaptureMemberShapes = config.Bind(
            "Capture",
            "CaptureMemberShapes",
            true,
            "Record reflected field/property/method shape metadata once per observed component type.");
        CaptureMemberValues = config.Bind(
            "Capture",
            "CaptureMemberValues",
            true,
            "Record a bounded sample of safe field/property values and Unity object references from observed component instances.");
        CaptureMemberValuesForKnownComponents = config.Bind(
            "Capture",
            "CaptureMemberValuesForKnownComponents",
            false,
            "Read member values from already-known object/component links. Off by default to avoid repeat getter work across sessions.");
        CaptureInheritedMemberShapes = config.Bind(
            "Capture",
            "CaptureInheritedMemberShapes",
            false,
            "Record reflected members declared by base classes too. Off by default because Unity and Il2CppInterop bases are noisy.");
        CaptureInteropInternalMemberShapes = config.Bind(
            "Capture",
            "CaptureInteropInternalMemberShapes",
            false,
            "Record Il2CppInterop generated Native* pointer fields/methods. Usually only useful when debugging generated wrappers.");
        CaptureUnityMemberValues = config.Bind(
            "Capture",
            "CaptureUnityMemberValues",
            false,
            "Also capture member values from UnityEngine component types. Off by default because Unity properties are numerous and often expensive.");
        MaxMemberValueComponentsPerSnapshot = config.Bind(
            "Safety",
            "MaxMemberValueComponentsPerSnapshot",
            250,
            new ConfigDescription(
                "Maximum components whose member values may be read in one snapshot.",
                new AcceptableValueRange<int>(0, 10000)));
        MaxMemberValuesPerComponent = config.Bind(
            "Safety",
            "MaxMemberValuesPerComponent",
            8,
            new ConfigDescription(
                "Maximum safe field/property values recorded per component instance.",
                new AcceptableValueRange<int>(0, 256)));
        MaxMembersPerType = config.Bind(
            "Safety",
            "MaxMembersPerType",
            512,
            new ConfigDescription(
                "Maximum reflected members recorded per observed type.",
                new AcceptableValueRange<int>(1, 4096)));
        CommandFileName = config.Bind(
            "Manual",
            "CommandFileName",
            "capture_commands.txt",
            "Command inbox file in the output directory. Supported commands: snapshot <label>, marker <label>, flush, reload_config, save_config.");
        CaptureInitialScene = config.Bind(
            "Capture",
            "CaptureInitialScene",
            true,
            "Capture the active scene shortly after plugin startup.");
        VerboseLogging = config.Bind("Diagnostics", "VerboseLogging", false, "Write extra diagnostic log lines.");
    }

    public static void Reload()
    {
        _config?.Reload();
    }

    public static void Save()
    {
        _config?.Save();
    }

    public static IReadOnlyList<EffectiveConfigRecord> GetEffectiveConfig()
    {
        var entries = new List<EffectiveConfigRecord>();
        Add(entries, Enabled);
        Add(entries, OutputDirectory);
        Add(entries, SceneSettleSeconds);
        Add(entries, EnableAutomaticSettledSnapshots);
        Add(entries, EnablePeriodicSnapshots);
        Add(entries, PeriodicSnapshotSeconds);
        Add(entries, AutoSnapshotKnownScenes);
        Add(entries, RawSessionDetailMode);
        Add(entries, MaxHierarchyDepth);
        Add(entries, MaxObjectsPerSnapshot);
        Add(entries, MaxComponentsPerObject);
        Add(entries, CaptureMemberShapes);
        Add(entries, CaptureMemberValues);
        Add(entries, CaptureMemberValuesForKnownComponents);
        Add(entries, CaptureInheritedMemberShapes);
        Add(entries, CaptureInteropInternalMemberShapes);
        Add(entries, CaptureUnityMemberValues);
        Add(entries, MaxMemberValueComponentsPerSnapshot);
        Add(entries, MaxMemberValuesPerComponent);
        Add(entries, MaxMembersPerType);
        Add(entries, CommandFileName);
        Add(entries, CaptureInitialScene);
        Add(entries, VerboseLogging);
        return entries;
    }

    private static void Add<T>(ICollection<EffectiveConfigRecord> entries, ConfigEntry<T> entry)
    {
        entries.Add(new EffectiveConfigRecord(
            entry.Definition.Section,
            entry.Definition.Key,
            entry.Value?.ToString() ?? string.Empty,
            entry.DefaultValue?.ToString() ?? string.Empty,
            entry.Description.Description ?? string.Empty,
            entry.Description.AcceptableValues?.ToString() ?? string.Empty));
    }
}

internal sealed record EffectiveConfigRecord(string Section, string Key, string Value, string DefaultValue, string Description, string AcceptableValues);
