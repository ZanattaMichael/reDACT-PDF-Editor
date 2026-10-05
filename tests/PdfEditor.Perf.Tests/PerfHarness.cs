using System.Diagnostics;
using System.Text;
using PdfEditor.Core.Pdf;
using PdfEditor.Core.Pdf.Fonts;
using Xunit;

namespace PdfEditor.Perf.Tests;

/// <summary>
/// Shared helpers for the performance guards: a deterministic PDF builder and a timing/assertion
/// harness. Budgets are deliberately generous (many times the observed local timings) so the tests
/// catch *gross* regressions — an accidental O(n²), a full-document reprocess where none is needed —
/// without flaking on slow or busy CI runners. A <c>PDF_EDITOR_PERF_SLACK</c> environment multiplier
/// lets an unusually slow environment relax every budget uniformly without touching the tests.
/// </summary>
internal static class PerfHarness
{
    /// <summary>
    /// Uniform multiplier applied to every budget. An explicit <c>PDF_EDITOR_PERF_SLACK</c> always
    /// wins. Otherwise it defaults to 1.0 locally but <see cref="CiSlack"/> on a CI runner (detected
    /// via the standard <c>CI</c> variable): shared runners are slow, and a solution-wide
    /// <c>dotnet test</c> runs the suites in parallel, so an un-slacked budget there measures
    /// contention rather than the code and fails at random.
    /// </summary>
    internal const double CiSlack = 4.0;

    public static readonly double Slack = ResolveSlack(
        Environment.GetEnvironmentVariable("PDF_EDITOR_PERF_SLACK"),
        IsCi(Environment.GetEnvironmentVariable("CI")));

    /// <summary>
    /// Pure slack resolution, split out from the ambient environment so every branch is testable:
    /// a valid positive <paramref name="envValue"/> wins outright, otherwise CI gets
    /// <see cref="CiSlack"/> and a local run stays strict at 1.0.
    /// </summary>
    internal static double ResolveSlack(string? envValue, bool isCi) =>
        double.TryParse(envValue, out var s) && s > 0 ? s : isCi ? CiSlack : 1.0;

    /// <summary>True on the common CI providers, which all set <c>CI</c> to a truthy value.</summary>
    internal static bool IsCi(string? ci) =>
        !string.IsNullOrEmpty(ci) &&
        !ci.Equals("false", StringComparison.OrdinalIgnoreCase) &&
        !ci.Equals("0", StringComparison.Ordinal);

    /// <summary>Builds a <paramref name="pages"/>-page PDF with ~<paramref name="wordsPerPage"/> words each.</summary>
    public static byte[] Doc(int pages, int wordsPerPage = 14)
    {
        var doc = PdfDocument.CreateNew();
        var font = PdfFont.Standard(StandardFonts.Helvetica);
        var fontRef = doc.MakeIndirect(font.Dictionary!);
        for (int p = 1; p <= pages; p++)
        {
            var page = doc.AddNewPage(595, 842);
            var name = PdfResources.Add(page.GetOrCreateResources(), PdfName.Font, "F", fontRef);
            var canvas = new ContentBuilder();
            canvas.BeginText().Font(name, 11).Leading(13).MoveText(56, 780);
            var line = new StringBuilder();
            int wordsOnLine = 0;
            for (int w = 0; w < wordsPerPage; w++)
            {
                line.Append('w').Append(p).Append('_').Append(w).Append(' ');
                if (++wordsOnLine == 12)
                {
                    canvas.ShowText(font.Encode(line.ToString())).Raw("T*\n");
                    line.Clear();
                    wordsOnLine = 0;
                }
            }
            if (line.Length > 0) canvas.ShowText(font.Encode(line.ToString()));
            canvas.EndText();
            page.AppendContent(canvas.ToArray());
        }
        return doc.Save();
    }

    /// <summary>Runs <paramref name="op"/> once to warm up, then returns the fastest of N timed runs
    /// (the minimum is the most stable estimator — least perturbed by GC/scheduling noise).</summary>
    public static double BestMs(int reps, Action op)
    {
        op(); // warm-up: JIT, first-touch allocations, caches
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            op();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    /// <summary>Asserts a measured time is within a budget (scaled by <see cref="Slack"/>).</summary>
    public static void AssertUnder(double actualMs, double budgetMs, string what)
    {
        double limit = budgetMs * Slack;
        Assert.True(actualMs <= limit,
            $"{what}: {actualMs:F1} ms exceeded the {limit:F0} ms budget " +
            $"(base {budgetMs:F0} ms × slack {Slack:F1}).");
    }
}
