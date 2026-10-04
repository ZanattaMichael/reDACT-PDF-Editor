using PdfEditor.Core;

namespace PdfEditor.Tests.Golden;

/// <summary>
/// Tests of the golden suite itself.
/// <para>
/// A regression net is only worth its runtime if it can go red, and the ways this kind of suite
/// fails silently are well known: a projection that reduces every document to the same string, a
/// comparator that treats a missing recording as a pass, an update mode that a failing run
/// triggers for itself. Each of those is a passing test suite that asserts nothing. The tests
/// below make the failure modes themselves observable.
/// </para>
/// </summary>
public class GoldenSuiteSelfTests
{
    /// <summary>
    /// The premise the whole design rests on: the writer does not produce the same bytes twice
    /// (every save stamps a fresh <c>/ID</c> and <c>/ModDate</c>), so a byte-level golden suite
    /// would be permanently flaky — but the projection of those bytes <em>is</em> stable. If this
    /// ever fails on the first assertion, the writer has become deterministic and comparing bytes
    /// would become an option worth revisiting.
    /// </summary>
    [Fact]
    public void RawBytes_AreNotReproducible_ButTheProjectionIs()
    {
        byte[] first = TestPdfs.MultiPage(2);
        byte[] second = TestPdfs.MultiPage(2);

        Assert.False(first.SequenceEqual(second),
            "The writer produced identical bytes twice. The golden suite compares projections because "
            + "it does not; if that has changed, revisit the decision rather than leaving this "
            + "comment to rot.");
        Assert.Equal(GoldenProjection.Describe(first), GoldenProjection.Describe(second));
    }

    /// <summary>
    /// The nondeterminism is exactly where it was expected to be — a timestamped <c>/ID</c> and
    /// <c>/ModDate</c> — and the projection records their presence without their value. A future
    /// change that made the projection include a timestamp would fail the test above; this one
    /// documents what is being suppressed.
    /// </summary>
    [Fact]
    public void Projection_RecordsPresenceOfTimestamps_ButNotTheirValue()
    {
        string projection = GoldenProjection.Describe(TestPdfs.MultiPage(1));

        Assert.Contains("has-ModDate=yes", projection, StringComparison.Ordinal);
        Assert.Contains("has-ID=yes", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("D:20", projection, StringComparison.Ordinal); // no PDF date literal
    }

    /// <summary>
    /// The negative case for the projection: it has to distinguish documents that differ in each
    /// of the dimensions the corpus exists to cover. A projection that collapsed them would let
    /// every golden pass forever.
    /// </summary>
    [Theory]
    [InlineData("font-type3-glyph-procedures", "font-type0-identity-h")]
    [InlineData("alpha-constant-and-blend", "alpha-luminosity-softmask")]
    [InlineData("alpha-nested-groups", "nested-forms-raw-4")]
    [InlineData("plain-multipage", "alpha-image-softmask")]
    public void Projection_DistinguishesCorpusDocuments(string left, string right)
    {
        var a = GoldenCorpus.Documents.Single(d => d.Name == left);
        var b = GoldenCorpus.Documents.Single(d => d.Name == right);
        Assert.NotEqual(GoldenProjection.Describe(a.Bytes), GoldenProjection.Describe(b.Bytes));
    }

    /// <summary>
    /// The negative case that matters most: a real, single-property change to a document must make
    /// the recorded golden stop matching. Rotating one page is about the smallest edit an operation
    /// can make, and the suite has to see it.
    /// </summary>
    [Fact]
    public void Comparison_FailsWhenTheDocumentChanges()
    {
        var doc = GoldenCorpus.Documents.Single(d => d.Name == "plain-multipage");
        string before = GoldenProjection.Describe(doc.Bytes);
        string after = GoldenProjection.Describe(PageTools.Rotate(doc.Bytes, new[] { 1 }, 90).Pdf);

        Assert.NotEqual(before, after);
        Assert.Contains("rotate=0", before, StringComparison.Ordinal);
        Assert.Contains("rotate=90", after, StringComparison.Ordinal);

        // ...and the comparator reports it as a mismatch against the real recording, with a diff.
        string? failure = GoldenFile.Compare(doc.Name, after, allowUpdate: false);
        Assert.NotNull(failure);
        Assert.Contains("no longer matches", failure, StringComparison.Ordinal);
        Assert.Contains("expected:", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a mismatch the failure message must carry the <em>whole</em> actual projection, not just
    /// the differing lines. The line diff is enough for a reproducible change a developer can re-run;
    /// it is not enough for the intermittent sighting (#88), where the failing CI run is the only
    /// evidence that will ever exist. If the full projection is in the log, the next investigator can
    /// diff it offline instead of trying to force the flake again.
    /// </summary>
    [Fact]
    public void Mismatch_DumpsTheWholeActualProjection_ForOfflineDiff()
    {
        var doc = GoldenCorpus.Documents.Single(d => d.Name == "plain-multipage");
        // A multi-line actual with a sentinel line that is NOT in the recording, so finding it in
        // the message proves the full body was dumped rather than only the diffed lines.
        const string sentinel = "sentinel-line-only-in-actual-not-in-recording";
        string actual = "== source ==\nfirst\n" + sentinel + "\nlast\n";

        string? failure = GoldenFile.Compare(doc.Name, actual, allowUpdate: false);

        Assert.NotNull(failure);
        Assert.Contains("Full actual projection", failure, StringComparison.Ordinal);
        Assert.Contains(sentinel, failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A transparency-specific negative case. Dropping the <c>/ExtGState</c> resource leaves a
    /// document that still opens, still validates, still has the same text and the same page
    /// geometry — and renders completely differently. Structural assertions miss it; the
    /// projection must not.
    /// </summary>
    [Fact]
    public void Projection_NoticesWhenTransparencyIsDropped()
    {
        var doc = GoldenCorpus.Documents.Single(d => d.Name == "alpha-constant-and-blend");
        byte[] stripped = WithoutExtGState(doc.Bytes);

        Assert.True(ExportValidator.Validate(stripped).IsValid,
            "the stripped document is still structurally valid — which is the point");
        Assert.NotEqual(GoldenProjection.Describe(doc.Bytes), GoldenProjection.Describe(stripped));
        Assert.Contains("BM=/Multiply", GoldenProjection.Describe(doc.Bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("BM=/Multiply", GoldenProjection.Describe(stripped), StringComparison.Ordinal);
    }

    private static byte[] WithoutExtGState(byte[] pdf)
    {
        var document = PdfEditor.Core.Pdf.PdfDocument.Open(pdf);
        document.GetPage(1).Resources!.Remove(PdfEditor.Core.Pdf.PdfName.ExtGState);
        return document.Save();
    }

    /// <summary>
    /// A missing recording is a failure, not a pass. The obvious way to write this comparator is
    /// to record whatever it sees the first time, which turns the first run after any change into
    /// a silent rubber stamp.
    /// </summary>
    [Fact]
    public void MissingRecording_IsAFailure_NotASilentRecord()
    {
        Assert.False(GoldenFile.UpdateRequested,
            $"{GoldenFile.UpdateVariable} is set in this environment; the suite would rewrite its "
            + "own recordings. Unset it before running the tests.");

        string? failure = GoldenFile.Compare("no-such-golden-" + Guid.NewGuid().ToString("N"), "anything\n");

        Assert.NotNull(failure);
        Assert.Contains("No golden recorded", failure, StringComparison.Ordinal);
        Assert.Contains(GoldenFile.UpdateVariable, failure, StringComparison.Ordinal);
        Assert.False(Directory.EnumerateFiles(GoldenFile.Directory, "no-such-golden-*.txt").Any(),
            "the comparator wrote a recording for a golden that did not exist");
    }

    /// <summary>
    /// Regenerating is opt-in <em>and</em> still fails the run, so a rewritten recording can never
    /// be mistaken for a green build.
    /// </summary>
    [Fact]
    public void UpdateMode_IsOptIn_AndStillFailsTheRun()
    {
        string name = "selftest-update-" + Guid.NewGuid().ToString("N");
        string path = GoldenFile.PathFor(name);
        try
        {
            Environment.SetEnvironmentVariable(GoldenFile.UpdateVariable, "1");
            Assert.True(GoldenFile.UpdateRequested);

            string? failure = GoldenFile.Compare(name, "recorded content\n");

            Assert.NotNull(failure);
            Assert.Contains("was rewritten", failure, StringComparison.Ordinal);
            Assert.Equal("recorded content\n", File.ReadAllText(path));

            // Even a matching comparison fails while the variable is set: the run is a
            // regeneration, not a verification.
            Assert.NotNull(GoldenFile.Compare(name, "recorded content\n"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoldenFile.UpdateVariable, null);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A regeneration run must not let a probe overwrite a real recording. The self-tests that
    /// deliberately feed the comparator non-matching content name a real corpus document, so
    /// without an opt-out `PDFEDITOR_UPDATE_GOLDENS=1` truncated `plain-multipage.txt` down to a
    /// probe's payload — turning the documented "update the goldens" command into a way to
    /// silently destroy them.
    /// </summary>
    [Fact]
    public void Regeneration_CannotBeTriggeredByAProbeThatExpectsAMismatch()
    {
        var doc = GoldenCorpus.Documents.Single(d => d.Name == "plain-multipage");
        string path = GoldenFile.PathFor(doc.Name);
        string before = File.ReadAllText(path);

        string? failure = GoldenFile.Compare(doc.Name, "obviously not the projection\n",
            allowUpdate: false);

        Assert.NotNull(failure);
        Assert.Equal(before, File.ReadAllText(path));
    }

    /// <summary>
    /// The comparator must be blind to line-ending style, or the suite is red for every developer
    /// on Windows and green for everyone else.
    /// </summary>
    [Fact]
    public void Comparison_IgnoresLineEndingStyle()
    {
        var doc = GoldenCorpus.Documents[0];
        string recorded = File.ReadAllText(GoldenFile.PathFor(doc.Name));
        Assert.Null(GoldenFile.Compare(doc.Name, recorded.ReplaceLineEndings("\r\n"), allowUpdate: false));
    }
}
