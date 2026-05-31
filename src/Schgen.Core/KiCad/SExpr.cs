using System.Globalization;
using System.Text;

namespace Schgen.Core.KiCad;

/// S-expression DOM used for KiCad files.
/// A node is either an Atom (symbol-like bareword, integer, float, or quoted string)
/// or a List (parenthesised, with a head symbol and zero or more children).
public abstract record SExpr
{
    public static SExpr Parse(string text) => new SExprReader(text).ReadOne();

    public static IReadOnlyList<SExpr> ParseAll(string text)
    {
        var r = new SExprReader(text);
        var nodes = new List<SExpr>();
        while (!r.AtEnd) nodes.Add(r.ReadOne());
        return nodes;
    }

    public string Format(SExprFormatOptions? opts = null)
    {
        var sb = new StringBuilder();
        SExprWriter.Write(sb, this, opts ?? SExprFormatOptions.Default, indent: 0);
        return sb.ToString();
    }
}

public sealed record SAtom : SExpr
{
    public string Value { get; }
    public bool Quoted { get; }
    public SAtom(string value, bool quoted)
    {
        Value = value;
        Quoted = quoted;
    }
    public static SAtom Sym(string s) => new(s, quoted: false);
    public static SAtom Str(string s) => new(s, quoted: true);
    public static SAtom Num(double d) => new(d.ToString("R", CultureInfo.InvariantCulture), quoted: false);
    public static SAtom Num(int i) => new(i.ToString(CultureInfo.InvariantCulture), quoted: false);

    public override string ToString() => Quoted ? $"\"{Value}\"" : Value;
}

public sealed record SList : SExpr
{
    public IReadOnlyList<SExpr> Items { get; }
    public SList(IReadOnlyList<SExpr> items) { Items = items; }
    public SList(params SExpr[] items) : this((IReadOnlyList<SExpr>)items) { }

    public string Head => Items.Count > 0 && Items[0] is SAtom a ? a.Value : "";

    public SExpr? Get(int i) => i < Items.Count ? Items[i] : null;

    /// First child SList whose head matches `head`.
    public SList? First(string head) =>
        Items.OfType<SList>().FirstOrDefault(l => l.Head == head);

    /// All child SLists whose head matches `head`.
    public IEnumerable<SList> All(string head) =>
        Items.OfType<SList>().Where(l => l.Head == head);

    /// Value of the first atom following the head - common shape for
    /// (name "FOO" ...), (number "1" ...), (length 2.54), etc. Null if no such atom.
    public string? StringValue =>
        Items.Count >= 2 && Items[1] is SAtom a ? a.Value : null;
}

public sealed record SExprFormatOptions(string Indent, bool NewlineLists)
{
    public static SExprFormatOptions Default { get; } = new("  ", NewlineLists: true);
    public static SExprFormatOptions Compact { get; } = new("", NewlineLists: false);
}

internal sealed class SExprReader
{
    private readonly string _src;
    private int _pos;

    public SExprReader(string src) { _src = src; _pos = 0; }

    public bool AtEnd
    {
        get { SkipWs(); return _pos >= _src.Length; }
    }

    public SExpr ReadOne()
    {
        SkipWs();
        if (_pos >= _src.Length) throw new FormatException("unexpected end of input");
        char c = _src[_pos];
        if (c == '(') return ReadList();
        if (c == ')') throw new FormatException($"unmatched ')' at {_pos}");
        if (c == '"') return ReadString();
        return ReadSymbol();
    }

    private SList ReadList()
    {
        if (_src[_pos] != '(') throw new FormatException("expected '('");
        _pos++;
        var items = new List<SExpr>();
        while (true)
        {
            SkipWs();
            if (_pos >= _src.Length) throw new FormatException("unterminated list");
            if (_src[_pos] == ')') { _pos++; return new SList(items); }
            items.Add(ReadOne());
        }
    }

    private SAtom ReadString()
    {
        if (_src[_pos] != '"') throw new FormatException("expected '\"'");
        _pos++;
        var sb = new StringBuilder();
        while (_pos < _src.Length)
        {
            char c = _src[_pos++];
            if (c == '"') return new SAtom(sb.ToString(), quoted: true);
            if (c == '\\' && _pos < _src.Length)
            {
                char esc = _src[_pos++];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => esc,
                });
            }
            else sb.Append(c);
        }
        throw new FormatException("unterminated string");
    }

    private SAtom ReadSymbol()
    {
        int start = _pos;
        while (_pos < _src.Length && !IsDelimiter(_src[_pos])) _pos++;
        if (_pos == start) throw new FormatException($"empty token at {_pos}");
        return new SAtom(_src[start.._pos], quoted: false);
    }

    private void SkipWs()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            if (char.IsWhiteSpace(c)) { _pos++; continue; }
            // KiCad files don't use ';' comments, but tolerate them just in case.
            if (c == ';') { while (_pos < _src.Length && _src[_pos] != '\n') _pos++; continue; }
            break;
        }
    }

    private static bool IsDelimiter(char c) =>
        char.IsWhiteSpace(c) || c == '(' || c == ')' || c == '"';
}

internal static class SExprWriter
{
    public static void Write(StringBuilder sb, SExpr node, SExprFormatOptions opts, int indent)
    {
        switch (node)
        {
            case SAtom atom:
                WriteAtom(sb, atom);
                break;
            case SList list:
                WriteList(sb, list, opts, indent);
                break;
            default:
                throw new InvalidOperationException($"unknown SExpr type: {node.GetType()}");
        }
    }

    private static void WriteAtom(StringBuilder sb, SAtom atom)
    {
        if (atom.Quoted)
        {
            sb.Append('"');
            foreach (char c in atom.Value)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:   sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
        else
        {
            sb.Append(atom.Value);
        }
    }

    private static void WriteList(StringBuilder sb, SList list, SExprFormatOptions opts, int indent)
    {
        sb.Append('(');
        if (list.Items.Count == 0) { sb.Append(')'); return; }

        // Head symbol on the same line as '('
        Write(sb, list.Items[0], opts, indent);

        bool inlineChildren = !opts.NewlineLists || IsInlineable(list);
        for (int i = 1; i < list.Items.Count; i++)
        {
            if (inlineChildren)
            {
                sb.Append(' ');
                Write(sb, list.Items[i], opts, indent);
            }
            else
            {
                sb.Append('\n');
                for (int k = 0; k <= indent; k++) sb.Append(opts.Indent);
                Write(sb, list.Items[i], opts, indent + 1);
            }
        }
        sb.Append(')');
    }

    // Inline a list when it's small + atom-only or contains only short atom-only children.
    private static bool IsInlineable(SList list)
    {
        if (list.Items.Count <= 4 && list.Items.Skip(1).All(c => c is SAtom)) return true;
        return false;
    }
}
