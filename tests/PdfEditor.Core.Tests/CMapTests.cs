using System.Text;
using PdfEditor.Core.Pdf.Fonts;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// CMaps decide how a composite font's bytes split into character codes and what text each code
/// is. Both matter to redaction: search finds what <c>ToUnicode</c> says, and removal cuts the
/// string at the code boundaries the codespace defines.
/// </summary>
public class CMapTests
{
    private static CMap Parse(string body) => CMap.Parse(Encoding.ASCII.GetBytes(
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
        "/CMapName /Test def /CMapType 2 def\n" + body + "\nendcmap CMapName currentdict /CMap defineresource pop end end"));

    [Fact]
    public void MixedCodespace_SplitsOneAndTwoByteCodes()
    {
        var cmap = Parse("2 begincodespacerange <00> <80> <8140> <9FFC> endcodespacerange");
        byte[] text = { 0x41, 0x81, 0x40, 0x42, 0x9F, 0xFC };

        var codes = new List<(int, int)>();
        for (int offset = 0; offset < text.Length;)
        {
            var (code, length) = cmap.ReadCode(text, offset);
            codes.Add((code, length));
            offset += length;
        }

        Assert.Equal(new[] { (0x41, 1), (0x8140, 2), (0x42, 1), (0x9FFC, 2) }, codes);
        Assert.True(cmap.HasCodespace);
    }

    [Fact]
    public void ByteOutsideEveryCodespace_ConsumesTheShortestLength()
    {
        var cmap = Parse("1 begincodespacerange <0000> <7FFF> endcodespacerange");
        Assert.Equal((0x9000, 2), cmap.ReadCode(new byte[] { 0x90, 0x00, 0x01 }, 0));
        Assert.Equal((0x01, 1), cmap.ReadCode(new byte[] { 0x90, 0x00, 0x01 }, 2)); // only one byte left
    }

    [Fact]
    public void BfChar_HandlesSurrogatePairsLigaturesAndGlyphNames()
    {
        var cmap = Parse("""
            1 begincodespacerange <0000> <FFFF> endcodespacerange
            3 beginbfchar
            <0001> <D83DDE00>
            <0002> <006600660069>
            <0003> /Euro
            endbfchar
            """);

        Assert.Equal("\U0001F600", cmap.ToUnicode(1));
        Assert.Equal("ffi", cmap.ToUnicode(2));
        Assert.Equal("€", cmap.ToUnicode(3));
        Assert.Null(cmap.ToUnicode(4));
        Assert.Equal(2, cmap.CodeFor("ffi"));
        Assert.True(cmap.HasUnicodeMappings);
    }

    [Fact]
    public void BfRange_IncrementsWithCarry_AndAcceptsArrays()
    {
        var cmap = Parse("""
            1 begincodespacerange <00> <FF> endcodespacerange
            2 beginbfrange
            <10> <13> <00FE>
            <20> <22> [<0041> <0042> <0043>]
            endbfrange
            """);

        Assert.Equal("þ", cmap.ToUnicode(0x10));
        Assert.Equal("Ā", cmap.ToUnicode(0x12)); // FE + 2 carries into the high byte
        Assert.Equal("ā", cmap.ToUnicode(0x13));
        Assert.Equal("C", cmap.ToUnicode(0x22));
    }

    [Fact]
    public void CidCharAndRange_MapCodesToCids()
    {
        var cmap = Parse("""
            1 begincodespacerange <0000> <FFFF> endcodespacerange
            1 begincidchar <0005> 500 endcidchar
            1 begincidrange <0100> <01FF> 1000 endcidrange
            """);

        Assert.Equal(500, cmap.ToCid(5));
        Assert.Equal(1010, cmap.ToCid(0x010A));
        Assert.Equal(0, cmap.ToCid(0x0300)); // unmapped in a CMap that maps CIDs: notdef
    }

    [Fact]
    public void IdentityAndUnicode_ReadTwoByteCodes()
    {
        var identity = CMap.Identity();
        Assert.Equal((0x0102, 2), identity.ReadCode(new byte[] { 1, 2 }, 0));
        Assert.Equal((0x07, 1), identity.ReadCode(new byte[] { 7 }, 0));
        Assert.Equal(0x0102, identity.ToCid(0x0102));

        var unicode = CMap.Unicode();
        Assert.Equal("é", unicode.ToUnicode(0xE9));
        Assert.Equal("�", unicode.ToUnicode(0xD800)); // a lone surrogate is not text
    }

    [Fact]
    public void OversizedRanges_AndGarbage_AreIgnoredRatherThanThrowing()
    {
        var cmap = Parse("""
            1 begincodespacerange <00000000> <FFFFFFFF> endcodespacerange
            1 beginbfrange <00000000> <00FFFFFF> <0041> endbfrange
            1 beginbfchar <0041> endbfchar
            """);
        Assert.Null(cmap.ToUnicode(5));

        var garbage = CMap.Parse(Encoding.ASCII.GetBytes("<< >> ] [ ) ( beginbfchar endbfrange 7 7 7 usecmap"));
        Assert.False(garbage.HasUnicodeMappings);
    }
}
