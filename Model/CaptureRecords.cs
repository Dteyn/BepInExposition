using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BepInExposition.Model;

internal sealed record SessionStartRecord(
    string SessionId,
    string StartedAtUtc,
    string PluginVersion,
    string GameVersion,
    string UnityVersion,
    string Platform);

internal sealed record SessionEndRecord(string SessionId, string EndedAtUtc, string Reason);

internal sealed record SceneRecord(
    string EventKind,
    string SceneName,
    string ScenePath,
    int BuildIndex,
    bool IsLoaded,
    string? PreviousSceneName)
{
    public static SceneRecord FromScene(string eventKind, Scene scene, string? previousSceneName = null)
    {
        return new SceneRecord(eventKind, scene.name ?? string.Empty, scene.path ?? string.Empty, scene.buildIndex, scene.isLoaded, previousSceneName);
    }
}

internal sealed record SnapshotRecord(string SnapshotId, string SnapshotKind, string SceneName, string? MarkerLabel);

internal sealed record GameObjectRecord(
    string SnapshotId,
    int RuntimeId,
    string NativePointer,
    string SemanticKey,
    string SceneName,
    string TransformPath,
    string ObjectName,
    bool ActiveSelf,
    bool ActiveInHierarchy,
    string Tag,
    int Layer,
    int? ParentRuntimeId,
    int ChildCount);

internal sealed record ComponentRecord(
    string SnapshotId,
    int ObjectRuntimeId,
    string ObjectSemanticKey,
    int ComponentRuntimeId,
    string NativePointer,
    string TransformPath,
    int ComponentIndex,
    string TypeFullName,
    string AssemblyName,
    string EnabledState)
{
    public static ComponentRecord Missing(string snapshotId, int objectRuntimeId, string objectSemanticKey, string transformPath, int componentIndex)
    {
        return new ComponentRecord(snapshotId, objectRuntimeId, objectSemanticKey, 0, string.Empty, transformPath, componentIndex, "<missing>", string.Empty, "missing");
    }
}

internal sealed record TypeCatalogRecord(
    string SnapshotId,
    string TypeFullName,
    string AssemblyName,
    string Namespace,
    string? BaseTypeFullName,
    bool IsPublic,
    bool IsSealed,
    bool IsAbstract,
    bool IsInterface,
    bool IsEnum,
    IReadOnlyList<string> InheritanceChain,
    string? EnumUnderlyingType,
    IReadOnlyList<EnumValueRecord> EnumValues);

internal sealed record EnumValueRecord(string Name, string? Value);

internal sealed record MemberParameterRecord(string Name, string TypeFullName, bool IsOut, bool IsRef, bool IsOptional);

internal sealed record MemberShapeRecord(
    string SnapshotId,
    string ObservedTypeFullName,
    string DeclaringTypeFullName,
    string MemberName,
    string MemberKind,
    string ValueTypeFullName,
    bool IsStatic,
    bool IsPublic,
    bool CanRead,
    bool CanWrite,
    int ParameterCount,
    string ParameterTypes,
    string? Accessibility,
    string? GetterAccessibility,
    string? SetterAccessibility,
    bool IsInitOnly,
    bool IsBackingField,
    bool IsVirtual,
    bool IsAbstract,
    bool IsConstructor,
    IReadOnlyList<MemberParameterRecord> Parameters)
{
    public static MemberShapeRecord FromField(string snapshotId, string observedTypeFullName, FieldInfo field)
    {
        return new MemberShapeRecord(
            snapshotId,
            observedTypeFullName,
            field.DeclaringType?.FullName ?? string.Empty,
            field.Name,
            "field",
            field.FieldType.FullName ?? field.FieldType.Name,
            field.IsStatic,
            field.IsPublic,
            true,
            !field.IsInitOnly,
            0,
            string.Empty,
            GetFieldAccessibility(field),
            null,
            null,
            field.IsInitOnly,
            IsCompilerBackingField(field),
            false,
            false,
            false,
            Array.Empty<MemberParameterRecord>());
    }

    public static MemberShapeRecord FromProperty(string snapshotId, string observedTypeFullName, PropertyInfo property)
    {
        var getter = property.GetGetMethod(true);
        var setter = property.GetSetMethod(true);
        var indexParameters = property.GetIndexParameters();
        return new MemberShapeRecord(
            snapshotId,
            observedTypeFullName,
            property.DeclaringType?.FullName ?? string.Empty,
            property.Name,
            "property",
            property.PropertyType.FullName ?? property.PropertyType.Name,
            (getter?.IsStatic ?? false) || (setter?.IsStatic ?? false),
            (getter?.IsPublic ?? false) || (setter?.IsPublic ?? false),
            getter != null,
            setter != null,
            indexParameters.Length,
            string.Join(",", indexParameters.Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)),
            null,
            getter != null ? GetMethodAccessibility(getter) : null,
            setter != null ? GetMethodAccessibility(setter) : null,
            false,
            false,
            false,
            false,
            false,
            indexParameters.Select(ToParameterRecord).ToArray());
    }

    public static MemberShapeRecord FromMethod(string snapshotId, string observedTypeFullName, MethodInfo method)
    {
        var parameters = method.GetParameters();
        return new MemberShapeRecord(
            snapshotId,
            observedTypeFullName,
            method.DeclaringType?.FullName ?? string.Empty,
            method.Name,
            "method",
            method.ReturnType.FullName ?? method.ReturnType.Name,
            method.IsStatic,
            method.IsPublic,
            true,
            false,
            parameters.Length,
            string.Join(",", parameters.Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)),
            GetMethodAccessibility(method),
            null,
            null,
            false,
            false,
            method.IsVirtual,
            method.IsAbstract,
            method.IsConstructor,
            parameters.Select(ToParameterRecord).ToArray());
    }

    private static MemberParameterRecord ToParameterRecord(ParameterInfo parameter)
    {
        return new MemberParameterRecord(
            parameter.Name ?? string.Empty,
            parameter.ParameterType.FullName ?? parameter.ParameterType.Name,
            parameter.IsOut,
            parameter.ParameterType.IsByRef && !parameter.IsOut,
            parameter.IsOptional);
    }

    private static bool IsCompilerBackingField(FieldInfo field)
    {
        return field.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
            || field.Name.Contains("k__BackingField");
    }

    private static string GetFieldAccessibility(FieldInfo field)
    {
        if (field.IsPublic)
        {
            return "public";
        }

        if (field.IsPrivate)
        {
            return "private";
        }

        if (field.IsFamily)
        {
            return "protected";
        }

        if (field.IsAssembly)
        {
            return "internal";
        }

        if (field.IsFamilyOrAssembly)
        {
            return "protected internal";
        }

        if (field.IsFamilyAndAssembly)
        {
            return "private protected";
        }

        return "unknown";
    }

    private static string GetMethodAccessibility(MethodBase method)
    {
        if (method.IsPublic)
        {
            return "public";
        }

        if (method.IsPrivate)
        {
            return "private";
        }

        if (method.IsFamily)
        {
            return "protected";
        }

        if (method.IsAssembly)
        {
            return "internal";
        }

        if (method.IsFamilyOrAssembly)
        {
            return "protected internal";
        }

        if (method.IsFamilyAndAssembly)
        {
            return "private protected";
        }

        return "unknown";
    }
}

internal sealed record MemberValueRecord(
    string SnapshotId,
    string ObjectSemanticKey,
    string TransformPath,
    int ComponentIndex,
    string ComponentTypeFullName,
    string AssemblyName,
    string DeclaringTypeFullName,
    string MemberName,
    string MemberKind,
    string ValueTypeFullName,
    string ValueKind,
    string SerializedValue,
    int? ReferencedObjectRuntimeId,
    string? ReferencedObjectName,
    string? ReferencedObjectTypeFullName,
    string? ReferencedObjectNativePointer);

internal sealed record MarkerRecord(string Label, string Source);

internal sealed record ErrorRecord(string Stage, string Message, string? TypeName, string? StackHint);

internal sealed record KnownDatabaseFileStats(string Name, string RelativePath, int RecordCount, int AddedThisRun);

// Session stats feed both the end-of-run report and DEBUG log summaries. They intentionally count
// observations separately from newly added known records so repeated sessions still show activity.
internal sealed class SessionCaptureStats
{
    public SessionCaptureStats(string sessionId, string startedAtUtc, string outputRoot, string sessionFilePath)
    {
        SessionId = sessionId;
        StartedAtUtc = startedAtUtc;
        OutputRoot = outputRoot;
        SessionFilePath = sessionFilePath;
    }

    public string SessionId { get; }
    public string StartedAtUtc { get; }
    public string? EndedAtUtc { get; set; }
    public string OutputRoot { get; }
    public string SessionFilePath { get; }
    public string RawSessionDetailMode { get; set; } = "NewKnownRecords";
    public string? EnvironmentFilePath { get; set; }
    public string? ReportFilePath { get; set; }
    public string? ManifestFilePath { get; set; }
    public int ScenesObserved { get; set; }
    public int NewScenes { get; set; }
    public int SnapshotsWritten { get; set; }
    public int ObjectsObserved { get; set; }
    public int NewObjects { get; set; }
    public int ComponentsObserved { get; set; }
    public int NewObjectComponents { get; set; }
    public int NewComponentTypes { get; set; }
    public int TypesObserved { get; set; }
    public int NewTypes { get; set; }
    public int MemberShapesObserved { get; set; }
    public int NewMemberShapes { get; set; }
    public int MemberValuesObserved { get; set; }
    public int NewMemberValues { get; set; }
    public int MarkersWritten { get; set; }
    public int ErrorsWritten { get; set; }
    public List<string> NewSceneSamples { get; } = new();
    public List<string> NewObjectSamples { get; } = new();
    public List<string> NewComponentTypeSamples { get; } = new();
    public List<string> NewMemberShapeSamples { get; } = new();
    public List<string> NewMemberValueSamples { get; } = new();
}

internal sealed class SnapshotSummaryRecord
{
    public SnapshotSummaryRecord(string snapshotId, string sceneName)
    {
        SnapshotId = snapshotId;
        SceneName = sceneName;
    }

    public string SnapshotId { get; }
    public string SceneName { get; }
    public int ObjectsCaptured { get; set; }
    public int ComponentsCaptured { get; set; }
    public int NewObjects { get; set; }
    public int NewObjectComponents { get; set; }
    public int NewComponentTypes { get; set; }
    public int NewTypes { get; set; }
    public int NewMemberShapes { get; set; }
    public int NewMemberValues { get; set; }
    public int ComponentListsTruncated { get; set; }
    public int MemberListsTruncated { get; set; }
    public int MemberValueListsTruncated { get; set; }
    public int MemberValueComponentsCaptured { get; set; }
    public int MemberValueComponentsSkipped { get; set; }
    public bool Truncated { get; set; }
    public List<string> Warnings { get; } = new();
}
