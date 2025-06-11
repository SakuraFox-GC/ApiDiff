using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using ApiDiff.Config;

using CppAst;

namespace ApiDiff;

internal static class CppTypeExt
{

    internal static readonly SearchValues<string> KnownTypesFast;
    internal static readonly JsonConfig GlobalConfig = new() { KnownNames = [], LastBuiltInTypeName = string.Empty };

    static CppTypeExt()
    {
        var config = new FileInfo(Path.Combine(AppContext.BaseDirectory, "remapping_config.json"));
        if (config.Exists)
        {
            Log.Debug($"Remapping config found.", null, nameof(JsonSerializer));
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(config.FullName), JsonConfigContext.Default.JsonConfig) is { KnownNames.Count: > 0, LastBuiltInTypeName.Length: > 0 } valid)
                    GlobalConfig = valid;
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to deserialize remapping config.", ex, nameof(JsonSerializer));
            }
        }
        else
        {
            using var configReader = new StreamWriter(File.Open(config.FullName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
            configReader.Write(JsonSerializer.Serialize(GlobalConfig, JsonConfigContext.Default.JsonConfig).AsSpan());
        }
        KnownTypesFast = SearchValues.Create([.. GlobalConfig.KnownNames], StringComparison.Ordinal);
    }

    private static bool CompareTypeName(string left, string right)
    {
        if (GlobalConfig.KnownReservedSuffixesFast.Exists(suffix => left.EndsWith(suffix) != right.EndsWith(suffix)))
            return false;

        left = left.Replace("__Enum", string.Empty);
        right = right.Replace("__Enum", string.Empty);

        bool leftUnderscore = left.Contains('_'), rightUnderscore = right.Contains('_');
        if (leftUnderscore && !rightUnderscore)
            return left[(left.LastIndexOf('_') + 1)..] == right;

        if (rightUnderscore && !leftUnderscore)
            return right[(right.LastIndexOf('_') + 1)..] == left;

        return left == right;
    }

    private static bool CheckGeneric(string typeName)
    {
        var indexOfUnderscore = typeName.IndexOf('_');
        if (indexOfUnderscore == -1)
            return false;

        var subName = typeName[(indexOfUnderscore + 1)..];
        if (subName.IsWhiteSpace())
            return false;

        return char.IsNumber(subName[0]) && GlobalConfig.KnownReservedSuffixesFast.TrueForAll(s => !typeName.EndsWith(s));
    }

    private static string MapPrimitiveType(CppPrimitiveType primitiveType)
    {
        return primitiveType.Kind switch
        {
            CppPrimitiveKind.Void => "void",
            CppPrimitiveKind.Char => "int8_t",
            CppPrimitiveKind.Short => "int16_t",
            CppPrimitiveKind.Int => "int32_t",
            CppPrimitiveKind.Long => "int64_t",
            CppPrimitiveKind.UnsignedLong => "uint64_t",
            CppPrimitiveKind.LongLong => "int64_t",
            CppPrimitiveKind.UnsignedChar => "uint8_t",
            CppPrimitiveKind.UnsignedShort => "uint16_t",
            CppPrimitiveKind.UnsignedInt => "uint32_t",
            CppPrimitiveKind.UnsignedLongLong => "uint64_t",
            CppPrimitiveKind.Float => "float",
            CppPrimitiveKind.Double => "double",
            CppPrimitiveKind.Bool => "bool",
            _ => throw new NotImplementedException($"Unsupported primitive kind {primitiveType.Kind}")
        };
    }

    extension(CppType type)
    {

        public bool IsKnownType => KnownTypesFast.Contains(type.TypeName);

        public bool IsPointerType => type.TypeKind is CppTypeKind.Pointer;

        public bool IsPrimitiveType => type.TypeKind is CppTypeKind.Primitive;

        public bool IsTypeDef => type.TypeKind is CppTypeKind.Typedef;

        public bool IsGenericType => CheckGeneric(type.TypeName);

        public bool HasElementType => type is CppTypeWithElementType;

        public string TypeName => type is CppTypeWithElementType eType ? eType.ElementType.FullName : type.FullName;

        public bool IsSameType(string anotherTypeName)
        {
            string leftName = type.FullName, rightName = anotherTypeName;
            if (type.Parent is CppNamespace { } @namespace)
            {
                leftName = leftName.Replace($"{@namespace.Name}::", string.Empty);
            }

            return CompareTypeName(leftName, rightName);
        }

        public bool IsSameType(CppType anotherType)
        {
            if (type.Equals(anotherType))
                return true;

            if (type.TypeKind != anotherType.TypeKind)
                return false;

            if (type.IsGenericType != anotherType.IsGenericType)
                return false;

            var rightName = anotherType.FullName;
            if (anotherType.Parent is CppNamespace { } anotherNamespace)
                rightName = rightName.Replace($"{anotherNamespace.Name}::", string.Empty);

            if (GlobalConfig.RemappedTypes.TryGetValue(rightName, out string? value))
                rightName = value;

            return type.IsSameType(rightName);
        }

        public string ConstructDefinition()
        {
            if (type is CppClass @class)
                return @class.ConstructDefinition();

            if (type is CppEnum @enum)
                return @enum.ConstructDefinition();

            if (type is CppTypedef typedef)
                return typedef.ConstructDefinition();

            if (type is CppPrimitiveType primitive)
                return MapPrimitiveType(primitive);

            throw new NotImplementedException($"{nameof(ConstructDefinition)} not implemented for {type.TypeKind}.");
        }

    }

    extension(CppPointerType pointerType)
    {

        public CppType FindPointerBaseType(out int depth)
        {
            depth = 1;
            var eType = pointerType.ElementType;
            while (eType.IsPointerType)
            {
                ++depth;
                eType = ((CppPointerType)eType).ElementType;
            }

            return eType;
        }

        public string ConstructDefinition()
        {
            var @base = pointerType.FindPointerBaseType(out var depth);
            return $"{(@base is CppClass c ? c.ConstructDefinition(true) : @base.TypeName)} {new string('*', depth)}";
        }

    }

    extension(CppDeclaration declaration)
    {

        public string ConstructDefinition()
        {
            if (declaration is CppField field)
                return field.ConstructDefinition();

            if (declaration is CppEnumItem enumItem)
                return $"{enumItem.Name} = {enumItem.ValueExpression}";

            throw new NotImplementedException($"{nameof(ConstructDefinition)} not implemented for unknown declaration type.");
        }

    }

    extension(CppField field)
    {

        public string ConstructDefinition()
        {
            var builder = new StringBuilder();
            if (field.Comment is CppCommentText commentText)
                builder.Append($"/* {commentText.Text} */ ");
            if (field.Attributes is { Count: > 0 } attributes)
            {
                foreach (var attribute in attributes)
                {
                    builder.Append($"{attribute.ConstructDefinition()} ");
                }
            }

            var fieldType = field.Type;
            if (fieldType is CppClass @class)
            {
                Debug.Assert(@class.SizeOf != 0 && @class.IsDefinition);

                builder.Append($"{@class.Name} ");
            }
            else if (fieldType is CppPointerType ptrType)
            {
                var pointerType = ptrType.FindPointerBaseType(out var pointerDepth);
                if (pointerType is CppQualifiedType qualifiedType)
                    builder.Append($"{qualifiedType.Qualifier.ToString().ToLower()} ");

                if (pointerType.SizeOf == 0 && pointerType is CppClass zeroSizeClass)
                    builder.Append($"{zeroSizeClass.ClassKind.ToString().ToLower()} ");

                builder.Append($"{pointerType.TypeName} {new string('*', pointerDepth)}");
            }
            else if (fieldType is CppEnum @enum)
            {
                builder.Append($"{@enum.Name} ");
            }
            else if (fieldType is CppPrimitiveType pType)
            {
                builder.Append($"{MapPrimitiveType(pType)} ");
            }
            else if (fieldType is CppArrayType arrayType)
            {
                if (arrayType.ElementType is CppPointerType pointerType)
                {
                    builder.Append(pointerType.ConstructDefinition());
                }
                else
                {
                    builder.Append($"{(arrayType.ElementType is CppClass c ? c.ConstructDefinition(true) : arrayType.ElementType.TypeName)} ");
                }
            }
            else if (fieldType is CppTypedef typedef)
            {
                builder.Append($"{typedef.Name} ");
            }
            else if (fieldType is CppQualifiedType qualifiedType)
            {
                builder.Append($"{qualifiedType.Qualifier.ToString().ToLower()} {qualifiedType.ElementType.ConstructDefinition()}");
                return builder.ToString();
            }
            else
            {
                Debug.Assert(false);
            }
            builder.Append(field.Name);
            if (fieldType is CppArrayType arrayType2)
            {
                builder.Append($"[{arrayType2.Size}]");
            }
            else if (field.IsBitField)
            {
                builder.Append($" : {field.BitFieldWidth}");
            }
            return builder.ToString();
        }

        public bool IsSameField(CppField right) => (field.Name == right.Name) || (field.Name.Replace("_k__BackingField", string.Empty) == right.Name);

    }

    extension(CppEnumItem enumItem)
    {

        public string ConstructDefinition() => $"{enumItem.Name} = {enumItem.ValueExpression}";

    }

    extension(CppTypedef typedef)
    {

        public string ConstructDefinition() => $"typedef {typedef.ElementType.ConstructDefinition()} {typedef.Name}";

    }

    extension(CppEnum @enum)
    {

        public string ConstructDefinition()
        {
            var builder = new StringBuilder($"enum {@enum.Name}");
            if (@enum.Items.Count == 0)
                return builder.ToString();

            builder.AppendLine(" {");
            foreach (var item in @enum.Items)
            {
                builder.AppendLine($"    {item.ConstructDefinition()},");
            }
            builder.Append('}');
            return builder.ToString();
        }

    }

    extension(CppClass @class)
    {

        public string ConstructDefinition(bool declarationOnly = false)
        {
            var builder = new StringBuilder($"{@class.ClassKind.ToString().ToLower()}{(@class.Name.IsWhiteSpace() ? string.Empty : $" {@class.Name}")}");
            if (@class.SizeOf == 0 || declarationOnly)
                return builder.ToString();

            var baseTypes = @class.BaseTypes;
            if (baseTypes.Count != 0)
            {
                builder.Append(" : ");
                for (int i = baseTypes.Count - 1; i >= 0; --i)
                {
                    var typeOfBaseType = baseTypes[i].Type;
                    builder.Append($"{(typeOfBaseType is CppPrimitiveType pType ? MapPrimitiveType(pType) : typeOfBaseType.TypeName)}{(i != 0 ? ',' : string.Empty)}");
                }
            }

            builder.AppendLine(" {");
            foreach (var child in @class.Children())
            {
                if (child is CppTypeDeclaration typeDeclaration)
                {
                    builder.AppendLine($"    {typeDeclaration.ConstructDefinition()};");
                    continue;
                }
                else if (child is CppDeclaration declaration)
                {
                    builder.AppendLine($"    {declaration.ConstructDefinition()};");
                    continue;
                }

                throw new NotImplementedException($"{nameof(ConstructDefinition)} not implemented for unknown type.");
            }
            builder.Append('}');
            return builder.ToString();
        }

    }

    extension(CppAttribute attribute)
    {
        public string ConstructDefinition()
        {
            if (attribute.Name.StartsWith("alignas"))
                return "alignas(8)";

            return attribute.ToString();
        }
    }

    extension<T>(List<T> declarations) where T : CppType
    {

        public bool ContainsType(string typeName)
        {
            var target = declarations.Find(declaration => declaration.IsSameType(typeName));
            return target is not null && declarations.ContainsType(target);
        }

        public bool ContainsType(CppType cppType)
        {
            return declarations.Find(declaration => declaration.IsSameType(cppType)) is not null;
        }

        public ref T TryFindType(string typeName)
        {
            if (GlobalConfig.RemappedTypes.TryGetValue(typeName, out string? value))
                typeName = value;

            var rawData = CollectionsMarshal.AsSpan(declarations);
            for (int i = rawData.Length - 1; i >= 0; --i)
            {
                ref var target = ref rawData[i];
                if (target.IsSameType(typeName))
                    return ref target!;
            }

            return ref Unsafe.NullRef<T>();
        }

        public ref T TryFindType(CppType cppType)
        {
            var rawData = CollectionsMarshal.AsSpan(declarations);
            var index = rawData.IndexOf([cppType], new CppTypeComparer());
            if (index == -1)
                return ref Unsafe.NullRef<T>();


            return ref rawData[index];
        }

        public void SortBySourceLocation(bool throwIfSourceNotSame = true)
        {
            declarations.Sort(new CppElementDeclarationSourceComparer(throwIfSourceNotSame));
        }

    }

    private class CppElementDeclarationSourceComparer(bool ThrowIfSourceNotSame) : IComparer<CppElement>
    {
        public int Compare(CppElement? x, CppElement? y)
        {
            if (x == null || y == null)
                throw new InvalidOperationException();
            if (x.SourceFile != y.SourceFile && ThrowIfSourceNotSame)
                throw new InvalidOperationException();

            int offsetLeft = x.Span.Start.Offset, offsetRight = y.Span.Start.Offset;
            if (offsetLeft < offsetRight)
                return -1;
            if (offsetLeft > offsetRight)
                return 1;
            return 0;
        }
    }

    private class CppTypeComparer : IEqualityComparer<CppType>
    {
        public bool Equals(CppType? x, CppType? y)
        {
            if (x == null || y == null)
                throw new InvalidOperationException();

            return x.IsSameType(y);
        }

        public int GetHashCode([DisallowNull] CppType obj)
        {
            return obj.GetHashCode();
        }
    }

}
