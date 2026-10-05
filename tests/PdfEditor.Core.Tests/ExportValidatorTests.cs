using PdfEditor.Core;

namespace PdfEditor.Tests;

/// <summary>
/// Every defect class the export validator claims to detect is proven here against a
/// deliberately corrupt document, and the well-formed control documents are proven to pass
/// clean — so the suite fails loudly if the validator ever degrades into "everything is fine".
/// </summary>
public class ExportValidatorTests
{
    private static ValidationReport Validate(byte[] pdf) => ExportValidator.Validate(pdf);

    private static void AssertFlags(byte[] pdf, string code, ValidationSeverity severity)
    {
        var report = Validate(pdf);
        var finding = report.Findings.FirstOrDefault(f => f.Code == code);
        Assert.True(finding != null,
            $"expected finding {code}; got: {report.ToLogText()}");
        Assert.Equal(severity, finding!.Severity);
        // "Actionable" means the finding says where to look and what is wrong.
        Assert.False(string.IsNullOrWhiteSpace(finding.Location));
        Assert.True(finding.Message.Length > 20, $"message is not actionable: {finding.Message}");
        if (severity == ValidationSeverity.Error) Assert.False(report.IsValid);
    }

    // ------------------------------------------------------- well-formed documents pass clean

    public static TheoryData<string, byte[]> CleanDocuments() => new()
    {
        { "hand-written raw", CorruptPdfs.WellFormed() },
        { "engine text page", TestPdfs.WithText(("Hello world", 72, 700, 14)) },
        { "multi page", TestPdfs.MultiPage(3) },
        { "raster image", TestPdfs.WithImage(100, 400, 120, 80) },
        { "inline image", TestPdfs.WithInlineImage(100, 400, 120, 80) },
        { "form xobject", TestPdfs.WithForm("inner", 100, 400, 120, 80) },
        { "acroform text field", TestPdfs.WithTextField("name", "Jane") },
        { "hidden data", TestPdfs.WithHiddenData() },
        { "chrome-style ctm", TestPdfs.ChromeStyleLeftoverCtm() },
        { "indirect stream length", CorruptPdfs.IndirectStreamLength() },
        { "filter array", CorruptPdfs.HexEncodedContentStream() },
        { "xref stream + object streams",
            CorruptPdfs.FullyCompressed(TestPdfs.WithText(("compressed", 72, 700, 12))) },
    };

    [Theory]
    [MemberData(nameof(CleanDocuments))]
    public void WellFormedDocumentsReportNoErrors(string label, byte[] pdf)
    {
        var report = Validate(pdf);
        Assert.True(report.IsValid, $"{label} should validate clean, got: {report.ToLogText()}");
        Assert.Equal(0, report.ErrorCount);
    }

    [Fact]
    public void CleanDocumentLogTextSaysSoExplicitly()
    {
        var report = Validate(TestPdfs.WithText(("Hello", 72, 700, 12)));
        Assert.Empty(report.Findings);
        Assert.Contains("no problems", report.ToLogText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, report.WarningCount);
        Assert.False(report.Has("PDF001"));
    }

    [Fact]
    public void ExportedDocumentsFromTheEditingPipelineValidateClean()
    {
        byte[] source = TestPdfs.WithText(("Confidential total 12345", 72, 700, 14));
        byte[] redacted = Redactor.Redact(source, new[] { new RectRegion(1, 70, 690, 300, 30) }).Pdf;
        Assert.True(Validate(redacted).IsValid, Validate(redacted).ToLogText());

        byte[] filled = FormTools.FillFields(TestPdfs.WithTextField("name"),
            new Dictionary<string, string> { ["name"] = "Jane" }, flatten: true).Pdf;
        Assert.True(Validate(filled).IsValid, Validate(filled).ToLogText());
    }

    // ------------------------------------------------------- file-level structure

    [Fact]
    public void FlagsMissingPdfHeader() =>
        AssertFlags(CorruptPdfs.MissingHeader(), "PDF001", ValidationSeverity.Error);

    [Fact]
    public void FlagsHeaderThatIsNotAtTheStartOfTheFile()
    {
        AssertFlags(CorruptPdfs.HeaderNotAtStart(), "PDF001", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.HeaderNotAtStart()).Findings.First(f => f.Code == "PDF001");
        Assert.Contains("offset 26", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsMissingEofMarker() =>
        AssertFlags(CorruptPdfs.MissingEof(), "PDF002", ValidationSeverity.Error);

    [Fact]
    public void FlagsMissingStartXrefKeyword() =>
        AssertFlags(CorruptPdfs.MissingStartXref(), "PDF003", ValidationSeverity.Error);

    [Fact]
    public void FlagsStartXrefWithNoOffsetAfterIt() =>
        AssertFlags(CorruptPdfs.StartXrefWithoutOffset(), "PDF003", ValidationSeverity.Error);

    [Fact]
    public void FlagsMalformedXrefSubsectionHeader() =>
        AssertFlags(CorruptPdfs.MalformedXrefSubsection(), "PDF003", ValidationSeverity.Error);

    [Fact]
    public void FlagsXrefEntryPointingPastEndOfFile()
    {
        AssertFlags(CorruptPdfs.XrefEntryPastEndOfFile(), "PDF004", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.XrefEntryPastEndOfFile()).Findings.First(f => f.Code == "PDF004");
        Assert.Contains("outside", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsStartXrefPointingPastEndOfFile() =>
        AssertFlags(CorruptPdfs.StartXrefOutOfRange(), "PDF003", ValidationSeverity.Error);

    [Fact]
    public void FlagsStartXrefPointingAtSomethingThatIsNotAnXref() =>
        AssertFlags(CorruptPdfs.StartXrefNotAnXref(), "PDF003", ValidationSeverity.Error);

    [Fact]
    public void FlagsXrefEntryThatDoesNotPointAtItsObject()
    {
        AssertFlags(CorruptPdfs.ShiftedXrefEntry(), "PDF004", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.ShiftedXrefEntry()).Findings.First(f => f.Code == "PDF004");
        // The maintainer must be able to go straight to the entry and the offset it names.
        Assert.Contains("3", finding.Location, StringComparison.Ordinal);
        Assert.Contains("offset", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlagsWhenTheParserHadToRebuildTheXref() =>
        AssertFlags(CorruptPdfs.StartXrefOutOfRange(), "PDF005", ValidationSeverity.Warning);

    [Fact]
    public void FlagsBytesThatAreNotAPdfAtAll()
    {
        AssertFlags(CorruptPdfs.NotAPdf(), "PDF010", ValidationSeverity.Error);
        // A file that cannot be opened must not silently pass the remaining checks.
        Assert.False(Validate(CorruptPdfs.NotAPdf()).IsValid);
    }

    [Fact]
    public void FlagsEmptyOutput() =>
        AssertFlags(Array.Empty<byte>(), "PDF001", ValidationSeverity.Error);

    // ------------------------------------------------------- catalog and page tree

    [Fact]
    public void FlagsRootThatIsNotACatalog() =>
        // The reader takes whatever /Root names as the catalog, so this surfaces
        // as "the catalog has no page tree" — the finding says so, and points at trailer /Root.
        AssertFlags(CorruptPdfs.RootIsNotACatalog(), "PDF011", ValidationSeverity.Error);

    [Fact]
    public void FlagsCatalogWithoutAPageTree() =>
        AssertFlags(CorruptPdfs.CatalogWithoutPageTree(), "PDF011", ValidationSeverity.Error);

    [Fact]
    public void FlagsDocumentWithNoPages() =>
        AssertFlags(CorruptPdfs.NoPages(), "PDF020", ValidationSeverity.Error);

    [Fact]
    public void FlagsPageTreeCountThatDisagreesWithTheActualPages() =>
        AssertFlags(CorruptPdfs.PageCountMismatch(), "PDF012", ValidationSeverity.Warning);

    [Fact]
    public void FlagsMissingMediaBox() =>
        AssertFlags(CorruptPdfs.MissingMediaBox(), "PDF021", ValidationSeverity.Error);

    [Fact]
    public void FlagsDegenerateMediaBox()
    {
        AssertFlags(CorruptPdfs.DegenerateMediaBox(), "PDF021", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.DegenerateMediaBox()).Findings.First(f => f.Code == "PDF021");
        Assert.Contains("page 1", finding.Location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlagsMediaBoxThatIsNotFourNumbers() =>
        AssertFlags(CorruptPdfs.MediaBoxWithThreeNumbers(), "PDF021", ValidationSeverity.Error);

    [Fact]
    public void FlagsPageDictionaryWithoutTypePage() =>
        AssertFlags(CorruptPdfs.PageWithoutType(), "PDF022", ValidationSeverity.Warning);

    // ------------------------------------------------------- streams

    [Fact]
    public void FlagsStreamWhoseDeclaredLengthIsWrong()
    {
        AssertFlags(CorruptPdfs.WrongStreamLength(), "PDF031", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.WrongStreamLength()).Findings.First(f => f.Code == "PDF031");
        Assert.Contains("5", finding.Message, StringComparison.Ordinal);       // the declared length
        Assert.Contains("endstream", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsStreamThatIsNeverTerminated()
    {
        AssertFlags(CorruptPdfs.UnterminatedStream(), "PDF031", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.UnterminatedStream()).Findings.First(f => f.Code == "PDF031");
        Assert.Contains("unterminated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlagsStreamThatFailsToDecode()
    {
        AssertFlags(CorruptPdfs.UndecodableStream(), "PDF030", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.UndecodableStream()).Findings.First(f => f.Code == "PDF030");
        Assert.Contains("FlateDecode", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlagsUnknownStreamFilter()
    {
        AssertFlags(CorruptPdfs.UnknownStreamFilter(), "PDF032", ValidationSeverity.Error);
        var finding = Validate(CorruptPdfs.UnknownStreamFilter()).Findings.First(f => f.Code == "PDF032");
        Assert.Contains("SuperDecode", finding.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- AcroForm invariants

    [Fact]
    public void FlagsFormFieldWithoutAnAppearanceStream() =>
        AssertFlags(CorruptPdfs.FormFieldWithoutAppearance(TestPdfs.WithTextField("name", "Jane")),
            "PDF040", ValidationSeverity.Warning);

    [Fact]
    public void FlagsFormFieldWidgetMissingFromEveryPage() =>
        AssertFlags(CorruptPdfs.FormFieldOrphanedFromPage(TestPdfs.WithTextField("name", "Jane")),
            "PDF041", ValidationSeverity.Warning);

    [Fact]
    public void DoesNotFlagAFormFieldThatIsCorrectlyAttachedToItsPage()
    {
        // The negative case for PDF040/PDF041: a field added the right way, widget on the page
        // with a generated appearance, must produce no form findings at all.
        byte[] pdf = FormTools.AddTextField(TestPdfs.WithText(("form", 72, 700, 12)), 1,
            new RectRegion(1, 100, 500, 200, 24), "fullName", "Jane").Pdf;
        var report = Validate(pdf);
        Assert.False(report.Has("PDF040"), report.ToLogText());
        Assert.False(report.Has("PDF041"), report.ToLogText());
        Assert.True(report.IsValid, report.ToLogText());
    }

    // ------------------------------------------------------- reporting surface

    [Fact]
    public void LogTextListsSeverityCodeAndLocationForEveryFinding()
    {
        var report = Validate(CorruptPdfs.UndecodableStream());
        string log = report.ToLogText();
        foreach (var finding in report.Findings)
        {
            Assert.Contains(finding.Code, log, StringComparison.Ordinal);
            Assert.Contains(finding.Location, log, StringComparison.Ordinal);
            Assert.Contains(finding.Severity.ToString().ToUpperInvariant(), log, StringComparison.Ordinal);
        }
        Assert.True(report.ErrorCount >= 1);
    }

    [Fact]
    public void ValidatesEncryptedDocumentsWithTheirPassword()
    {
        byte[] encrypted = Encryptor.Encrypt(TestPdfs.WithText(("secret", 72, 700, 12)), "pw", "owner");
        var report = ExportValidator.Validate(encrypted, "pw");
        Assert.True(report.IsValid, report.ToLogText());
    }
}
