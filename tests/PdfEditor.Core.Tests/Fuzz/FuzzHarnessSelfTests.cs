using System.Text;

namespace PdfEditor.Tests.Fuzz;

/// <summary>
/// Proves the fuzz harness has teeth. A fuzzing suite that passes because it silently fails to
/// exercise anything is worse than no suite at all, so each of the three failure modes the harness
/// claims to catch — hangs, defect-class exceptions, unbounded allocation — is demonstrated here
/// against a deliberately broken operation. If any of these tests stops failing the harness has
/// gone blind, and <see cref="ParserFuzzTests"/> is no longer meaningful.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2201:Do not raise reserved exception types",
    Justification = "Raising the reserved defect-class exceptions on purpose is the whole point: " +
                    "these tests prove the harness rejects exactly those, and no substitute type would.")]
public class FuzzHarnessSelfTests
{
    private static readonly byte[] Input = Encoding.ASCII.GetBytes("%PDF-1.7\nnot a real document\n");

    [Fact]
    public void Harness_CatchesAHang()
    {
        var stop = new ManualResetEventSlim();
        try
        {
            var ex = Assert.Throws<FuzzContractViolationException>(() =>
                FuzzHarness.RunWithBudget("selftest-hang", Input, _ => stop.Wait(),
                    TimeSpan.FromMilliseconds(250)));
            Assert.Contains("a hang, not a handled failure", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            stop.Set(); // release the abandoned worker thread
        }
    }

    [Fact]
    public void Harness_CatchesADefectClassException()
    {
        var ex = Assert.Throws<FuzzContractViolationException>(() =>
            FuzzHarness.Run("selftest-nre", Input, _ => throw new NullReferenceException("boom")));
        Assert.Contains("defect-class exception", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NullReferenceException), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(KeyNotFoundException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(DivideByZeroException))]
    public void Harness_RejectsEveryDefectClassException(Type defect)
    {
        var ex = Assert.Throws<FuzzContractViolationException>(() =>
            FuzzHarness.Run("selftest-defect", Input,
                _ => throw (Exception)Activator.CreateInstance(defect)!));
        Assert.Contains("defect-class exception", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A bare <c>Exception</c> is untyped: the caller cannot branch on it, so it fails too.</summary>
    [Fact]
    public void Harness_RejectsAnUntypedException()
    {
        var ex = Assert.Throws<FuzzContractViolationException>(() =>
            FuzzHarness.Run("selftest-untyped", Input, _ => throw new Exception("something went wrong")));
        Assert.Contains("not a handled rejection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_CatchesUnboundedAllocation()
    {
        var ex = Assert.Throws<FuzzContractViolationException>(() =>
            FuzzHarness.Run("selftest-bomb", Input, _ =>
            {
                // ~640 MiB of managed allocation, past the 512 MiB budget, without ever holding
                // more than one buffer alive — exactly what a streaming decompression bomb looks like.
                for (int i = 0; i < 10; i++) GC.KeepAlive(new byte[64 * 1024 * 1024]);
            }));
        Assert.Contains("unbounded expansion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_CatchesAnEmptyExceptionMessage()
    {
        var ex = Assert.Throws<FuzzContractViolationException>(() =>
            FuzzHarness.Run("selftest-silent", Input, _ => throw new SilentException()));
        Assert.Contains("empty message", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_AcceptsARecoverableRejection()
    {
        var result = FuzzHarness.Run("selftest-ok", Input,
            _ => throw new InvalidOperationException("that is not a PDF"));
        Assert.Equal(FuzzOutcome.Rejected, result.Outcome);
        Assert.Equal(nameof(InvalidOperationException), result.ExceptionType);
    }

    [Fact]
    public void Harness_AcceptsSuccess()
    {
        var result = FuzzHarness.Run("selftest-success", Input, _ => { });
        Assert.Equal(FuzzOutcome.Completed, result.Outcome);
        Assert.Null(result.ExceptionType);
    }

    [Fact]
    public void Harness_UnwrapsWrappedExceptions()
    {
        var ex = Assert.Throws<FuzzContractViolationException>(() =>
            FuzzHarness.Run("selftest-wrapped", Input,
                _ => throw new AggregateException(new NullReferenceException("inner boom"))));
        Assert.Contains(nameof(NullReferenceException), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_TreatsEngineExceptionsAsHandledRejections()
    {
        var result = FuzzHarness.Run("selftest-engine", Input,
            _ => throw new PdfEditor.Core.Pdf.PdfFormatException("trailer not found"));
        Assert.Equal(FuzzOutcome.Rejected, result.Outcome);
    }

    /// <summary>Writes the offending bytes so a CI failure can be reproduced from the artifact.</summary>
    [Fact]
    public void Harness_DumpsTheOffendingInputAsAnArtifact()
    {
        byte[] distinctive = Encoding.ASCII.GetBytes("%PDF-1.7 artifact-dump-probe");
        var ex = Assert.Throws<FuzzContractViolationException>(() =>
            FuzzHarness.Run("selftest-artifact", distinctive, _ => throw new NullReferenceException("boom")));

        string path = Path.Combine(FuzzHarness.ArtifactDirectory, "selftest-artifact.pdf");
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Equal(distinctive, File.ReadAllBytes(path));
        Assert.Contains($"Corpus seed: 0x{FuzzHarness.CorpusSeed:X}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Recoverable by type, but useless to a caller — the harness must still reject it.</summary>
    private sealed class SilentException : InvalidOperationException
    {
        public SilentException() : base("") { }
    }
}
