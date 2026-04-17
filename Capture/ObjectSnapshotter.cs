using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInExposition.Config;
using BepInExposition.Model;
using BepInExposition.Persistence;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BepInExposition.Capture;

internal sealed class ObjectSnapshotter
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly object ComponentTypeIndexLock = new();
    private static readonly MethodInfo? Il2CppObjectBaseGetObjectClass = ResolveObjectClassGetter();
    private static readonly MethodInfo? Il2CppObjectGetClass = ResolveIl2CppMethod("il2cpp_object_get_class", typeof(IntPtr));
    private static readonly MethodInfo? Il2CppClassGetType = ResolveIl2CppMethod("il2cpp_class_get_type", typeof(IntPtr));
    private static readonly MethodInfo? Il2CppTypeGetFullName = ResolveIl2CppMethod("GetIl2CppTypeFullName", typeof(IntPtr));
    private static readonly MethodInfo? Il2CppClassGetName = ResolveIl2CppMethod("il2cpp_class_get_name", typeof(IntPtr));
    private static readonly MethodInfo? Il2CppClassGetNamespace = ResolveIl2CppMethod("il2cpp_class_get_namespace", typeof(IntPtr));
    private static readonly MethodInfo? Il2CppClassGetImage = ResolveIl2CppMethod("il2cpp_class_get_image", typeof(IntPtr));
    private static readonly MethodInfo? Il2CppImageGetName = ResolveIl2CppMethod("il2cpp_image_get_name", typeof(IntPtr));
    private static readonly MethodInfo? Il2CppPtrToString = ResolveIl2CppMethod("PtrToStringUTF8", typeof(IntPtr))
        ?? ResolveIl2CppMethod("PtrToStringAnsi", typeof(IntPtr));
    private static readonly MethodInfo? UnityObjectGetScriptClassName = ResolveUnityObjectMethod("GetScriptClassName");

    private readonly HashSet<string> _catalogedTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemberInfo[]> _memberValueMembersByType = new(StringComparer.Ordinal);
    private static Dictionary<string, Type>? _componentTypesByName;

    public SnapshotSummaryRecord Capture(Scene scene, string snapshotId, JsonlCaptureStore store, bool captureMemberValues)
    {
        var summary = new SnapshotSummaryRecord(snapshotId, scene.name);

        if (!scene.IsValid() || !scene.isLoaded)
        {
            summary.Warnings.Add("scene_not_valid_or_not_loaded");
            return summary;
        }

        var roots = scene.GetRootGameObjects();
        for (var i = 0; i < roots.Length; i++)
        {
            if (summary.ObjectsCaptured >= CaptureSettings.MaxObjectsPerSnapshot.Value)
            {
                summary.Truncated = true;
                break;
            }

            var root = roots[i];
            if (root == null)
            {
                continue;
            }

            CaptureTransform(root.transform, null, scene.name, snapshotId, store, summary, captureMemberValues, 0, "/" + SafeName(root.name));
        }

        return summary;
    }

    private void CaptureTransform(
        Transform transform,
        int? parentRuntimeId,
        string sceneName,
        string snapshotId,
        JsonlCaptureStore store,
        SnapshotSummaryRecord summary,
        bool captureMemberValues,
        int depth,
        string path)
    {
        if (summary.ObjectsCaptured >= CaptureSettings.MaxObjectsPerSnapshot.Value)
        {
            summary.Truncated = true;
            return;
        }

        if (depth > CaptureSettings.MaxHierarchyDepth.Value)
        {
            summary.Truncated = true;
            summary.Warnings.Add("max_hierarchy_depth_reached");
            return;
        }

        var gameObject = transform.gameObject;
        var runtimeId = SafeInstanceId(gameObject);
        var semanticKey = SemanticKey(sceneName, path);
        var objectRecord = new GameObjectRecord(
            snapshotId,
            runtimeId,
            SafePointer(gameObject),
            semanticKey,
            sceneName,
            path,
            SafeString(gameObject.name),
            SafeBool(() => gameObject.activeSelf),
            SafeBool(() => gameObject.activeInHierarchy),
            SafeTag(gameObject),
            SafeInt(() => gameObject.layer),
            parentRuntimeId,
            SafeInt(() => transform.childCount));

        store.WriteObject(objectRecord);
        summary.ObjectsCaptured++;

        CaptureComponents(gameObject, runtimeId, semanticKey, snapshotId, path, store, summary, captureMemberValues);

        var childCount = SafeInt(() => transform.childCount);
        for (var i = 0; i < childCount; i++)
        {
            if (summary.ObjectsCaptured >= CaptureSettings.MaxObjectsPerSnapshot.Value)
            {
                summary.Truncated = true;
                break;
            }

            Transform? child = null;
            try
            {
                child = transform.GetChild(i);
            }
            catch (Exception ex)
            {
                store.WriteError("get_child", ex.Message, ex.GetType().FullName, null);
            }

            if (child == null)
            {
                continue;
            }

            var childPath = path + "/" + SafeName(child.name);
            CaptureTransform(child, runtimeId, sceneName, snapshotId, store, summary, captureMemberValues, depth + 1, childPath);
        }
    }

    private void CaptureComponents(
        GameObject gameObject,
        int runtimeId,
        string objectSemanticKey,
        string snapshotId,
        string transformPath,
        JsonlCaptureStore store,
        SnapshotSummaryRecord summary,
        bool captureMemberValues)
    {
        Component[] components;
        try
        {
            components = gameObject.GetComponents<Component>();
        }
        catch (Exception ex)
        {
            store.WriteError("get_components", ex.Message, ex.GetType().FullName, null);
            return;
        }

        var maxComponents = Math.Min(components.Length, CaptureSettings.MaxComponentsPerObject.Value);
        for (var i = 0; i < maxComponents; i++)
        {
            var component = components[i];
            if (component == null)
            {
                store.WriteComponent(ComponentRecord.Missing(snapshotId, runtimeId, objectSemanticKey, transformPath, i));
                summary.ComponentsCaptured++;
                continue;
            }

            var type = component.GetType();
            var componentRuntimeId = SafeInstanceId(component);
            var typeFullName = ResolveRuntimeType(component, type, out var assemblyName, out var catalogType);

            try
            {
                CaptureTypeShape(catalogType, snapshotId, store, summary);
            }
            catch (Exception ex)
            {
                store.WriteError("capture_type_shape", ex.Message, ex.GetType().FullName, typeFullName);
            }

            var componentAdded = store.WriteComponent(new ComponentRecord(
                snapshotId,
                runtimeId,
                objectSemanticKey,
                componentRuntimeId,
                SafePointer(component),
                transformPath,
                i,
                typeFullName,
                assemblyName,
                EnabledState(component)));
            summary.ComponentsCaptured++;

            if (captureMemberValues && (componentAdded || CaptureSettings.CaptureMemberValuesForKnownComponents.Value))
            {
                try
                {
                    CaptureMemberValues(component, catalogType, snapshotId, objectSemanticKey, transformPath, i, typeFullName, assemblyName, store, summary);
                }
                catch (Exception ex)
                {
                    store.WriteError("capture_member_values", ex.Message, ex.GetType().FullName, typeFullName);
                }
            }
        }

        if (components.Length > maxComponents)
        {
            summary.ComponentListsTruncated++;
        }
    }

    private void CaptureTypeShape(Type type, string snapshotId, JsonlCaptureStore store, SnapshotSummaryRecord summary)
    {
        var typeKey = type.AssemblyQualifiedName ?? type.FullName ?? type.Name;
        if (!CaptureSettings.CaptureMemberShapes.Value || !_catalogedTypes.Add(typeKey))
        {
            return;
        }

        store.WriteTypeCatalog(new TypeCatalogRecord(
            snapshotId,
            type.FullName ?? type.Name,
            type.Assembly.GetName().Name ?? string.Empty,
            type.Namespace ?? string.Empty,
            type.BaseType?.FullName,
            type.IsPublic,
            type.IsSealed,
            type.IsAbstract,
            type.IsInterface,
            type.IsEnum,
            GetInheritanceChain(type),
            GetEnumUnderlyingType(type),
            GetEnumValues(type)));

        var written = 0;
        foreach (var current in EnumerateMemberTypes(type))
        {
            var flags = MemberFlags | BindingFlags.DeclaredOnly;
            foreach (var field in current.GetFields(flags))
            {
                if (!ShouldCaptureMember(field))
                {
                    continue;
                }

                if (written >= CaptureSettings.MaxMembersPerType.Value)
                {
                    summary.MemberListsTruncated++;
                    return;
                }

                store.WriteMemberShape(MemberShapeRecord.FromField(snapshotId, type.FullName ?? type.Name, field));
                written++;
            }

            foreach (var property in current.GetProperties(flags))
            {
                if (!ShouldCaptureMember(property))
                {
                    continue;
                }

                if (written >= CaptureSettings.MaxMembersPerType.Value)
                {
                    summary.MemberListsTruncated++;
                    return;
                }

                store.WriteMemberShape(MemberShapeRecord.FromProperty(snapshotId, type.FullName ?? type.Name, property));
                written++;
            }

            foreach (var method in current.GetMethods(flags))
            {
                if (!ShouldCaptureMember(method))
                {
                    continue;
                }

                if (written >= CaptureSettings.MaxMembersPerType.Value)
                {
                    summary.MemberListsTruncated++;
                    return;
                }

                if (method.IsSpecialName && !method.Name.StartsWith("get_", StringComparison.Ordinal) && !method.Name.StartsWith("set_", StringComparison.Ordinal))
                {
                    continue;
                }

                store.WriteMemberShape(MemberShapeRecord.FromMethod(snapshotId, type.FullName ?? type.Name, method));
                written++;
            }
        }
    }

    private void CaptureMemberValues(
        Component component,
        Type catalogType,
        string snapshotId,
        string objectSemanticKey,
        string transformPath,
        int componentIndex,
        string componentTypeFullName,
        string assemblyName,
        JsonlCaptureStore store,
        SnapshotSummaryRecord summary)
    {
        if (!CaptureSettings.CaptureMemberValues.Value)
        {
            return;
        }

        if (!CaptureSettings.CaptureUnityMemberValues.Value && IsUnityEngineType(catalogType))
        {
            return;
        }

        if (summary.MemberValueComponentsCaptured >= CaptureSettings.MaxMemberValueComponentsPerSnapshot.Value)
        {
            summary.MemberValueComponentsSkipped++;
            return;
        }

        var target = CreateTypedWrapper(component, catalogType);
        if (target == null)
        {
            return;
        }

        var members = GetMemberValueMembers(catalogType);
        var maxValues = Math.Min(members.Length, CaptureSettings.MaxMemberValuesPerComponent.Value);
        for (var i = 0; i < maxValues; i++)
        {
            if (!TryCreateMemberValueRecord(
                members[i],
                target,
                snapshotId,
                objectSemanticKey,
                transformPath,
                componentIndex,
                componentTypeFullName,
                assemblyName,
                out var record))
            {
                continue;
            }

            store.WriteMemberValue(record);
        }

        if (members.Length > maxValues)
        {
            summary.MemberValueListsTruncated++;
        }

        summary.MemberValueComponentsCaptured++;
    }

    private MemberInfo[] GetMemberValueMembers(Type catalogType)
    {
        var key = catalogType.AssemblyQualifiedName ?? catalogType.FullName ?? catalogType.Name;
        if (_memberValueMembersByType.TryGetValue(key, out var members))
        {
            return members;
        }

        var discovered = new List<MemberInfo>();
        foreach (var current in EnumerateMemberTypes(catalogType))
        {
            foreach (var field in current.GetFields(MemberFlags | BindingFlags.DeclaredOnly))
            {
                if (ShouldCaptureFieldValue(field))
                {
                    discovered.Add(field);
                }
            }

            foreach (var property in current.GetProperties(MemberFlags | BindingFlags.DeclaredOnly))
            {
                if (ShouldCapturePropertyValue(property))
                {
                    discovered.Add(property);
                }
            }
        }

        members = discovered.ToArray();
        _memberValueMembersByType[key] = members;
        return members;
    }

    private static object? CreateTypedWrapper(Component component, Type catalogType)
    {
        if (catalogType.IsInstanceOfType(component))
        {
            return component;
        }

        if (component is not Il2CppObjectBase il2CppObject || il2CppObject.Pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var constructor = catalogType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(IntPtr) }, null);
            return constructor?.Invoke(new object[] { il2CppObject.Pointer });
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldCaptureFieldValue(FieldInfo field)
    {
        return !field.IsStatic
            && ShouldCaptureMember(field)
            && IsSupportedMemberValueType(field.FieldType);
    }

    private static bool ShouldCapturePropertyValue(PropertyInfo property)
    {
        var getter = property.GetGetMethod(false);
        return getter != null
            && !getter.IsStatic
            && getter.DeclaringType != typeof(Component)
            && getter.DeclaringType != typeof(Behaviour)
            && getter.DeclaringType != typeof(MonoBehaviour)
            && getter.DeclaringType != typeof(UnityEngine.Object)
            && property.GetIndexParameters().Length == 0
            && ShouldCaptureMember(property)
            && IsSupportedMemberValueType(property.PropertyType);
    }

    private static bool IsUnityEngineType(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.StartsWith("UnityEngine.", StringComparison.Ordinal)
            || string.Equals(type.Assembly.GetName().Name, "UnityEngine.CoreModule", StringComparison.Ordinal)
            || (type.Namespace?.StartsWith("UnityEngine", StringComparison.Ordinal) ?? false);
    }

    private static bool IsSupportedMemberValueType(Type type)
    {
        return type.IsEnum
            || type == typeof(string)
            || type == typeof(bool)
            || type == typeof(char)
            || type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal)
            || type == typeof(Vector2)
            || type == typeof(Vector3)
            || type == typeof(Vector4)
            || type == typeof(Quaternion)
            || type == typeof(Color)
            || type == typeof(Color32)
            || type == typeof(Rect)
            || type == typeof(RectInt)
            || type == typeof(Bounds)
            || type == typeof(BoundsInt)
            || type == typeof(Matrix4x4)
            || type == typeof(LayerMask)
            || type == typeof(Vector2Int)
            || type == typeof(Vector3Int)
            || typeof(UnityEngine.Object).IsAssignableFrom(type);
    }

    private static bool TryCreateMemberValueRecord(
        MemberInfo member,
        object target,
        string snapshotId,
        string objectSemanticKey,
        string transformPath,
        int componentIndex,
        string componentTypeFullName,
        string assemblyName,
        out MemberValueRecord record)
    {
        record = null!;

        var declaringTypeFullName = member.DeclaringType?.FullName ?? string.Empty;
        var memberKind = member.MemberType == MemberTypes.Property ? "property" : "field";
        var valueType = member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => null
        };
        if (valueType == null)
        {
            return false;
        }

        object? value;
        try
        {
            value = member switch
            {
                FieldInfo field => field.GetValue(target),
                PropertyInfo property => property.GetValue(target),
                _ => null
            };
        }
        catch
        {
            return false;
        }

        if (!TrySerializeMemberValue(value, valueType, out var valueKind, out var serializedValue, out var referencedRuntimeId, out var referencedName, out var referencedType, out var referencedPointer))
        {
            return false;
        }

        record = new MemberValueRecord(
            snapshotId,
            objectSemanticKey,
            transformPath,
            componentIndex,
            componentTypeFullName,
            assemblyName,
            declaringTypeFullName,
            member.Name,
            memberKind,
            valueType.FullName ?? valueType.Name,
            valueKind,
            serializedValue,
            referencedRuntimeId,
            referencedName,
            referencedType,
            referencedPointer);
        return true;
    }

    private static bool TrySerializeMemberValue(
        object? value,
        Type declaredType,
        out string valueKind,
        out string serializedValue,
        out int? referencedRuntimeId,
        out string? referencedName,
        out string? referencedType,
        out string? referencedPointer)
    {
        referencedRuntimeId = null;
        referencedName = null;
        referencedType = null;
        referencedPointer = null;

        if (value == null)
        {
            valueKind = "null";
            serializedValue = "null";
            return true;
        }

        if (value is UnityEngine.Object unityObject)
        {
            valueKind = "unity_object_reference";
            serializedValue = SafeString(() => unityObject.name);
            referencedRuntimeId = SafeInstanceId(unityObject);
            referencedName = serializedValue;
            referencedType = unityObject.GetType().FullName ?? unityObject.GetType().Name;
            referencedPointer = SafePointer(unityObject);
            return true;
        }

        var valueType = value.GetType();
        if (declaredType.IsEnum || valueType.IsEnum)
        {
            valueKind = "enum";
            serializedValue = value.ToString() ?? string.Empty;
            return true;
        }

        if (value is string text)
        {
            valueKind = "string";
            serializedValue = Truncate(text, 512);
            return true;
        }

        if (value is bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            valueKind = "scalar";
            serializedValue = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return true;
        }

        if (value is Vector2 vector2)
        {
            valueKind = "vector2";
            serializedValue = FormattableString.Invariant($"{vector2.x},{vector2.y}");
            return true;
        }

        if (value is Vector3 vector3)
        {
            valueKind = "vector3";
            serializedValue = FormattableString.Invariant($"{vector3.x},{vector3.y},{vector3.z}");
            return true;
        }

        if (value is Vector4 vector4)
        {
            valueKind = "vector4";
            serializedValue = FormattableString.Invariant($"{vector4.x},{vector4.y},{vector4.z},{vector4.w}");
            return true;
        }

        if (value is Quaternion quaternion)
        {
            valueKind = "quaternion";
            serializedValue = FormattableString.Invariant($"{quaternion.x},{quaternion.y},{quaternion.z},{quaternion.w}");
            return true;
        }

        if (value is Color color)
        {
            valueKind = "color";
            serializedValue = FormattableString.Invariant($"{color.r},{color.g},{color.b},{color.a}");
            return true;
        }

        if (value is Color32 color32)
        {
            valueKind = "color32";
            serializedValue = FormattableString.Invariant($"{color32.r},{color32.g},{color32.b},{color32.a}");
            return true;
        }

        if (value is Rect rect)
        {
            valueKind = "rect";
            serializedValue = FormattableString.Invariant($"{rect.x},{rect.y},{rect.width},{rect.height}");
            return true;
        }

        if (value is RectInt rectInt)
        {
            valueKind = "rect_int";
            serializedValue = FormattableString.Invariant($"{rectInt.x},{rectInt.y},{rectInt.width},{rectInt.height}");
            return true;
        }

        if (value is Bounds bounds)
        {
            valueKind = "bounds";
            serializedValue = FormattableString.Invariant($"center={bounds.center.x},{bounds.center.y},{bounds.center.z};size={bounds.size.x},{bounds.size.y},{bounds.size.z}");
            return true;
        }

        if (value is BoundsInt boundsInt)
        {
            valueKind = "bounds_int";
            serializedValue = FormattableString.Invariant($"position={boundsInt.position.x},{boundsInt.position.y},{boundsInt.position.z};size={boundsInt.size.x},{boundsInt.size.y},{boundsInt.size.z}");
            return true;
        }

        if (value is Matrix4x4 matrix)
        {
            valueKind = "matrix4x4";
            serializedValue = FormattableString.Invariant($"{matrix.m00},{matrix.m01},{matrix.m02},{matrix.m03};{matrix.m10},{matrix.m11},{matrix.m12},{matrix.m13};{matrix.m20},{matrix.m21},{matrix.m22},{matrix.m23};{matrix.m30},{matrix.m31},{matrix.m32},{matrix.m33}");
            return true;
        }

        if (value is LayerMask layerMask)
        {
            valueKind = "layer_mask";
            serializedValue = layerMask.value.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Vector2Int vector2Int)
        {
            valueKind = "vector2_int";
            serializedValue = FormattableString.Invariant($"{vector2Int.x},{vector2Int.y}");
            return true;
        }

        if (value is Vector3Int vector3Int)
        {
            valueKind = "vector3_int";
            serializedValue = FormattableString.Invariant($"{vector3Int.x},{vector3Int.y},{vector3Int.z}");
            return true;
        }

        valueKind = string.Empty;
        serializedValue = string.Empty;
        return false;
    }

    private static IEnumerable<Type> EnumerateMemberTypes(Type type)
    {
        if (!CaptureSettings.CaptureInheritedMemberShapes.Value)
        {
            yield return type;
            yield break;
        }

        for (Type? current = type; current != null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool ShouldCaptureMember(MemberInfo member)
    {
        if (CaptureSettings.CaptureInteropInternalMemberShapes.Value)
        {
            return true;
        }

        var declaringType = member.DeclaringType?.FullName ?? string.Empty;
        if (declaringType.StartsWith("Il2CppInterop.", StringComparison.Ordinal))
        {
            return false;
        }

        var name = member.Name;
        return !string.Equals(name, "NativeClassPtr", StringComparison.Ordinal)
            && !name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal)
            && !name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)
            && !name.StartsWith("NativePropertyInfoPtr_", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> GetInheritanceChain(Type type)
    {
        var chain = new List<string>();
        for (Type? current = type; current != null; current = current.BaseType)
        {
            chain.Add(current.FullName ?? current.Name);
        }

        return chain;
    }

    private static string? GetEnumUnderlyingType(Type type)
    {
        if (!type.IsEnum)
        {
            return null;
        }

        try
        {
            var underlyingType = Enum.GetUnderlyingType(type);
            return underlyingType.FullName ?? underlyingType.Name;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<EnumValueRecord> GetEnumValues(Type type)
    {
        var values = new List<EnumValueRecord>();
        if (!type.IsEnum)
        {
            return values;
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (!field.IsLiteral)
            {
                continue;
            }

            object? raw = null;
            try
            {
                raw = field.GetRawConstantValue();
            }
            catch
            {
                // Some IL2CPP enum constants are visible but not readable through managed reflection.
            }

            values.Add(new EnumValueRecord(field.Name, raw?.ToString()));
        }

        return values;
    }

    private static string ResolveRuntimeType(Component component, Type managedType, out string assemblyName, out Type catalogType)
    {
        var managedTypeFullName = managedType.FullName ?? managedType.Name;
        assemblyName = managedType.Assembly.GetName().Name ?? string.Empty;
        catalogType = managedType;

        if (TryResolveClassNameCandidate(ReadScriptClassName(component), managedType, managedTypeFullName, out var scriptTypeFullName, out var scriptAssemblyName, out var scriptCatalogType))
        {
            catalogType = scriptCatalogType ?? managedType;
            assemblyName = scriptAssemblyName;
            return scriptTypeFullName;
        }

        if (TryResolveClassNameCandidate(ReadToStringClassName(component), managedType, managedTypeFullName, out var textTypeFullName, out var textAssemblyName, out var textCatalogType))
        {
            catalogType = textCatalogType ?? managedType;
            assemblyName = textAssemblyName;
            return textTypeFullName;
        }

        if (!TryReadNativeType(component, out var nativeTypeFullName, out var nativeAssemblyName))
        {
            return managedTypeFullName;
        }

        if (!string.IsNullOrWhiteSpace(nativeAssemblyName))
        {
            assemblyName = nativeAssemblyName;
        }

        return string.IsNullOrWhiteSpace(nativeTypeFullName) ? managedTypeFullName : nativeTypeFullName;
    }

    private static bool TryResolveClassNameCandidate(
        string className,
        Type managedType,
        string managedTypeFullName,
        out string typeFullName,
        out string assemblyName,
        out Type? catalogType)
    {
        typeFullName = string.Empty;
        assemblyName = string.Empty;
        catalogType = null;

        if (string.IsNullOrWhiteSpace(className)
            || string.Equals(className, managedType.Name, StringComparison.Ordinal)
            || string.Equals(className, managedTypeFullName, StringComparison.Ordinal))
        {
            return false;
        }

        var mappedType = ResolveLoadedComponentType(className);
        if (mappedType != null)
        {
            catalogType = mappedType;
            assemblyName = mappedType.Assembly.GetName().Name ?? string.Empty;
            typeFullName = mappedType.FullName ?? mappedType.Name;
            return true;
        }

        typeFullName = className;
        return true;
    }

    private static string ReadScriptClassName(UnityEngine.Object obj)
    {
        if (UnityObjectGetScriptClassName == null)
        {
            return string.Empty;
        }

        try
        {
            return UnityObjectGetScriptClassName.Invoke(obj, Array.Empty<object>()) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadToStringClassName(UnityEngine.Object obj)
    {
        try
        {
            var text = obj.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var open = text.LastIndexOf('(');
            var close = text.LastIndexOf(')');
            if (open < 0 || close <= open + 1)
            {
                return string.Empty;
            }

            return text.Substring(open + 1, close - open - 1).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Type? ResolveLoadedComponentType(string scriptClassName)
    {
        var lookup = GetComponentTypeIndex();
        if (lookup.TryGetValue(scriptClassName, out var exact))
        {
            return exact;
        }

        var simpleName = scriptClassName;
        var lastDot = simpleName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < simpleName.Length - 1)
        {
            simpleName = simpleName[(lastDot + 1)..];
        }

        return lookup.TryGetValue(simpleName, out var simple) ? simple : null;
    }

    private static IReadOnlyDictionary<string, Type> GetComponentTypeIndex()
    {
        if (_componentTypesByName != null)
        {
            return _componentTypesByName;
        }

        lock (ComponentTypeIndexLock)
        {
            if (_componentTypesByName != null)
            {
                return _componentTypesByName;
            }

            var index = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (!IsUsableComponentType(type))
                    {
                        continue;
                    }

                    AddTypeIndexEntry(index, type.FullName, type);
                    AddTypeIndexEntry(index, type.Name, type);
                }
            }

            _componentTypesByName = index;
            return _componentTypesByName;
        }
    }

    private static bool IsUsableComponentType(Type type)
    {
        return type != typeof(Component)
            && !type.IsAbstract
            && !type.IsInterface
            && !type.ContainsGenericParameters
            && typeof(Component).IsAssignableFrom(type);
    }

    private static void AddTypeIndexEntry(Dictionary<string, Type> index, string? key, Type type)
    {
        if (string.IsNullOrWhiteSpace(key) || index.ContainsKey(key))
        {
            return;
        }

        index[key] = type;
    }

    private static bool TryReadNativeType(UnityEngine.Object obj, out string typeFullName, out string assemblyName)
    {
        typeFullName = string.Empty;
        assemblyName = string.Empty;

        if (obj is not Il2CppObjectBase il2CppObject
            || il2CppObject.Pointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var classPointer = ReadClassPointer(il2CppObject);
            if (classPointer == IntPtr.Zero)
            {
                return false;
            }

            if (TryReadFullNameFromIl2CppType(classPointer, out var fullName))
            {
                typeFullName = fullName;
            }
            else
            {
                if (Il2CppClassGetName == null || Il2CppClassGetNamespace == null)
                {
                    return false;
                }

                var name = InvokeIl2CppString(Il2CppClassGetName, classPointer);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                var ns = InvokeIl2CppString(Il2CppClassGetNamespace, classPointer);
                typeFullName = string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
            }

            assemblyName = ReadImageAssemblyName(classPointer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr ReadClassPointer(Il2CppObjectBase il2CppObject)
    {
        if (Il2CppObjectBaseGetObjectClass != null)
        {
            var value = Il2CppObjectBaseGetObjectClass.Invoke(il2CppObject, Array.Empty<object>());
            if (value is IntPtr classPointer && classPointer != IntPtr.Zero)
            {
                return classPointer;
            }
        }

        return Il2CppObjectGetClass == null ? IntPtr.Zero : InvokeIntPtr(Il2CppObjectGetClass, il2CppObject.Pointer);
    }

    private static bool TryReadFullNameFromIl2CppType(IntPtr classPointer, out string fullName)
    {
        fullName = string.Empty;
        if (Il2CppClassGetType == null || Il2CppTypeGetFullName == null)
        {
            return false;
        }

        var typePointer = InvokeIntPtr(Il2CppClassGetType, classPointer);
        if (typePointer == IntPtr.Zero)
        {
            return false;
        }

        fullName = InvokeStringOrNativeString(Il2CppTypeGetFullName, typePointer);
        return !string.IsNullOrWhiteSpace(fullName);
    }

    private static string ReadImageAssemblyName(IntPtr classPointer)
    {
        if (Il2CppClassGetImage == null || Il2CppImageGetName == null)
        {
            return string.Empty;
        }

        var imagePointer = InvokeIntPtr(Il2CppClassGetImage, classPointer);
        var imageName = imagePointer == IntPtr.Zero ? string.Empty : InvokeIl2CppString(Il2CppImageGetName, imagePointer);
        return string.IsNullOrWhiteSpace(imageName) ? string.Empty : Path.GetFileNameWithoutExtension(imageName);
    }

    private static IntPtr InvokeIntPtr(MethodInfo method, IntPtr argument)
    {
        var value = method.Invoke(null, new object[] { argument });
        return value is IntPtr pointer ? pointer : IntPtr.Zero;
    }

    private static string InvokeIl2CppString(MethodInfo method, IntPtr argument)
    {
        var pointer = InvokeIntPtr(method, argument);
        if (pointer == IntPtr.Zero || Il2CppPtrToString == null)
        {
            return string.Empty;
        }

        return Il2CppPtrToString.Invoke(null, new object[] { pointer }) as string ?? string.Empty;
    }

    private static string InvokeStringOrNativeString(MethodInfo method, IntPtr argument)
    {
        var value = method.Invoke(null, new object[] { argument });
        if (value is string text)
        {
            return text;
        }

        if (value is not IntPtr pointer || pointer == IntPtr.Zero || Il2CppPtrToString == null)
        {
            return string.Empty;
        }

        return Il2CppPtrToString.Invoke(null, new object[] { pointer }) as string ?? string.Empty;
    }

    private static MethodInfo? ResolveObjectClassGetter()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return typeof(Il2CppObjectBase).GetProperty("ObjectClass", flags)?.GetGetMethod(true)
            ?? typeof(Il2CppObjectBase).GetMethod("get_ObjectClass", flags);
    }

    private static MethodInfo? ResolveUnityObjectMethod(string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            return typeof(UnityEngine.Object).GetMethod(name, flags, null, Type.EmptyTypes, null);
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? ResolveIl2CppMethod(string name, params Type[] parameterTypes)
    {
        try
        {
            var il2CppType = ResolveIl2CppType();
            return il2CppType?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, parameterTypes, null);
        }
        catch
        {
            return null;
        }
    }

    private static Type? ResolveIl2CppType()
    {
        var type = Type.GetType("Il2CppInterop.Runtime.IL2CPP, Il2CppInterop.Runtime", false);
        if (type != null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                type = assembly.GetType("Il2CppInterop.Runtime.IL2CPP", false);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
                // Some dynamic assemblies can refuse type lookup during early startup.
            }
        }

        return null;
    }

    private static string EnabledState(Component component)
    {
        try
        {
            if (component is Behaviour behaviour)
            {
                return behaviour.enabled ? "enabled" : "disabled";
            }

            if (component is Renderer renderer)
            {
                return renderer.enabled ? "enabled" : "disabled";
            }
        }
        catch
        {
            return "unreadable";
        }

        return "not_applicable";
    }

    private static int SafeInstanceId(UnityEngine.Object obj)
    {
        try
        {
            return obj.GetInstanceID();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafePointer(UnityEngine.Object obj)
    {
        try
        {
            // Native pointers help connect wrapper churn back to the same underlying IL2CPP object.
            if (obj is Il2CppObjectBase il2CppObject)
            {
                return il2CppObject.Pointer.ToString("X");
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static string SafeTag(GameObject gameObject)
    {
        try
        {
            return gameObject.tag;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SafeString(Func<string> read)
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

    private static string SafeString(string? value) => value ?? string.Empty;

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return false;
        }
    }

    private static int SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "<unnamed>";
        }

        return name.Replace("/", "\\/");
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static string SemanticKey(string sceneName, string transformPath)
    {
        return $"{sceneName}:{transformPath}";
    }
}
