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
    private static readonly CppTypeComparer DefaultTypeComparer = new(), RelaxedTypeComparer = new() { RelaxedComparison = true };

    static CppTypeExt()
    {
        var config = new FileInfo(Path.Combine(AppContext.BaseDirectory, "remapping_config.json"));
        if (config.Exists)
        {
            Log.Debug($"Remapping config found.", null, nameof(JsonSerializer));
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(config.FullName), JsonConfigContext.Default.JsonConfig) is { KnownNames.Count: > 0, LastBuiltInTypeName.Length: > 0 } valid)
                {
                    GlobalConfig = valid;
                }
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

    private static bool CompareTypeName(string left, string right, bool relax)
    {
        left = CanonicalizeForCompare(left);
        right = CanonicalizeForCompare(right);
        if (GlobalConfig.KnownReservedSuffixesFast.Exists(suffix => left.EndsWith(suffix) != right.EndsWith(suffix)))
        {
            return false;
        }

        left = left.Replace("__Enum", string.Empty);
        right = right.Replace("__Enum", string.Empty);

        int leftUnderscore = left.Count('_'), rightUnderscore = right.Count('_');
        if (relax && (leftUnderscore > 0 && rightUnderscore == 0))
        {
            return left[(left.LastIndexOf('_') + 1)..] == right;
        }

        if (relax && (rightUnderscore > 0 && leftUnderscore == 0))
        {
            return right[(right.LastIndexOf('_') + 1)..] == left;
        }

        return left == right;
    }

    // Applies RemappedTypes to the base token while tolerating a trailing pointer
    // decoration (e.g. "List_1_..._ *"), so a configured equivalence closes the
    // comparison on pointer FullNames as well as bare class names.
    private static string CanonicalizeForCompare(string name)
    {
        int end = name.Length;
        while (end > 0 && (name[end - 1] == '*' || name[end - 1] == ' '))
        {
            end--;
        }

        return end == name.Length
            ? RemapLookupTypeName(name)
            : RemapLookupTypeName(name[..end]) + name[end..];
    }

    // Relaxed/exact comparison of two raw type-name strings, reusing CompareTypeName so
    // namespace-qualified Inspector names match Core's short names (used for base-class
    // resolution in the hierarchy walk).
    internal static bool IsSameTypeName(string left, string right, bool relax = false)
    {
        return CompareTypeName(left, right, relax);
    }

    internal static string RemapLookupTypeName(string typeName)
    {
        return GlobalConfig.RemappedTypes.TryGetValue(typeName, out string? remappedName)
            ? remappedName
            : typeName;
    }

    internal static string GetLookupKey(string typeName, bool applyRemapping = true)
    {
        if (applyRemapping)
        {
            typeName = RemapLookupTypeName(typeName);
        }

        return typeName.Replace("__Enum", string.Empty);
    }

    internal static string GetLookupKey(CppType type, bool applyRemapping = false)
    {
        return GetLookupKey(GetComparableTypeName(type), applyRemapping);
    }

    internal static string GetRelaxedLookupKey(string typeName, bool applyRemapping = true)
    {
        if (applyRemapping)
        {
            typeName = RemapLookupTypeName(typeName);
        }

        var key = new StringBuilder(GlobalConfig.KnownReservedSuffixesFast.Count + typeName.Length + 1);
        foreach (string suffix in GlobalConfig.KnownReservedSuffixesFast)
        {
            key.Append(typeName.EndsWith(suffix) ? '1' : '0');
        }

        typeName = typeName.Replace("__Enum", string.Empty);
        int lastUnderscore = typeName.LastIndexOf('_');
        key.Append(':');
        key.Append(lastUnderscore < 0 ? typeName : typeName[(lastUnderscore + 1)..]);
        return key.ToString();
    }

    internal static string GetRelaxedLookupKey(CppType type, bool applyRemapping = false)
    {
        return GetRelaxedLookupKey(GetComparableTypeName(type), applyRemapping);
    }

    private static string GetComparableTypeName(CppType type)
    {
        string typeName = type.FullName;
        if (type.Parent is CppNamespace { } @namespace)
        {
            typeName = typeName.Replace($"{@namespace.Name}::", string.Empty);
        }

        return typeName;
    }

    private static bool CheckGeneric(string typeName)
    {
        int indexOfUnderscore = typeName.IndexOf('_');
        if (indexOfUnderscore == -1)
        {
            return false;
        }

        string subName = typeName[(indexOfUnderscore + 1)..];
        if (subName.IsWhiteSpace())
        {
            return false;
        }

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

        public bool IsSameType(string anotherTypeName, bool relax = false)
        {
            string leftName = type.FullName, rightName = anotherTypeName;
            if (type.Parent is CppNamespace { } @namespace)
            {
                leftName = leftName.Replace($"{@namespace.Name}::", string.Empty);
            }

            return CompareTypeName(leftName, rightName, relax);
        }

        public bool IsSameType(CppType anotherType, bool relax = false)
        {
            if (type.Equals(anotherType))
            {
                return true;
            }

            if (type.TypeKind != anotherType.TypeKind)
            {
                return false;
            }

            if (type.IsGenericType != anotherType.IsGenericType)
            {
                return false;
            }

            string rightName = anotherType.FullName;
            if (anotherType.Parent is CppNamespace { } anotherNamespace)
            {
                rightName = rightName.Replace($"{anotherNamespace.Name}::", string.Empty);
            }

            if (GlobalConfig.RemappedTypes.TryGetValue(rightName, out string? value))
            {
                rightName = value;
            }

            return type.IsSameType(rightName, relax);
        }

        public string ConstructDefinition()
        {
            if (type is CppClass @class)
            {
                return @class.ConstructDefinition();
            }

            if (type is CppEnum @enum)
            {
                return @enum.ConstructDefinition();
            }

            if (type is CppTypedef typedef)
            {
                return typedef.ConstructDefinition();
            }

            if (type is CppPrimitiveType primitive)
            {
                return MapPrimitiveType(primitive);
            }

            throw new NotImplementedException($"{nameof(ConstructDefinition)} not implemented for {type.TypeKind}.");
        }

    }

    extension(CppPointerType pointerType)
    {

        public CppType FindPointerBaseType(out int depth)
        {
            depth = 1;
            CppType eType = pointerType.ElementType;
            while (eType.IsPointerType)
            {
                ++depth;
                eType = ((CppPointerType)eType).ElementType;
            }

            return eType;
        }

        public string ConstructDefinition()
        {
            CppType @base = pointerType.FindPointerBaseType(out int depth);
            return $"{(@base is CppClass c ? c.ConstructDefinition(true) : @base.TypeName)} {new string('*', depth)}";
        }

    }

    extension(CppDeclaration declaration)
    {

        public string ConstructDefinition()
        {
            if (declaration is CppField field)
            {
                return field.ConstructDefinition();
            }

            if (declaration is CppEnumItem enumItem)
            {
                return $"{enumItem.Name} = {enumItem.ValueExpression}";
            }

            throw new NotImplementedException($"{nameof(ConstructDefinition)} not implemented for unknown declaration type.");
        }

    }

    extension(CppField field)
    {

        public string ConstructDefinition()
        {
            var builder = new StringBuilder();
            if (field.Comment is CppCommentText commentText)
            {
                builder.Append($"/* {commentText.Text} */ ");
            }

            if (field.Attributes is { Count: > 0 } attributes)
            {
                foreach (CppAttribute? attribute in attributes)
                {
                    builder.Append($"{attribute.ConstructDefinition()} ");
                }
            }

            CppType fieldType = field.Type;
            if (fieldType is CppClass @class)
            {
                Debug.Assert(@class.SizeOf != 0 && @class.IsDefinition);

                builder.Append($"{@class.Name} ");
            }
            else if (fieldType is CppPointerType ptrType)
            {
                CppType pointerType = ptrType.FindPointerBaseType(out int pointerDepth);
                if (pointerType is CppQualifiedType qualifiedType)
                {
                    builder.Append($"{qualifiedType.Qualifier.ToString().ToLower()} ");
                }

                if (pointerType.SizeOf == 0 && pointerType is CppClass zeroSizeClass)
                {
                    builder.Append($"{zeroSizeClass.ClassKind.ToString().ToLower()} ");
                }

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

        public bool IsSameField(CppField right)
        {
            return field.Name == right.Name;
        }
    }

    extension(CppEnumItem enumItem)
    {

        public string ConstructDefinition()
        {
            return $"{enumItem.Name} = {enumItem.ValueExpression}";
        }
    }

    extension(CppTypedef typedef)
    {

        public string ConstructDefinition()
        {
            return $"typedef {typedef.ElementType.ConstructDefinition()} {typedef.Name}";
        }
    }

    extension(CppEnum @enum)
    {

        public string ConstructDefinition()
        {
            var builder = new StringBuilder($"enum {@enum.Name}");
            if (@enum.Items.Count == 0)
            {
                string integerType = @enum.IntegerType switch
                {
                    CppPrimitiveType primitive => MapPrimitiveType(primitive),
                    CppTypedef typedef => typedef.Name,
                    { } type => type.TypeName,
                    null => "int32_t"
                };
                builder.Append($" : {integerType}");
                return builder.ToString();
            }

            builder.AppendLine(" {");
            foreach (CppEnumItem? item in @enum.Items)
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
            {
                return builder.ToString();
            }

            List<CppBaseType> baseTypes = @class.BaseTypes;
            if (baseTypes.Count != 0)
            {
                builder.Append(" : ");
                for (int i = baseTypes.Count - 1; i >= 0; --i)
                {
                    CppType typeOfBaseType = baseTypes[i].Type;
                    builder.Append($"{(typeOfBaseType is CppPrimitiveType pType ? MapPrimitiveType(pType) : typeOfBaseType.TypeName)}{(i != 0 ? ',' : string.Empty)}");
                }
            }

            builder.AppendLine(" {");
            foreach (ICppDeclaration? child in @class.Children())
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
            {
                return "alignas(8)";
            }

            return attribute.ToString();
        }
    }

    extension<T>(List<T> declarations) where T : CppType
    {

        public bool ContainsType(string typeName)
        {
            T? target = declarations.Find(declaration => declaration.IsSameType(typeName));
            return target is not null && declarations.ContainsType(target);
        }

        public bool ContainsType(CppType cppType)
        {
            return declarations.Find(declaration => declaration.IsSameType(cppType)) is not null;
        }

        public ref T TryFindType(string typeName, bool relax = false)
        {
            if (GlobalConfig.RemappedTypes.TryGetValue(typeName, out string? value))
            {
                typeName = value;
            }

            Span<T> rawData = CollectionsMarshal.AsSpan(declarations);
            for (int i = rawData.Length - 1; i >= 0; --i)
            {
                ref T target = ref rawData[i];
                if (target.IsSameType(typeName, relax))
                {
                    return ref target!;
                }
            }

            return ref Unsafe.NullRef<T>();
        }

        public ref T TryFindType(CppType cppType, bool relax = false)
        {
            Span<T> rawData = CollectionsMarshal.AsSpan(declarations);
            int index = rawData.IndexOf([cppType], relax ? RelaxedTypeComparer : DefaultTypeComparer);
            if (index == -1)
            {
                return ref Unsafe.NullRef<T>();
            }

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
            {
                throw new InvalidOperationException();
            }

            if (x.SourceFile != y.SourceFile && ThrowIfSourceNotSame)
            {
                throw new InvalidOperationException();
            }

            int offsetLeft = x.Span.Start.Offset, offsetRight = y.Span.Start.Offset;
            if (offsetLeft < offsetRight)
            {
                return -1;
            }

            if (offsetLeft > offsetRight)
            {
                return 1;
            }

            return 0;
        }
    }

    private class CppTypeComparer : IEqualityComparer<CppType>
    {

        public bool RelaxedComparison { get; init; } = false;

        public bool Equals(CppType? x, CppType? y)
        {
            if (x == null || y == null)
            {
                throw new InvalidOperationException();
            }

            return x.IsSameType(y, RelaxedComparison);
        }

        public int GetHashCode([DisallowNull] CppType obj)
        {
            return obj.GetHashCode();
        }
    }

}
