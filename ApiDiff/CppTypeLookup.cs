using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using CppAst;

namespace ApiDiff;

internal sealed class CppTypeLookup<T> where T : CppType
{
    private readonly List<T> _declarations;
    private readonly Dictionary<string, List<int>> _candidatesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _relaxedCandidatesByName = new(StringComparer.Ordinal);

    public long ExactHits { get; private set; }
    public long ExactMisses { get; private set; }
    public long RelaxedQueries { get; private set; }

    public CppTypeLookup(List<T> declarations)
    {
        _declarations = declarations;
        for (int i = 0; i < declarations.Count; i++)
        {
            string key = CppTypeExt.GetLookupKey(declarations[i]);
            ref List<int>? candidates = ref CollectionsMarshal.GetValueRefOrAddDefault(_candidatesByName, key, out bool exists);
            if (!exists)
            {
                candidates = [];
            }

            candidates!.Add(i);

            string relaxedKey = CppTypeExt.GetRelaxedLookupKey(declarations[i]);
            ref List<int>? relaxedCandidates = ref CollectionsMarshal.GetValueRefOrAddDefault(_relaxedCandidatesByName, relaxedKey, out bool relaxedExists);
            if (!relaxedExists)
            {
                relaxedCandidates = [];
            }

            relaxedCandidates!.Add(i);
        }
    }

    public ref T Find(string typeName, bool relax = false)
    {
        if (relax)
        {
            RelaxedQueries++;
            string relaxedName = CppTypeExt.RemapLookupTypeName(typeName);
            string relaxedKey = CppTypeExt.GetRelaxedLookupKey(relaxedName, false);
            if (_relaxedCandidatesByName.TryGetValue(relaxedKey, out List<int>? relaxedCandidates))
            {
                Span<T> declarations = CollectionsMarshal.AsSpan(_declarations);
                for (int i = relaxedCandidates.Count - 1; i >= 0; i--)
                {
                    ref T candidate = ref declarations[relaxedCandidates[i]];
                    if (candidate.IsSameType(relaxedName, true))
                    {
                        return ref candidate;
                    }
                }
            }

            return ref Unsafe.NullRef<T>();
        }

        string remappedName = CppTypeExt.RemapLookupTypeName(typeName);
        string key = CppTypeExt.GetLookupKey(remappedName, false);
        if (_candidatesByName.TryGetValue(key, out List<int>? candidates))
        {
            Span<T> declarations = CollectionsMarshal.AsSpan(_declarations);
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                ref T candidate = ref declarations[candidates[i]];
                if (candidate.IsSameType(remappedName))
                {
                    ExactHits++;
                    return ref candidate;
                }
            }
        }

        ExactMisses++;
        return ref Unsafe.NullRef<T>();
    }

    public ref T Find(CppType type, bool relax = false)
    {
        if (relax)
        {
            RelaxedQueries++;
            string rawRelaxedKey = CppTypeExt.GetRelaxedLookupKey(type);
            string remappedRelaxedKey = CppTypeExt.GetRelaxedLookupKey(type, true);
            IReadOnlyList<int>? relaxedCandidates = GetMergedCandidates(_relaxedCandidatesByName, rawRelaxedKey, remappedRelaxedKey);
            if (relaxedCandidates is not null)
            {
                Span<T> declarations = CollectionsMarshal.AsSpan(_declarations);
                foreach (int index in relaxedCandidates)
                {
                    ref T candidate = ref declarations[index];
                    if (candidate.IsSameType(type, true))
                    {
                        return ref candidate;
                    }
                }
            }

            return ref Unsafe.NullRef<T>();
        }

        string rawKey = CppTypeExt.GetLookupKey(type);
        string remappedKey = CppTypeExt.GetLookupKey(type, true);
        IReadOnlyList<int>? candidates = GetMergedCandidates(_candidatesByName, rawKey, remappedKey);
        if (candidates is not null)
        {
            Span<T> declarations = CollectionsMarshal.AsSpan(_declarations);
            foreach (int index in candidates)
            {
                ref T candidate = ref declarations[index];
                if (candidate.IsSameType(type))
                {
                    ExactHits++;
                    return ref candidate;
                }
            }
        }

        ExactMisses++;
        return ref Unsafe.NullRef<T>();
    }

    private static IReadOnlyList<int>? GetMergedCandidates(Dictionary<string, List<int>> index, string rawKey, string remappedKey)
    {
        index.TryGetValue(rawKey, out List<int>? rawCandidates);
        if (rawKey == remappedKey)
        {
            return rawCandidates;
        }

        index.TryGetValue(remappedKey, out List<int>? remappedCandidates);
        if (rawCandidates is null)
        {
            return remappedCandidates;
        }

        if (remappedCandidates is null)
        {
            return rawCandidates;
        }

        var merged = new List<int>(rawCandidates.Count + remappedCandidates.Count);
        int rawIndex = 0, remappedIndex = 0;
        while (rawIndex < rawCandidates.Count || remappedIndex < remappedCandidates.Count)
        {
            int raw = rawIndex < rawCandidates.Count ? rawCandidates[rawIndex] : int.MaxValue;
            int remapped = remappedIndex < remappedCandidates.Count ? remappedCandidates[remappedIndex] : int.MaxValue;
            int next = Math.Min(raw, remapped);
            merged.Add(next);
            if (raw == next)
            {
                rawIndex++;
            }
            if (remapped == next)
            {
                remappedIndex++;
            }
        }

        return merged;
    }
}
