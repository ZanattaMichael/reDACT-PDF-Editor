using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfEditor.Core;

namespace PdfEditor.Tests.Fuzz;

/// <summary>
/// Fuzz/regression suite for the decompression and parsing paths (issue #54).
/// <para>
/// The engine is a library that a browser extension hands untrusted files to, so the only
/// acceptable reaction to a malformed document is a <em>handled</em> one: a clean result, or a
/// recoverable exception the native host can turn into an error envelope. A hang, a decompression
/// bomb, or a <see cref="NullReferenceException"/> from deep inside a parser are all failures.
/// <see cref="FuzzHarness"/> enforces exactly that, with a wall-clock budget per case so a hang
/// surfaces as a red test rather than a stalled CI job.
/// </para>
/// <para>
/// The corpus is generated in-repo from a fixed seed, so a failure names a case that can be
/// re-run verbatim, and the offending bytes are dumped to <c>fuzz-artifacts/</c>.
/// </para>
/// </summary>
public class ParserFuzzTests
{
    /// <summary>
    /// Mutations generated per seed document. Kept small enough that the whole suite is a
    /// few seconds; <c>PDFEDITOR_FUZZ_MUTATIONS</c> raises it for a deeper (e.g. nightly) run
    /// without making the default CI pass slow.
    /// </summary>
    private static readonly int MutationsPerSeed =
        int.TryParse(Environment.GetEnvironmentVariable("PDFEDITOR_FUZZ_MUTATIONS"),
            CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 40;

    private static readonly RectRegion Region = new(1, 20, 20, 120, 40);

    /// <summary>
    /// The entry points a hostile document reaches. Read-only inspection, rendering, text
    /// extraction and redaction between them cover the xref/object parser, the stream filters,
    /// the image decoders and the content-stream tokeniser.
    /// </summary>
    private static readonly (string Name, Action<byte[]> Run)[] Operations =
    {
        ("GetInfo", pdf => PdfInspector.GetInfo(pdf)),
        ("RenderPagePng", pdf => PageRenderer.RenderPagePng(pdf, 1, 72)),
        ("FindText", pdf => TextTools.FindText(pdf, "payload")),
        ("Redact", pdf => Redactor.Redact(pdf, new[] { Region })),
        ("SafetyScan", pdf => PdfSafety.Scan(pdf)),
    };

    /// <summary>Inspection and text extraction only — the cheap pair used for the bulk mutation sweep.</summary>
    private static readonly (string Name, Action<byte[]> Run)[] CheapOperations =
    {
        ("GetInfo", pdf => PdfInspector.GetInfo(pdf)),
        ("FindText", pdf => TextTools.FindText(pdf, "payload")),
    };

    [Fact]
    public void ContentStreamFilters_MalformedStreams_FailHandled()
    {
        var results = RunCorpus(FuzzCorpus.FilterCases, Operations);
        AssertParserWasActuallyExercised(results, minimumRejections: 20);
    }

    [Fact]
    public void DocumentStructure_Malformed_FailsHandled()
    {
        var results = RunCorpus(FuzzCorpus.StructureCases, Operations);
        AssertParserWasActuallyExercised(results, minimumRejections: 20);
    }

    [Fact]
    public void MutatedValidDocuments_FailHandled()
    {
        var cases = FuzzCorpus.MutationSeeds.SelectMany(seed => FuzzCorpus.Mutate(seed, MutationsPerSeed)).ToList();
        Assert.Equal(FuzzCorpus.MutationSeeds.Count * MutationsPerSeed, cases.Count);

        var results = RunCorpus(cases, CheapOperations);
        AssertParserWasActuallyExercised(results, minimumRejections: 20);
    }

    /// <summary>
    /// The mutation stream must be a pure function of <see cref="FuzzHarness.CorpusSeed"/>: if it
    /// were not, a CI failure could not be reproduced locally and the artifacts would be useless.
    /// </summary>
    [Fact]
    public void Mutations_AreDeterministic_AcrossRuns()
    {
        var seed = FuzzCorpus.MutationSeeds[0];
        var first = FuzzCorpus.Mutate(seed, 12).ToList();
        var second = FuzzCorpus.Mutate(seed, 12).ToList();

        Assert.Equal(12, first.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Name, second[i].Name);
            Assert.Equal(first[i].Bytes, second[i].Bytes);
        }
        // ...and it must actually change the bytes, or the "fuzzing" is just re-parsing a good file.
        Assert.Contains(first, c => !c.Bytes.SequenceEqual(seed.Bytes));
    }

    /// <summary>
    /// The structure and mutation corpora contain nothing but hand-written bytes, so their
    /// contents are pinned: if this hash moves without the corpus being deliberately edited,
    /// something has crept in that varies between runs or machines (a writer-produced document, a
    /// timestamp, an unseeded PRNG) and every future failure would be irreproducible.
    /// </summary>
    [Fact]
    public void Corpus_IsBitForBitReproducible()
    {
        const string expected = "A5561E31E730D16F626165C17362E6B57E3AC97D000F41ED25B244BB7603ED78";
        string actual = HashOf(FuzzCorpus.StructureCases
            .Concat(FuzzCorpus.MutationSeeds.SelectMany(s => FuzzCorpus.Mutate(s, 40))));

        Assert.True(expected == actual,
            $"The pinned corpus hash changed: expected {expected}, got {actual}. If you edited the " +
            "corpus on purpose, update the constant. If you did not, the corpus has become " +
            "non-reproducible and its failures can no longer be reproduced from the recorded seed.");
    }

    private static string HashOf(IEnumerable<FuzzCase> cases)
    {
        using var sha = SHA256.Create();
        foreach (var c in cases)
        {
            byte[] name = Encoding.UTF8.GetBytes(c.Name);
            sha.TransformBlock(name, 0, name.Length, null, 0);
            sha.TransformBlock(c.Bytes, 0, c.Bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>
    /// Regression test for the robustness bug this fuzzing campaign found. Every one of these
    /// inputs used to escape the engine as a bare <see cref="NullReferenceException"/>,
    /// <see cref="IndexOutOfRangeException"/> or <see cref="InvalidCastException"/> from inside
    /// the filter decoders and canvas processor of iText, the PDF library reDACT used before 3.0 — a failure the host could only report as
    /// "Object reference not set to an instance of an object". They must now surface as a typed
    /// failure that says the document is malformed, with the original preserved for diagnosis.
    /// </summary>
    [Theory]
    [InlineData("flate-truncated-half")]      // was InvalidCastException
    [InlineData("lzw-truncated")]             // was InvalidCastException
    [InlineData("lzw-all-ones")]              // was NullReferenceException
    [InlineData("runlength-truncated-literal")] // was IndexOutOfRangeException
    public void MalformedFilters_SurfaceAsTypedFailures_NotDefectExceptions(string caseName)
    {
        byte[] pdf = FuzzCorpus.FilterCases.Single(c => c.Name == caseName).Bytes;

        foreach (var op in new Action[]
                 {
                     () => TextTools.FindText(pdf, "payload"),
                     () => Redactor.Redact(pdf, new[] { Region }),
                 })
        {
            var ex = Assert.Throws<InvalidDataException>(op);
            Assert.Contains("malformed or corrupt", ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ex.InnerException); // the original defect is kept for diagnosis
        }
    }

    /// <summary>
    /// A stream whose <c>/Length</c> names an object that does not exist used to escape as a
    /// <see cref="NullReferenceException"/>. The stream's extent is still recoverable from its
    /// <c>endstream</c> keyword — which is how the engine now reads it — so the document is simply
    /// readable, and the search finds the text it holds.
    /// </summary>
    [Fact]
    public void StreamLengthNamingAMissingObject_IsReadFromItsEndstream()
    {
        byte[] pdf = FuzzCorpus.FilterCases.Single(c => c.Name == "flate-length-indirect-missing").Bytes;
        Assert.NotEmpty(TextTools.FindText(pdf, "payload"));
        Assert.NotNull(Redactor.Redact(pdf, new[] { Region }).Pdf);
    }

    /// <summary>
    /// Regression test for the most serious finding of this campaign: a form XObject that lists
    /// itself in its own <c>/Resources /XObject</c> sent iText's content processor (pre-3.0) into unbounded
    /// recursion, and the resulting <see cref="StackOverflowException"/> is uncatchable on .NET —
    /// it killed the whole test host ("Test Run Aborted") and would equally kill the native host
    /// process of anyone who opened such a file. It must now be refused up front.
    /// </summary>
    [Fact]
    public void SelfReferentialFormXObject_IsRefused_RatherThanCrashingTheProcess()
    {
        byte[] pdf = FuzzCorpus.StructureCases.Single(c => c.Name == "page-resources-cycle").Bytes;

        var find = Assert.Throws<InvalidDataException>(() => TextTools.FindText(pdf, "payload"));
        Assert.Contains("draws itself", find.Message, StringComparison.Ordinal);
        var redact = Assert.Throws<InvalidDataException>(() => Redactor.Redact(pdf, new[] { Region }));
        Assert.Contains("draws itself", redact.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The acyclic-but-unbounded variant of the same crash: 200 levels of form XObject, each
    /// drawing the next. Refused; a plausible four-level document still works.
    /// </summary>
    [Fact]
    public void AbsurdlyDeepFormNesting_IsRefused_ButPlausibleNestingStillWorks()
    {
        byte[] tooDeep = FuzzCorpus.StructureCases.Single(c => c.Name == "form-xobject-nesting-200").Bytes;
        var ex = Assert.Throws<InvalidDataException>(() => TextTools.FindText(tooDeep, "payload"));
        Assert.Contains("levels deep", ex.Message, StringComparison.Ordinal);

        byte[] reasonable = FuzzCorpus.StructureCases.Single(c => c.Name == "form-xobject-nesting-4").Bytes;
        Assert.Empty(TextTools.FindText(reasonable, "payload"));
    }

    /// <summary>
    /// A trailer whose <c>/Root</c> is a string used to escape as a bare
    /// <see cref="InvalidCastException"/> from the previous engine's document constructor, on every
    /// entry point at once. The catalog object itself is intact, so the repair path finds it by
    /// its <c>/Type /Catalog</c> — as other readers do — and the document opens with its page.
    /// </summary>
    [Fact]
    public void TrailerRootThatIsNotADictionary_IsRepairedFromTheCatalogObject()
    {
        byte[] pdf = FuzzCorpus.StructureCases.Single(c => c.Name == "trailer-root-is-a-string").Bytes;
        Assert.Equal(1, PdfInspector.GetInfo(pdf).PageCount);
    }

    /// <summary>A document with no catalog anywhere is refused with an explicable error, not a crash.</summary>
    [Fact]
    public void DocumentWithNoCatalogAnywhere_IsRefusedWithAnExplicableError()
    {
        byte[] pdf = System.Text.Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Foo 1 >>\nendobj\ntrailer\n<< /Root (nope) /Size 2 >>\n%%EOF\n");
        var ex = Assert.Throws<InvalidDataException>(() => PdfInspector.GetInfo(pdf));
        Assert.Contains("malformed or corrupt", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decompression bomb deserves its own named test: 8 MiB of zeros in a few hundred bytes
    /// of Flate. Whatever the engine does with it, it must stay inside the time and allocation
    /// budgets rather than expanding without bound.
    /// </summary>
    [Fact]
    public void DecompressionBomb_StaysWithinBudget()
    {
        var bombs = FuzzCorpus.FilterCases.Where(c => c.Name.Contains("bomb", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(bombs);
        var results = RunCorpus(bombs, Operations);
        Assert.NotEmpty(results);
    }

    /// <summary>Runs every operation over every case, collecting *all* violations before failing.</summary>
    private static List<FuzzResult> RunCorpus(
        IReadOnlyList<FuzzCase> cases, (string Name, Action<byte[]> Run)[] operations)
    {
        var results = new List<FuzzResult>();
        var violations = new List<string>();

        foreach (var fuzzCase in cases)
        {
            foreach (var (opName, run) in operations)
            {
                string label = fuzzCase.Name + "." + opName;
                try
                {
                    results.Add(FuzzHarness.Run(label, fuzzCase.Bytes, run));
                }
                catch (FuzzContractViolationException ex)
                {
                    violations.Add(ex.Message);
                    if (violations.Count >= 10) goto done; // enough evidence; keep the log readable
                }
            }
        }
    done:
        if (violations.Count > 0)
        {
            var report = new StringBuilder()
                .Append(violations.Count)
                .Append(" fuzz case(s) violated the robustness contract (")
                .Append(results.Count)
                .AppendLine(" ran cleanly before this point):")
                .AppendLine();
            foreach (string v in violations) report.AppendLine(v).AppendLine();
            Assert.Fail(report.ToString());
        }
        return results;
    }

    /// <summary>
    /// Guards against the worst failure mode a fuzz suite has: passing because it never really
    /// reached the code under test. A corpus of deliberately broken documents that the engine
    /// *never* rejects means the inputs stopped being hostile (or the operations stopped running),
    /// and the suite has quietly become decoration.
    /// </summary>
    private static void AssertParserWasActuallyExercised(IReadOnlyList<FuzzResult> results, int minimumRejections)
    {
        Assert.NotEmpty(results);
        int rejected = results.Count(r => r.Outcome == FuzzOutcome.Rejected);
        Assert.True(rejected >= minimumRejections,
            $"Only {rejected} of {results.Count} fuzz runs were rejected by the engine (expected at " +
            $"least {minimumRejections}). Either the corpus is no longer hostile or the operations " +
            "are no longer reaching the parser — in both cases this suite is no longer testing anything.");
    }
}
