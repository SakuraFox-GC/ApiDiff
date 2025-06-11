using System.Runtime.InteropServices;

namespace ApiDiff.Config;

internal record JsonConfig
{

    public required List<string> KnownNames { get; init; } = [];

    public required string LastBuiltInTypeName { get; init; }

    public Dictionary<string, string> KnownReservedSuffixes { get; init { KnownReservedSuffixesFast.AddRange(value.Keys); } } = [];

    public Dictionary<string, string> RemappedTypes { get; init; } = [];

    internal List<string> KnownReservedSuffixesFast = [];

    public Span<string> GetBuiltInTypes()
    {
        var data = CollectionsMarshal.AsSpan(KnownNames);

        return data[..(KnownNames.IndexOf(LastBuiltInTypeName) + 1)];
    }

}
