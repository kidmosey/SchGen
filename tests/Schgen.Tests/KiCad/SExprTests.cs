using FluentAssertions;
using Schgen.Core.KiCad;
using Xunit;

namespace Schgen.Tests.KiCad;

public class SExprTests
{
    [Fact]
    public void Parses_simple_list()
    {
        var s = (SList)SExpr.Parse("(at 1.0 2.0)");
        s.Head.Should().Be("at");
        s.Items.Should().HaveCount(3);
        ((SAtom)s.Items[1]).Value.Should().Be("1.0");
        ((SAtom)s.Items[2]).Value.Should().Be("2.0");
    }

    [Fact]
    public void Parses_quoted_string_with_escapes()
    {
        var s = (SList)SExpr.Parse("(value \"100 nF \\\"X7R\\\"\")");
        var v = (SAtom)s.Items[1];
        v.Quoted.Should().BeTrue();
        v.Value.Should().Be("100 nF \"X7R\"");
    }

    [Fact]
    public void Parses_nested_lists()
    {
        var s = (SList)SExpr.Parse("(pin (at 1 2) (length 2.54))");
        s.Head.Should().Be("pin");
        s.First("at")!.Items.Should().HaveCount(3);
        s.First("length")!.StringValue.Should().Be("2.54");
    }

    [Fact]
    public void Round_trips_compact_form()
    {
        const string src = "(at 1.0 2.0 90)";
        var node = SExpr.Parse(src);
        node.Format(SExprFormatOptions.Compact).Should().Be(src);
    }

    [Fact]
    public void Round_trip_preserves_quoted_atoms()
    {
        const string src = "(property \"Reference\" \"U1\" (at 0 0 0))";
        var node = SExpr.Parse(src);
        var roundTripped = SExpr.Parse(node.Format(SExprFormatOptions.Compact));
        roundTripped.Format(SExprFormatOptions.Compact).Should().Be(src);
    }

    [Fact]
    public void All_returns_every_matching_child()
    {
        var s = (SList)SExpr.Parse("(symbol (pin A) (pin B) (pin C) (other x))");
        s.All("pin").Should().HaveCount(3);
        s.All("other").Should().HaveCount(1);
    }

    [Fact]
    public void Multi_node_input_parsed_via_ParseAll()
    {
        var nodes = SExpr.ParseAll("(a 1) (b 2)");
        nodes.Should().HaveCount(2);
        ((SList)nodes[0]).Head.Should().Be("a");
        ((SList)nodes[1]).Head.Should().Be("b");
    }

    [Fact]
    public void Unterminated_string_throws()
    {
        Action act = () => SExpr.Parse("(value \"unterminated");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Unmatched_paren_throws()
    {
        Action act = () => SExpr.Parse("(a (b c)");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Pretty_format_indents_nested_lists()
    {
        var node = SExpr.Parse("(kicad_sch (version 20230121) (uuid \"abc\") (paper \"A3\"))");
        var formatted = node.Format();
        formatted.Should().Contain("\n  (version");
        formatted.Should().Contain("\n  (paper");
    }
}
