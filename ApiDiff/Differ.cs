using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using CppAst;

namespace ApiDiff;

internal class Differ
{

    private static readonly CppCommentText UnresolvedComment = new() { Text = "Unresolved" };

    private readonly List<CppTypeDeclaration> _inputDeclarations = [], _targetDeclarations = [];
    private readonly Dictionary<string, CppType> _prebuiltTypes = [];
    private readonly HashSet<string> _walkedClasses = [];
    private readonly List<CppType> _insertedTypes = [];
    private readonly Dictionary<CppTypeDeclaration, List<CppType>> _insertionMap = [];
    private readonly CppCompilation _inputCompilation, _targetCompilation;

    public Differ(string inputHeader, string targetHeader, string includeDir)
    {
        CppParserOptions cppParserOptions = new() { TargetCpu = CppTargetCpu.ARM64, TargetSystem = "linux", ParseMacros = true };
        cppParserOptions.Defines.Add("_IDACLANG_=1");
        var sysRootInclude = new DirectoryInfo(includeDir);
        cppParserOptions.IncludeFolders.Add(new FileInfo(targetHeader).Directory!.FullName);
        cppParserOptions.SystemIncludeFolders.Add(Path.Combine(sysRootInclude.FullName, "c++", "v1"));
        cppParserOptions.SystemIncludeFolders.Add(sysRootInclude.FullName);

        _inputCompilation = TryParseHeader(File.ReadAllText(inputHeader), "input", cppParserOptions);
        var targetFileContent = File.ReadAllText(targetHeader);
        _targetCompilation = TryParseHeader(targetFileContent.Replace("#pragma once", $"#pragma once\ntypedef unsigned long size_t;"), "target", cppParserOptions);

        var appNamespace = _targetCompilation.Namespaces.FirstOrDefault(@namespace => @namespace.Name == "app");
        ArgumentNullException.ThrowIfNull(appNamespace);

        _inputDeclarations.AddRange([.. _inputCompilation.Typedefs, .. _inputCompilation.Enums, .. _inputCompilation.Classes]);
        _targetDeclarations.AddRange([.. appNamespace.Enums, .. appNamespace.Classes]);
        _inputDeclarations.SortBySourceLocation();
        _targetDeclarations.SortBySourceLocation();

        LoadPrebuiltType("Il2CppClass");
        LoadPrebuiltType("Il2CppObject");
        LoadPrebuiltType("Il2CppArray");
        LoadPrebuiltType("int32_t", false);
        LoadPrebuiltType("Action");
    }

    public string Generate()
    {
        var rawData = CollectionsMarshal.AsSpan(_targetDeclarations);
        for (int i = rawData.Length - 1; i >= 0; --i)
        {
            ref var originalType = ref rawData[i];
            Log.Debug($"Try to resolve {originalType.TypeName}");
            if (originalType is CppClass cppClass)
            {
                if (TryWalkClassFields(cppClass))
                {
                    Log.Info($"class {originalType.TypeName} resolved successfully.");
                }
            }
            else if (originalType is CppEnum)
            {
                if (TryWalkEnum(ref Unsafe.As<CppTypeDeclaration, CppEnum>(ref originalType)))
                {
                    Log.Info($"enum {originalType.TypeName} resolved successfully.");
                }
            }
        }

        foreach (ref var targetType in rawData)
        {
            ref var insertionList = ref CollectionsMarshal.GetValueRefOrNullRef(_insertionMap, targetType);
            if (Unsafe.IsNullRef(ref insertionList))
                continue;

            var removalList = new List<CppType>();
            foreach (var typeToInsert in insertionList!)
            {
                if (_insertedTypes.Any(insertedType => insertedType.IsSameType(typeToInsert)))
                {
                    removalList.Add(typeToInsert);
                    continue;
                }

                _insertedTypes.Add(typeToInsert);
            }

            foreach (var typeToRemove in removalList)
            {
                insertionList.Remove(typeToRemove);
            }
        }

        StringBuilder headerBuilder = new();
        headerBuilder.AppendLine(CONST_HEADER);
        headerBuilder.AppendLine();
        foreach (var typeDef in _targetCompilation.Typedefs)
        {
            headerBuilder.AppendLine(typeDef.FullName);
        }
        headerBuilder.AppendLine();
        foreach (var type in _targetCompilation.Classes)
        {
            headerBuilder.AppendLine(type.ToString());
        }
        headerBuilder.AppendLine();
        headerBuilder.AppendLine("namespace app {");
        foreach (var @enum in _targetDeclarations.OfType<CppEnum>())
        {
            headerBuilder.AppendLine(@enum.ToString());
            headerBuilder.AppendLine();
        }
        foreach (var insertedEnum in _insertedTypes.OfType<CppEnum>())
        {
            headerBuilder.AppendLine(insertedEnum.ToString());
            headerBuilder.AppendLine();
        }
        foreach (ref var data in rawData)
        {
            if (data is not CppClass { } @class)
                continue;

            ref var insertionList = ref CollectionsMarshal.GetValueRefOrNullRef(_insertionMap, data);
            if (!Unsafe.IsNullRef(ref insertionList))
            {
                foreach (var insertedType in insertionList)
                {
                    headerBuilder.AppendLine(insertedType.ToString());
                    headerBuilder.AppendLine();
                }
            }
            headerBuilder.AppendLine(@class.ToString());
            headerBuilder.AppendLine();
        }
        headerBuilder.AppendLine("}");

        return headerBuilder.ToString();
    }

    private static CppCompilation TryParseHeader(string content, string name, CppParserOptions parserOptions)
    {
        Log.Debug($"Compiling {name}...");
        CppCompilation compilation = CppParser.Parse(content, parserOptions, name);
        if (compilation.Diagnostics.HasErrors)
        {
            var errors = compilation.Diagnostics.Messages.Where(message => message.Type == CppLogMessageType.Error);
            Log.Error($"Compilation ended with {errors.Count()} errors generated.");
            foreach (var error in errors)
            {
                Log.Error(error.ToString());
            }
        }
        else
        {
            Log.Info($"Compilation ended with {compilation.Diagnostics.Messages.Count} diagnostics generated.");
        }

        Log.Debug($"{compilation.Children().Count()} nodes found in {name}");
        return compilation;
    }

    private CppTypeDeclaration LoadPrebuiltType(string typeName, bool loadPointer = true)
    {
        ref var requiredType = ref _inputDeclarations.TryFindType(typeName);
        if (Unsafe.IsNullRef(ref requiredType))
            throw new InvalidOperationException($"Can not load critical cpp type {typeName}");

        _prebuiltTypes.Add(typeName, requiredType);
        if (loadPointer)
            _prebuiltTypes.Add($"{typeName}*", new CppPointerType(requiredType));
        return requiredType;
    }

    private unsafe bool TryWalkClassFields(CppClass targetClass)
    {
        ref CppTypeDeclaration inputType = ref _inputDeclarations.TryFindType(targetClass);
        if (Unsafe.IsNullRef(ref inputType))
            return false;

        if (inputType is not CppClass { } inputClass)
            throw new InvalidOperationException("input declaration is not a class");

        if (targetClass.SizeOf == 0)
            return true;

        using var pinnedGCHandleForTargetContainer = new PinnedGCHandle<CppContainerList<CppField>>(targetClass.Fields);
        ref var targetFields = ref Unsafe.AsRef<List<CppField>>(pinnedGCHandleForTargetContainer.GetAddressOfObjectData());
        CppContainerList<CppField> inputContainer = inputClass.Fields;
        List<CppField> rebuiltFields = [];
        if (inputContainer.Count == targetFields.Count)
        {
            goto COMPARE_SAME_LENGTH;
        }

        var lastFieldInTarget = targetFields[^1];
        var targetBaseFields = new List<CppField>();
        foreach (var targetBaseType in targetClass.BaseTypes)
        {
            targetBaseFields.AddRange(((CppClass)targetBaseType.Type).Fields);
        }
        foreach (CppField inputField in inputContainer)
        {
            if (targetBaseFields.Find(targetBaseField => targetBaseField.Name == inputField.Name) is CppField { })
                continue;

            if (targetFields.Find(targetField => targetField.Name == inputField.Name) is CppField { } matchedField)
            {
                if (inputField.Type.TypeKind == matchedField.Type.TypeKind)
                    inputField.Type = matchedField.Type;
                rebuiltFields.Add(inputField);

                if (lastFieldInTarget.Name == inputField.Name)
                    break;

                continue;
            }

            if (!TryAddType(targetClass, inputField.Type))
                inputField.Comment = UnresolvedComment;

            rebuiltFields.Add(inputField);
        }

        goto MODIFY_AND_RETURN;

    COMPARE_SAME_LENGTH:
        for (int i = 0; i < inputContainer.Count; i++)
        {
            CppField inputField = inputContainer[i], targetField = targetFields[i];
            CppType inputFieldType = inputField.Type, targetFieldType = targetField.Type;
            if (inputFieldType.IsKnownType && targetFieldType.IsKnownType)
            {
                rebuiltFields.Add(inputField);
                continue;
            }
            else if (inputFieldType.TypeKind == targetFieldType.TypeKind)
            {
                if (inputFieldType.IsSameType(targetFieldType))
                {
                    rebuiltFields.Add(inputField);
                }
                else if (inputFieldType.IsPointerType && (inputField.Name == targetField.Name) && targetFieldType.IsKnownType)
                {
                    rebuiltFields.Add(targetField);
                }
                continue;
            }
            else if (inputFieldType.SizeOf == targetFieldType.SizeOf)
            {
                rebuiltFields.Add(targetField);
                continue;
            }

            if (!TryAddType(targetClass, inputField.Type))
                inputField.Comment = UnresolvedComment;

            rebuiltFields.Add(inputField);
        }
    MODIFY_AND_RETURN:
        targetFields = rebuiltFields;
        return true;
    }

    private bool TryWalkEnum(ref CppEnum targetEnum)
    {
        ref CppTypeDeclaration inputType = ref _inputDeclarations.TryFindType(targetEnum);
        if (Unsafe.IsNullRef(ref inputType))
            return false;

        if (inputType is not CppEnum { } inputEnum)
            throw new InvalidOperationException("input declaration is not a enum");

        var parent = targetEnum.Parent;
        targetEnum = ref Unsafe.As<CppTypeDeclaration, CppEnum>(ref inputType);
        SetElementParent(targetEnum, parent);

        return true;
    }

    private bool TryAddType(CppTypeDeclaration @base, CppType type)
    {
        if (type.IsKnownType || type.IsPrimitiveType || type.IsTypeDef || type.TypeName.EndsWith("__Class"))
            return true;    // pretend that we just insert these types.

        ref var types = ref CollectionsMarshal.GetValueRefOrAddDefault(_insertionMap, @base, out var exists);
        if (!exists)
            types = [];

        types!.Insert(0, type);

        return TryWalkTypeHierarchy(ref type);
    }

    private bool TryWalkTypeHierarchy(ref CppType type)
    {
        if (_walkedClasses.Contains(type.TypeName))
            return true;

        if (type.IsKnownType || type.IsPrimitiveType || type.IsTypeDef)
            return true;

        var typeName = type.TypeName;
        var isGeneric = type.IsGenericType;
        if (typeName.EndsWith("__Class"))
        {
            Log.Debug($"{type.TypeName} => Il2CppClass");
            type = _prebuiltTypes["Il2CppClass*"];
            return true;
        }
        else if (!isGeneric && typeName.EndsWith("__Array"))
        {
            Log.Debug($"{type.TypeName} => Il2CppArray");
            type = _prebuiltTypes["Il2CppArray*"];
            return true;
        }
        else if (isGeneric && (typeName.StartsWith("Action_") || typeName.StartsWith("Func_")))
        {
            Log.Debug($"{type.TypeName} => Action");
            type = _prebuiltTypes["Action*"];
            return true;
        }

        if (type.HasElementType)
        {
            var eType = ((CppTypeWithElementType)type).ElementType;
            return TryWalkTypeHierarchy(ref eType);
        }

        _walkedClasses.Add(type.TypeName);
        if (type is CppClass cppClass)
        {
            foreach (var field in cppClass.Fields)
            {
                CppType fieldType = field.Type!;

                if (!fieldType.IsPointerType)
                {
                    if (!TryWalkTypeHierarchy(ref fieldType))
                        field.Comment = UnresolvedComment;
                    continue;
                }

                typeName = fieldType.TypeName;
                isGeneric = fieldType.IsGenericType;
                ref CppTypeDeclaration resolvedTargetType = ref _targetDeclarations.TryFindType(typeName);
                bool resolvedInMap = false;
                foreach (var (_, insertionList) in _insertionMap)
                {
                    ref var insertedMatched = ref insertionList.TryFindType(typeName);
                    if (!Unsafe.IsNullRef(ref insertedMatched))
                    {
                        resolvedInMap = true;
                        break;
                    }
                }
                if (Unsafe.IsNullRef(ref resolvedTargetType) && !resolvedInMap)
                {
                    var previousType = field.Type;
                    if (typeName.EndsWith("__Class"))
                    {
                        field.Type = _prebuiltTypes["Il2CppClass*"];
                    }
                    else if (!isGeneric && typeName.EndsWith("__Array"))
                    {
                        field.Type = _prebuiltTypes["Il2CppArray*"];
                    }
                    else if (!CppTypeExt.KnownTypes.Contains(typeName))
                    {
                        field.Type = _prebuiltTypes["Il2CppObject*"];
                    }
                    else if (isGeneric && (typeName.StartsWith("Action_") || typeName.StartsWith("Func_")))
                    {
                        field.Type = _prebuiltTypes["Action*"];
                    }

                    if (!previousType.IsSameType(field.Type))
                        Log.Debug($"{typeName} => {field.Type.TypeName}");
                }
            }
        }
        else if (type is CppEnum cppEnum)
        {
            ref CppTypeDeclaration resolvedEnumType = ref _targetDeclarations.TryFindType(cppEnum.FullName);
            if (Unsafe.IsNullRef(ref resolvedEnumType))
                type = LoadPrebuiltType("int32_t");
        }

        return true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Parent")]
    private static extern void SetElementParent(CppElement @this, ICppContainer parent);

    const string CONST_HEADER = @"#pragma once

#if defined(__i386__) || defined(__arm__)
#define IS_32BIT
#endif

#ifndef DO_ARRAY_DEFINE
#define DO_ARRAY_DEFINE(E_NAME) \
struct  E_NAME ## __Array { \
Il2CppClass *klass; \
MonitorData *monitor; \
Il2CppArrayBounds *bounds; \
il2cpp_array_size_t max_length; \
E_NAME vector[32]; \
};
#endif
#ifndef DO_LIST_DEFINE
#define DO_LIST_DEFINE(E_NAME) \
DO_ARRAY_DEFINE(E_NAME) \
struct List_1_ ## E_NAME { \
Il2CppClass *klass; \
MonitorData *monitor; \
struct E_NAME ## __Array *_items; \
int32_t _size; \
int32_t _version; \
};
#endif

#include <cstdint>
#include ""il2cpp-class.h""";

}
