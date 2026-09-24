using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Susu.Storage;

/// <summary>A problem found in a settings/manifest file, with its exact path (PLAN 5.2, DATA01).</summary>
public sealed record FileIssue(string Path, string Code, string Message, int Line = 0, int Column = 0)
{
    public override string ToString() => Line > 0 ? $"{Path} (line {Line}, column {Column}): {Code}: {Message}" : $"{Path}: {Code}: {Message}";
}

public abstract record YNode(int Line, int Column);
public sealed record YScalar(string Value, bool Quoted, int Line, int Column) : YNode(Line, Column);
public sealed record YSeq(IReadOnlyList<YNode> Items, int Line, int Column) : YNode(Line, Column);
public sealed record YMap(IReadOnlyList<KeyValuePair<string, YNode>> Entries, int Line, int Column) : YNode(Line, Column)
{
    public YNode? Get(string key) => Entries.FirstOrDefault(e => e.Key == key).Value;
}

/// <summary>
/// Reads the YAML 1.2 core subset allowed by PLAN 5.2: mappings, sequences, scalars, comments. Anchors,
/// aliases, merge keys, explicit tags and multiple documents are rejected, as are duplicate keys. Uses the
/// YamlDotNet event parser only (no reflection), which is NativeAOT-safe.
/// </summary>
public static class YamlSubset
{
    public const int MaxBytes = 1 << 20;

    public static (YNode? Root, IReadOnlyList<FileIssue> Issues) Parse(string text)
    {
        var issues = new List<FileIssue>();
        if (text.Length > MaxBytes) return (null, [new FileIssue("$", "too-large", $"file exceeds {MaxBytes} bytes")]);
        try
        {
            var parser = new Parser(new StringReader(text));
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _)) return (null, [new FileIssue("$", "empty", "file is empty")]);
            parser.Consume<DocumentStart>();
            var root = ReadNode(parser, "$", issues);
            parser.Consume<DocumentEnd>();
            if (!parser.Accept<StreamEnd>(out _))
            {
                var mark = parser.Current!.Start;
                issues.Add(new FileIssue("$", "forbidden-feature", "multiple documents are not allowed", (int)mark.Line, (int)mark.Column));
            }
            return (issues.Count == 0 ? root : null, issues);
        }
        catch (YamlException e)
        {
            issues.Add(new FileIssue("$", "syntax", e.Message.Split('\n')[0], (int)e.Start.Line, (int)e.Start.Column));
            return (null, issues);
        }
    }

    private static YNode ReadNode(IParser parser, string path, List<FileIssue> issues)
    {
        var current = parser.Current ?? throw new YamlException("unexpected end of input");
        int line = (int)current.Start.Line, column = (int)current.Start.Column;
        if (current is AnchorAlias)
        {
            issues.Add(new FileIssue(path, "forbidden-feature", "aliases are not allowed", line, column));
            parser.MoveNext();
            return new YScalar("", false, line, column);
        }
        if (current is NodeEvent node)
        {
            if (!node.Anchor.IsEmpty) issues.Add(new FileIssue(path, "forbidden-feature", "anchors are not allowed", line, column));
            if (!node.Tag.IsEmpty && !node.Tag.IsNonSpecific) issues.Add(new FileIssue(path, "forbidden-feature", "explicit tags are not allowed", line, column));
        }
        switch (current)
        {
            case Scalar scalar:
                parser.MoveNext();
                return new YScalar(scalar.Value, scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded, line, column);
            case SequenceStart:
            {
                parser.MoveNext();
                var items = new List<YNode>();
                while (!parser.TryConsume<SequenceEnd>(out _)) items.Add(ReadNode(parser, $"{path}[{items.Count}]", issues));
                return new YSeq(items, line, column);
            }
            case MappingStart:
            {
                parser.MoveNext();
                var entries = new List<KeyValuePair<string, YNode>>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    var keyEvent = parser.Current!;
                    var keyNode = ReadNode(parser, path, issues);
                    string key = keyNode is YScalar s ? s.Value : "";
                    string childPath = path == "$" ? key : $"{path}.{key}";
                    if (keyNode is not YScalar) issues.Add(new FileIssue(path, "type", "mapping keys must be scalars", (int)keyEvent.Start.Line, (int)keyEvent.Start.Column));
                    else if (key == "<<") issues.Add(new FileIssue(childPath, "forbidden-feature", "merge keys are not allowed", keyNode.Line, keyNode.Column));
                    else if (!keys.Add(key)) issues.Add(new FileIssue(childPath, "duplicate-key", $"key '{key}' appears more than once", keyNode.Line, keyNode.Column));
                    var value = ReadNode(parser, childPath, issues);
                    entries.Add(new(key, value));
                }
                return new YMap(entries, line, column);
            }
            default:
                throw new YamlException(current.Start, current.End, $"unexpected {current.GetType().Name}");
        }
    }

    /// <summary>Double-quoted YAML scalar that round-trips any string (control characters escaped).</summary>
    public static string Quote(string value)
    {
        var b = new System.Text.StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || c is (char)0x2028 or (char)0x2029 or (char)0xFEFF) b.Append($"\\u{(int)c:x4}");
                    else b.Append(c);
                    break;
            }
        }
        return b.Append('"').ToString();
    }
}
