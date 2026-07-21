using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LocalRag.Infrastructure.Processing;

internal abstract class StructuralChunkerBase(string id, string[] extensions) : IStructuralChunker
{
    public string ChunkerId => id;
    public string ChunkerVersion => "1";
    public bool Supports(string relativePath) => extensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase);
    public abstract bool TryChunk(string relativePath, string normalizedContent, out IReadOnlyList<StructuralUnit> units);

    protected static StructuralUnit Module(string kind, string locator, string content) =>
        new(kind, null, null, locator, 1, ChunkingText.Lines(content).Length);

    protected static IReadOnlyList<StructuralUnit> WithCoverage(
        string content,
        IEnumerable<StructuralUnit> structuralUnits,
        string locatorPrefix)
    {
        var lines = ChunkingText.Lines(content);
        var output = structuralUnits.ToList();
        var covered = new bool[lines.Length];
        foreach (var unit in output)
        {
            for (var line = unit.StartLine; line <= unit.EndLine && line <= lines.Length; line++) covered[line - 1] = true;
        }
        for (var start = 0; start < lines.Length;)
        {
            if (covered[start] || string.IsNullOrWhiteSpace(lines[start])) { start++; continue; }
            var end = start + 1;
            while (end < lines.Length && !covered[end]) end++;
            if (lines[start..end].Any(line => !string.IsNullOrWhiteSpace(line)))
            {
                output.Add(new StructuralUnit(
                    "text", null, null, $"{locatorPrefix}:gap:lines:{start + 1}-{end}", start + 1, end));
            }
            start = end;
        }
        return output;
    }

    protected static bool HasBalancedQuotes(string value)
    {
        var singleQuoted = false;
        var doubleQuoted = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (!singleQuoted && !doubleQuoted && current == '#') break;
            if (!singleQuoted && current == '"' && !escaped) doubleQuoted = !doubleQuoted;
            else if (!doubleQuoted && current == '\'' && !escaped) singleQuoted = !singleQuoted;
            escaped = doubleQuoted && !escaped && current == '\\';
            if (current != '\\') escaped = false;
        }
        return !singleQuoted && !doubleQuoted;
    }

    protected static bool HasBalancedCollections(string value)
    {
        var stack = new Stack<char>();
        var quote = '\0';
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped) { escaped = false; continue; }
            if (quote != '\0')
            {
                if (character == '\\') { escaped = true; continue; }
                if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '#') break;
            if (character is '[' or '{') { stack.Push(character); continue; }
            if (character == ']' && (stack.Count == 0 || stack.Pop() != '[')) return false;
            if (character == '}' && (stack.Count == 0 || stack.Pop() != '{')) return false;
        }
        return quote == '\0' && stack.Count == 0;
    }
}

internal sealed partial class CSharpStructuralChunker() : StructuralChunkerBase("csharp", [".cs"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units) =>
        CStyleUnits(content, CSharpDeclarationRegex(), "csharp", out units);

    [GeneratedRegex(@"(?m)^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|async|virtual|override|new|readonly|unsafe)\s+)*(?:(?<kind>class|record|struct|interface|enum|namespace)\s+(?<name>[A-Za-z_][\w.]*)|(?:[\w<>,?\[\].]+\s+)+(?<name>[A-Za-z_]\w*)\s*\([^;{}]*\))[^;\n{]*(?:\n\s*)?\{")]
    private static partial Regex CSharpDeclarationRegex();

    internal static bool CStyleUnits(string content, Regex declarations, string moduleKind, out IReadOnlyList<StructuralUnit> units)
    {
        if (!TryLexicalMask(content, out var masked)) { units = []; return false; }
        if (!TryBracePairs(masked, out var pairs)) { units = []; return false; }
        var lines = ChunkingText.Lines(content);
        var fileScopedNamespace = moduleKind == "csharp"
            ? FileScopedNamespaceRegex().Match(masked).Groups["name"].Value
            : string.Empty;
        var nodes = new List<(string Kind, string Name, int StartLine, int EndLine, int Open, int Close)>();
        foreach (Match match in declarations.Matches(masked))
        {
            var open = masked.IndexOf('{', match.Index, match.Length);
            if (open < 0 || !pairs.TryGetValue(open, out var close)) continue;
            var name = match.Groups["name"].Value;
            var kind = match.Groups["kind"].Success ? match.Groups["kind"].Value : "member";
            var startLine = LineAt(masked, match.Index);
            var endLine = LineAt(masked, close);
            nodes.Add((kind, name, startLine, endLine, open, close));
        }
        var output = nodes.Select(node =>
        {
            IEnumerable<string> ancestors = nodes.Where(candidate => candidate.Open < node.Open && candidate.Close > node.Close)
                .OrderBy(candidate => candidate.Open)
                .Select(candidate => candidate.Name);
            if (!string.IsNullOrWhiteSpace(fileScopedNamespace) && node.Kind != "namespace")
                ancestors = ancestors.Prepend(fileScopedNamespace);
            var qualifiedName = string.Join('.', ancestors.Append(node.Name));
            return new StructuralUnit(node.Kind, node.Name, qualifiedName,
                $"{node.Kind}:{qualifiedName}@lines:{node.StartLine}-{node.EndLine}", node.StartLine, node.EndLine);
        }).ToList();
        if (output.Count == 0 && !string.IsNullOrWhiteSpace(masked)) output.Add(Module(moduleKind, $"module:lines:1-{lines.Length}", content));
        units = WithCoverage(content, output, moduleKind);
        return true;
    }

    private static bool TryLexicalMask(string content, out string masked)
    {
        var chars = content.ToCharArray();
        var state = 0; // 0 code, 1 line comment, 2 block comment, 3 single, 4 double, 5 template
        var escaped = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var current = chars[i];
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';
            if (state == 0)
            {
                if (current == '/' && next == '/') { chars[i++] = chars[i - 1] = ' '; state = 1; continue; }
                if (current == '/' && next == '*') { chars[i++] = chars[i - 1] = ' '; state = 2; continue; }
                if (current == '\'') { chars[i] = ' '; state = 3; continue; }
                if (current == '"') { chars[i] = ' '; state = 4; continue; }
                if (current == '`') { chars[i] = ' '; state = 5; continue; }
                continue;
            }
            if (current == '\n') { if (state == 1) state = 0; continue; }
            chars[i] = ' ';
            if (state == 2 && current == '*' && next == '/') { chars[++i] = ' '; state = 0; continue; }
            if (state is 3 or 4 or 5)
            {
                if (!escaped && ((state == 3 && current == '\'') || (state == 4 && current == '"') || (state == 5 && current == '`'))) state = 0;
                escaped = !escaped && current == '\\';
                if (current != '\\') escaped = false;
            }
        }
        masked = new string(chars);
        return state is 0 or 1;
    }

    [GeneratedRegex(@"(?m)^\s*namespace\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*;")]
    private static partial Regex FileScopedNamespaceRegex();

    private static bool TryBracePairs(string content, out Dictionary<int, int> pairs)
    {
        pairs = [];
        var stack = new Stack<int>();
        var parentheses = 0;
        var brackets = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '{') stack.Push(i);
            else if (content[i] == '}' && (stack.Count == 0 || !pairs.TryAdd(stack.Pop(), i))) return false;
            else if (content[i] == '(') parentheses++;
            else if (content[i] == ')' && --parentheses < 0) return false;
            else if (content[i] == '[') brackets++;
            else if (content[i] == ']' && --brackets < 0) return false;
        }
        return stack.Count == 0 && parentheses == 0 && brackets == 0;
    }

    private static int LineAt(string content, int position)
    {
        var line = 1;
        for (var i = 0; i < position; i++) if (content[i] == '\n') line++;
        return line;
    }
}

internal sealed partial class TypeScriptJavaScriptStructuralChunker()
    : StructuralChunkerBase("typescript-javascript", [".ts", ".tsx", ".js", ".jsx"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units) =>
        CSharpStructuralChunker.CStyleUnits(content, DeclarationRegex(), "module", out units);

    [GeneratedRegex(@"(?m)^\s*(?:(?:export|default|async|declare|abstract)\s+)*(?:(?<kind>class|interface|namespace|module|function)\s+(?<name>[A-Za-z_$][\w$]*)|(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?(?:\([^\n]*?\)|[A-Za-z_$][\w$]*)\s*=>)[^{\n]*\{")]
    private static partial Regex DeclarationRegex();
}

internal sealed partial class PythonStructuralChunker() : StructuralChunkerBase("python", [".py"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units)
    {
        var lines = ChunkingText.Lines(content);
        if (!TryLexicalMask(lines, out var maskedLines)) { units = []; return false; }
        var declarations = new List<(int Line, int Indent, string Kind, string Name)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains('\t')) { units = []; return false; }
            var match = DeclarationRegex().Match(maskedLines[i]);
            if (!match.Success && DeclarationPrefixRegex().IsMatch(maskedLines[i])) { units = []; return false; }
            if (match.Success) declarations.Add((i + 1, match.Groups["indent"].Length, match.Groups["kind"].Value, match.Groups["name"].Value));
        }
        var spans = new List<(int Line, int End, int Indent, string Kind, string Name)>();
        for (var i = 0; i < declarations.Count; i++)
        {
            var declaration = declarations[i];
            var end = lines.Length;
            for (var line = declaration.Line; line < lines.Length; line++)
            {
                if (string.IsNullOrWhiteSpace(maskedLines[line])) continue;
                var indent = maskedLines[line].Length - maskedLines[line].TrimStart().Length;
                if (indent <= declaration.Indent) { end = line; break; }
            }
            spans.Add((declaration.Line, end, declaration.Indent, declaration.Kind, declaration.Name));
        }
        var output = spans.Select(span =>
        {
            var ancestors = spans.Where(candidate => candidate.Line < span.Line && candidate.End >= span.End && candidate.Indent < span.Indent)
                .OrderBy(candidate => candidate.Indent)
                .Select(candidate => candidate.Name);
            var qualifiedName = string.Join('.', ancestors.Append(span.Name));
            return new StructuralUnit(span.Kind, span.Name, qualifiedName,
                $"{span.Kind}:{qualifiedName}@lines:{span.Line}-{span.End}", span.Line, span.End);
        }).ToList();
        var searchable = lines.Any(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'));
        if (output.Count == 0 && searchable) output.Add(Module("module", $"module:lines:1-{lines.Length}", content));
        units = WithCoverage(content, output, "python");
        return true;
    }

    private static bool TryLexicalMask(string[] lines, out string[] maskedLines)
    {
        maskedLines = new string[lines.Length];
        char tripleQuote = '\0';
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var masked = line.ToCharArray();
            char quote = '\0';
            var escaped = false;
            for (var index = 0; index < line.Length; index++)
            {
                if (tripleQuote != '\0')
                {
                    masked[index] = ' ';
                    if (index + 2 < line.Length && line[index] == tripleQuote &&
                        line[index + 1] == tripleQuote && line[index + 2] == tripleQuote)
                    {
                        masked[index + 1] = masked[index + 2] = ' ';
                        index += 2;
                        tripleQuote = '\0';
                    }
                    continue;
                }
                if (quote != '\0')
                {
                    masked[index] = ' ';
                    if (escaped) { escaped = false; continue; }
                    if (line[index] == '\\') { escaped = true; continue; }
                    if (line[index] == quote) quote = '\0';
                    continue;
                }
                if (line[index] == '#')
                {
                    Array.Fill(masked, ' ', index, masked.Length - index);
                    break;
                }
                if (line[index] is '\'' or '"')
                {
                    if (index + 2 < line.Length && line[index + 1] == line[index] && line[index + 2] == line[index])
                    {
                        tripleQuote = line[index];
                        masked[index] = masked[index + 1] = masked[index + 2] = ' ';
                        index += 2;
                    }
                    else
                    {
                        quote = line[index];
                        masked[index] = ' ';
                    }
                }
            }
            if (quote != '\0') return false;
            maskedLines[lineIndex] = new string(masked);
        }
        return tripleQuote == '\0';
    }

    [GeneratedRegex(@"^(?<indent> *)(?:async\s+)?(?:(?<kind>class)\s+(?<name>[A-Za-z_]\w*)(?:\s*\([^()\n]*\))?|(?<kind>def)\s+(?<name>[A-Za-z_]\w*)\s*\([^()\n]*\)(?:\s*->\s*[^:]+)?)\s*:\s*(?:#.*)?$")]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex(@"^\s*(?:async\s+)?(?:class|def)\b")]
    private static partial Regex DeclarationPrefixRegex();
}

internal sealed partial class MarkdownStructuralChunker() : StructuralChunkerBase("markdown", [".md"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units)
    {
        var lines = ChunkingText.Lines(content);
        var headings = lines.Select((line, index) =>
                (Match: HeadingRegex().Match(line), Line: index + 1, Level: line.TakeWhile(character => character == '#').Count()))
            .Where(item => item.Match.Success).ToArray();
        if (headings.Length == 0)
        {
            units = string.IsNullOrWhiteSpace(content) ? [] : [Module("section", $"section:lines:1-{lines.Length}", content)];
            return true;
        }
        var output = new List<StructuralUnit>();
        if (headings[0].Line > 1 && lines[..(headings[0].Line - 1)].Any(line => !string.IsNullOrWhiteSpace(line)))
            output.Add(new("section", null, null, $"preamble:lines:1-{headings[0].Line - 1}", 1, headings[0].Line - 1));
        for (var i = 0; i < headings.Length; i++)
        {
            var name = headings[i].Match.Groups["name"].Value.Trim();
            var end = i + 1 < headings.Length ? headings[i + 1].Line - 1 : lines.Length;
            var ancestors = headings.Take(i).Where(candidate => candidate.Level < headings[i].Level)
                .Reverse().Aggregate(new List<(int Level, string Name)>(), (parents, candidate) =>
                {
                    if (parents.Count == 0 || candidate.Level < parents[^1].Level)
                        parents.Add((candidate.Level, candidate.Match.Groups["name"].Value.Trim()));
                    return parents;
                }).AsEnumerable().Reverse().Select(parent => parent.Name);
            var qualifiedName = string.Join(" / ", ancestors.Append(name));
            output.Add(new("section", name, qualifiedName, $"heading:{qualifiedName}@lines:{headings[i].Line}-{end}", headings[i].Line, end));
        }
        units = WithCoverage(content, output, "markdown");
        return true;
    }

    [GeneratedRegex(@"^#{1,6}\s+(?<name>.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();
}

internal sealed partial class JsonStructuralChunker() : StructuralChunkerBase("json", [".json"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units)
    {
        try { using var _ = JsonDocument.Parse(content); }
        catch (JsonException) { units = []; return false; }
        var lines = ChunkingText.Lines(content);
        var properties = lines.Select((line, index) => (Match: PropertyRegex().Match(line), Line: index + 1))
            .Where(item => item.Match.Success).ToArray();
        if (properties.Length == 0) { units = string.IsNullOrWhiteSpace(content) ? [] : [Module("object", $"json:lines:1-{lines.Length}", content)]; return true; }
        var output = properties.Select((property, index) =>
        {
            var name = property.Match.Groups["name"].Value;
            var end = index + 1 < properties.Length ? properties[index + 1].Line - 1 : lines.Length;
            return new StructuralUnit("property", name, name, $"property:{name}@lines:{property.Line}-{end}", property.Line, end);
        }).ToArray();
        units = WithCoverage(content, output, "json");
        return true;
    }

    [GeneratedRegex("^  \\\"(?<name>(?:[^\\\"\\\\]|\\\\.)+)\\\"\\s*:")]
    private static partial Regex PropertyRegex();
}

internal sealed partial class YamlStructuralChunker() : StructuralChunkerBase("yaml", [".yaml", ".yml"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units)
    {
        var lines = ChunkingText.Lines(content);
        if (lines.Any(line => line.StartsWith('\t'))) { units = []; return false; }
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed is "---" or "...") continue;
            if (line.Length == trimmed.Length && !HeaderRegex().IsMatch(line)) { units = []; return false; }
            if (line.Length != trimmed.Length && !trimmed.StartsWith("- ", StringComparison.Ordinal) && !trimmed.Contains(':'))
            { units = []; return false; }
            var separator = trimmed.IndexOf(':');
            if (separator >= 0 && (!HasBalancedQuotes(trimmed[(separator + 1)..]) ||
                !HasBalancedCollections(trimmed[(separator + 1)..]))) { units = []; return false; }
        }
        units = WithCoverage(content, SectionUnits(lines, HeaderRegex(), "key", "yaml"), "yaml");
        return true;
    }
    [GeneratedRegex(@"^(?<name>[^\s#][^:#]*):(?:\s|$)")]
    private static partial Regex HeaderRegex();

    internal static IReadOnlyList<StructuralUnit> SectionUnits(string[] lines, Regex regex, string kind, string prefix)
    {
        var headers = lines.Select((line, index) => (Match: regex.Match(line), Line: index + 1)).Where(x => x.Match.Success).ToArray();
        if (headers.Length == 0) return lines.Any(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            ? [new(kind, null, null, $"{prefix}:lines:1-{lines.Length}", 1, lines.Length)] : [];
        return headers.Select((header, index) =>
        {
            var name = header.Match.Groups["name"].Value.Trim();
            var end = index + 1 < headers.Length ? headers[index + 1].Line - 1 : lines.Length;
            return new StructuralUnit(kind, name, name, $"{kind}:{name}@lines:{header.Line}-{end}", header.Line, end);
        }).ToArray();
    }
}

internal sealed partial class TomlStructuralChunker() : StructuralChunkerBase("toml", [".toml"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units)
    {
        var lines = ChunkingText.Lines(content);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('[') ? !HeaderRegex().IsMatch(line) : !trimmed.Contains('='))
            { units = []; return false; }
            var separator = trimmed.IndexOf('=');
            if (separator >= 0 && (!HasBalancedQuotes(trimmed[(separator + 1)..]) ||
                !HasBalancedCollections(trimmed[(separator + 1)..]))) { units = []; return false; }
        }
        units = WithCoverage(content, YamlStructuralChunker.SectionUnits(lines, HeaderRegex(), "table", "toml"), "toml");
        return true;
    }
    [GeneratedRegex(@"^\s*\[\[?(?<name>[^\]]+)\]\]?\s*(?:#.*)?$")]
    private static partial Regex HeaderRegex();
}

internal sealed class XmlStructuralChunker() : StructuralChunkerBase("xml", [".xml", ".csproj", ".props", ".targets"])
{
    public override bool TryChunk(string relativePath, string content, out IReadOnlyList<StructuralUnit> units)
    {
        try
        {
            var document = XDocument.Parse(content, LoadOptions.SetLineInfo);
            var lines = ChunkingText.Lines(content);
            var children = document.Root?.Elements().Select(element => (Element: element, Line: ((IXmlLineInfo)element).LineNumber))
                .Where(item => item.Line > 0).ToArray() ?? [];
            if (children.Length == 0) { units = string.IsNullOrWhiteSpace(content) ? [] : [Module("element", $"xml:lines:1-{lines.Length}", content)]; return true; }
            var rootName = document.Root?.Name.LocalName;
            var output = children.Select((child, index) =>
            {
                var end = index + 1 < children.Length ? children[index + 1].Line - 1 : lines.Length;
                var name = child.Element.Name.LocalName;
                var qualifiedName = string.IsNullOrWhiteSpace(rootName) ? name : $"{rootName}.{name}";
                return new StructuralUnit("element", name, qualifiedName, $"element:{qualifiedName}@lines:{child.Line}-{end}", child.Line, end);
            }).ToArray();
            units = WithCoverage(content, output, "xml");
            return true;
        }
        catch (XmlException) { units = []; return false; }
    }
}
