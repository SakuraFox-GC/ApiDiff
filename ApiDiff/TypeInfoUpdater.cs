using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApiDiff;

// TypeInfo declarations keep their native aliases; only managed class hints change.
internal static partial class TypeInfoUpdater
{
    internal sealed record Update(byte[] Original, byte[] Proposed, IReadOnlyList<string> ChangedAliases);
    private sealed record Identity(string Image, string Namespace, string Name)
    {
        internal string Key => string.Join('\0', Image, Namespace, Name);
        internal string Path => string.Join('\0', Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries).Concat(Name.Split('+')));
        internal string Alias => string.Join('_', Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries).Concat(Name.Split('+')));
    }
    private sealed record Candidate(Identity Identity, string CppName);
    private sealed record Edit(int Start, int Length, string Replacement);

    [GeneratedRegex(@"\bDO_TYPEINFO\s*\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*;")]
    private static partial Regex DeclarationPattern();
    [GeneratedRegex(@"\bDO_TYPEINFO\b")]
    private static partial Regex MacroPattern();

    internal static Update Prepare(string metadataPath, string headerPath, string classesPath, string assembliesPath,
        IReadOnlyDictionary<string, string> remappings)
    {
        string outputPath = Path.GetFullPath(classesPath);
        StringComparer paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (new[] { metadataPath, headerPath, assembliesPath }.Any(path => paths.Equals(Path.GetFullPath(path), outputPath)))
            throw new InvalidDataException("The class hint output must differ from every source file.");
        string[] aliases = ReadAliases(File.ReadAllText(headerPath));
        using var metadataStream = File.OpenRead(metadataPath);
        using JsonDocument metadata = JsonDocument.Parse(metadataStream);
        JsonElement addressMap = metadata.RootElement.GetProperty("addressMap");
        var identities = new Dictionary<string, Identity>(StringComparer.Ordinal);
        foreach (JsonElement method in addressMap.GetProperty("methodDefinitions").EnumerateArray())
        {
            if (!method.TryGetProperty("group", out JsonElement groupElement)) continue;
            string[] parts = (groupElement.GetString() ?? throw new InvalidDataException("Method group must be a string.")).Split('/');
            if (parts.Length < 2) continue;
            var identity = new Identity(parts[0], string.Join('.', parts[1..^1]), parts[^1]);
            if (IsGeneric(identity.Name)) continue;
            identities.TryAdd(identity.Key, identity);
        }
        var byPath = identities.Values.GroupBy(identity => identity.Path)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var candidates = new List<Candidate>();
        foreach (JsonElement pointer in addressMap.GetProperty("typeInfoPointers").EnumerateArray())
        {
            string? path = ReadTypeInfoPath(pointer.GetProperty("name").GetString() ?? throw new InvalidDataException("TypeInfo symbol must be a string."));
            string type = pointer.GetProperty("type").GetString() ?? throw new InvalidDataException("TypeInfo C++ type must be a string.");
            if (path is null || !byPath.TryGetValue(path, out Identity[]? matches) ||
                !type.StartsWith("struct ", StringComparison.Ordinal) || !type.EndsWith("__Class *", StringComparison.Ordinal)) continue;
            string cppName = type[7..^9];
            foreach (Identity identity in matches) candidates.Add(new(identity, cppName));
        }
        candidates = candidates.Distinct().ToList();
        var byAlias = candidates.SelectMany(candidate => new[] { candidate.CppName, candidate.Identity.Alias }.Distinct()
                .Select(alias => (alias, candidate)))
            .GroupBy(item => item.alias, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.candidate).Distinct().ToArray(), StringComparer.Ordinal);

        byte[] original = File.ReadAllBytes(classesPath);
        bool bom = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf;
        string source = Encoding.UTF8.GetString(original.AsSpan(bom ? 3 : 0));
        var hints = JsonNode.Parse(source) as JsonObject ?? throw new InvalidDataException("Class hints must be a JSON object.");
        var assemblies = JsonNode.Parse(File.ReadAllText(assembliesPath)) as JsonObject ?? throw new InvalidDataException("Assemblies must be a JSON object.");
        var changes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (string alias in aliases)
        {
            JsonNode? previousNode = hints[alias];
            if (previousNode is not null && previousNode is not JsonObject)
                throw new InvalidDataException($"TypeInfo {alias}: its class hint must be an object.");
            JsonObject? previous = previousNode as JsonObject;
            bool remapped = remappings.TryGetValue(alias, out string? searchName);
            Candidate[] matches;
            if (!remapped && previous is not null)
            {
                matches = candidates.Where(candidate => MatchesHint(candidate.Identity, previous, assemblies)).ToArray();
                if (matches.Length == 0)
                    throw new InvalidDataException($"TypeInfo {alias}: its previous managed identity is missing; supply an explicit RemappedTypes entry.");
            }
            else
            {
                matches = byAlias.GetValueOrDefault(searchName ?? alias) ?? [];
            }
            Identity[] targets = matches.Select(candidate => candidate.Identity).Distinct().ToArray();
            if (targets.Length != 1)
                throw new InvalidDataException($"TypeInfo {alias}: expected one non-generic metadata identity, found {targets.Length}. Missing, ambiguous and unsupported types cannot be updated.");
            Identity target = targets[0];
            string[] assemblyAliases = assemblies.Where(pair => ContainsImage(pair.Value, target.Image)).Select(pair => pair.Key).ToArray();
            string? oldAssembly = previous?["Assembly"]?.GetValue<string>();
            string assembly = oldAssembly is not null && assemblyAliases.Contains(oldAssembly, StringComparer.Ordinal) ? oldAssembly :
                assemblyAliases.Length == 1 ? assemblyAliases[0] :
                throw new InvalidDataException($"TypeInfo {alias}: image {target.Image} has no unique assembly alias.");
            string name = target.Name.Replace('+', '/');
            if (previous?["Assembly"]?.GetValue<string>() == assembly && previous?["Namespace"]?.GetValue<string>() == target.Namespace &&
                previous?["Name"]?.GetValue<string>() == name) continue;
            var replacement = previous?.DeepClone() as JsonObject ?? new JsonObject();
            replacement["Assembly"] = assembly;
            replacement["Namespace"] = target.Namespace;
            replacement["Name"] = name;
            changes.Add(alias, replacement);
        }
        if (changes.Count == 0) return new(original, original, []);
        string[] changedAliases = changes.Keys.ToArray();
        string proposed = ApplyHints(source, changes);
        byte[] body = Encoding.UTF8.GetBytes(proposed);
        byte[] encoded = bom ? [0xef, 0xbb, 0xbf, .. body] : body;
        return new(original, encoded, changedAliases);
    }

    private static bool IsGeneric(string name) => name.IndexOfAny(['`', '<', '>', '[', ']']) >= 0;
    private static bool ContainsImage(JsonNode? node, string image) => node is JsonArray values &&
        values.Any(value => value?.GetValue<string>() == image);
    private static bool MatchesHint(Identity identity, JsonObject hint, JsonObject assemblies) =>
        hint["Namespace"]?.GetValue<string>() == identity.Namespace && hint["Name"]?.GetValue<string>()?.Replace('/', '+') == identity.Name &&
        hint["Assembly"]?.GetValue<string>() is string alias && ContainsImage(assemblies[alias], identity.Image);

    // Inspector emits a length-prefixed TypeInfo symbol. Unsupported encodings fail closed.
    private static string? ReadTypeInfoPath(string symbol)
    {
        const string prefix = "_ZN8TypeInfo";
        if (!symbol.StartsWith(prefix, StringComparison.Ordinal) || !symbol.EndsWith('E')) return null;
        var parts = new List<string>();
        int index = prefix.Length;
        while (index < symbol.Length - 1)
        {
            int start = index;
            while (index < symbol.Length - 1 && char.IsAsciiDigit(symbol[index])) ++index;
            if (start == index || !int.TryParse(symbol.AsSpan(start, index - start), out int length) ||
                length <= 0 || length > symbol.Length - 1 - index) return null;
            string part = symbol.Substring(index, length);
            if (IsGeneric(part)) return null;
            parts.Add(part);
            index += length;
        }
        return parts.Count > 0 ? string.Join('\0', parts) : null;
    }

    internal static string[] ReadAliases(string source)
    {
        // Mask trivia and preprocessor directives without changing offsets or line breaks.
        char[] code = source.ToCharArray();
        bool lineStart = true;
        for (int i = 0; i < code.Length; ++i)
        {
            char c = source[i];
            if (c is '\r' or '\n') { lineStart = true; continue; }
            if (lineStart && char.IsWhiteSpace(c)) continue;
            bool directive = lineStart && c == '#';
            lineStart = false;
            bool lineComment = c == '/' && i + 1 < source.Length && source[i + 1] == '/';
            bool blockComment = c == '/' && i + 1 < source.Length && source[i + 1] == '*';
            if (directive || lineComment)
            {
                do
                {
                    code[i] = ' ';
                    ++i;
                } while (i < code.Length && source[i] != '\n');
                --i;
            }
            else if (blockComment)
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Unterminated comment in TypeInfo header.");
                for (; i < end + 2; ++i) if (code[i] is not '\r' and not '\n') code[i] = ' ';
                --i;
            }
            else if (c is '"' or '\'')
            {
                char quote = c;
                code[i] = ' ';
                bool closed = false;
                while (++i < code.Length)
                {
                    char value = source[i];
                    code[i] = ' ';
                    if (value == '\\') { if (++i < code.Length) code[i] = ' '; continue; }
                    if (value == quote) { closed = true; break; }
                }
                if (!closed) throw new InvalidDataException("Unterminated literal in TypeInfo header.");
            }
        }
        string masked = new(code);
        MatchCollection declarations = DeclarationPattern().Matches(masked);
        if (declarations.Count == 0 || declarations.Count != MacroPattern().Matches(masked).Count)
            throw new InvalidDataException("Expected DO_TYPEINFO(name); declarations with one native identifier argument.");
        string[] aliases = declarations.Select(match => match.Groups["name"].Value).ToArray();
        if (aliases.Distinct(StringComparer.Ordinal).Count() != aliases.Length)
            throw new InvalidDataException("Duplicate TypeInfo aliases are not supported.");
        return aliases;
    }

    private static string ApplyHints(string source, Dictionary<string, JsonObject> changes)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(source);
        var reader = new Utf8JsonReader(utf8);
        var edits = new List<Edit>();
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string indentation = "  ";
        reader.Read();
        int lastEnd = (int)reader.BytesConsumed;
        bool hasProperties = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string alias = reader.GetString()!;
            int propertyStart = (int)reader.TokenStartIndex;
            int lineStart = Array.LastIndexOf(utf8, (byte)'\n', propertyStart) + 1;
            string propertyIndent = Encoding.UTF8.GetString(utf8.AsSpan(lineStart, propertyStart - lineStart));
            if (propertyIndent.All(char.IsWhiteSpace)) indentation = propertyIndent;
            reader.Read();
            int valueStart = (int)reader.TokenStartIndex;
            reader.Skip();
            lastEnd = (int)reader.BytesConsumed;
            hasProperties = true;
            if (changes.Remove(alias, out JsonObject? replacement))
                edits.Add(new(valueStart, lastEnd - valueStart, Format(replacement, indentation, newline)));
        }
        if (changes.Count > 0)
        {
            string insertion = string.Join("," + newline, changes.Select(pair => indentation + "\"" + JsonEncodedText.Encode(pair.Key).ToString() + "\"" + ": " + Format(pair.Value, indentation, newline)));
            string closingNewline = utf8.AsSpan(lastEnd, (int)reader.TokenStartIndex - lastEnd).Contains((byte)'\n') ? "" : newline;
            edits.Add(new(lastEnd, 0, (hasProperties ? "," : "") + newline + insertion + closingNewline));
        }
        using var output = new MemoryStream();
        int position = 0;
        foreach (Edit edit in edits.OrderBy(edit => edit.Start))
        {
            output.Write(utf8.AsSpan(position, edit.Start - position));
            output.Write(Encoding.UTF8.GetBytes(edit.Replacement));
            position = edit.Start + edit.Length;
        }
        output.Write(utf8.AsSpan(position));
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static string Format(JsonObject node, string indentation, string newline)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true })) node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray()).Replace("\n", newline + indentation, StringComparison.Ordinal);
    }
}
