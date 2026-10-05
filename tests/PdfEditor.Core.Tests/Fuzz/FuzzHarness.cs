using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PdfEditor.Tests.Fuzz;

/// <summary>What running one operation over one hostile input produced.</summary>
internal enum FuzzOutcome
{
    /// <summary>The operation ran to completion. Tolerating malformed input is allowed.</summary>
    Completed,
    /// <summary>The operation refused the input with a recoverable, typed exception.</summary>
    Rejected,
}

/// <summary>The verdict for a single fuzz case, used by the tests to summarise a run.</summary>
internal sealed record FuzzResult(string Case, FuzzOutcome Outcome, string? ExceptionType, double Milliseconds);

/// <summary>
/// Thrown when a fuzz case violates the robustness contract. Carries the offending bytes so the
/// failure is reproducible from the test artifacts, not just from the seed.
/// </summary>
internal sealed class FuzzContractViolationException : Exception
{
    public FuzzContractViolationException(string message) : base(message) { }
}

/// <summary>
/// The enforcement half of the fuzzing suite. Every case runs through <see cref="Run"/>, which
/// holds the parser to a single explicit contract:
/// <list type="bullet">
///   <item>it may succeed, or</item>
///   <item>it may fail with a <em>recoverable</em> exception carrying a non-empty message,</item>
/// </list>
/// and nothing else. Specifically it may not
/// <list type="bullet">
///   <item>hang — every case runs on its own thread under a wall-clock budget and the budget's
///     expiry is a test failure, not a stalled CI job;</item>
///   <item>fail with a defect-class exception (<see cref="NullReferenceException"/>,
///     <see cref="IndexOutOfRangeException"/>, <see cref="KeyNotFoundException"/>, …) — those are
///     unhandled crashes that happen to be catchable on .NET, and they mean the parser walked off
///     the end of something rather than validating it;</item>
///   <item>allocate without bound — the per-case allocation budget catches decompression bombs.</item>
/// </list>
/// Every violation writes the exact input bytes to <see cref="ArtifactDirectory"/> and names both
/// the file and the corpus seed in the failure message, so a red run is always reproducible.
/// <para>
/// Known limitation: a <see cref="StackOverflowException"/> is process-fatal on .NET and cannot be
/// intercepted here — an input that sends the engine into unbounded recursion aborts the whole test
/// run instead of failing one case. That is still a loud, unmissable failure (the run reports
/// "Test Run Aborted" and the recursive stack is printed), and it is exactly how the
/// self-referential form XObject that <c>PdfStructureGuard</c> now rejects was found.
/// </para>
/// </summary>
internal static class FuzzHarness
{
    /// <summary>
    /// The fixed PRNG seed for the whole mutation corpus. Printed in every failure message; change
    /// it only deliberately, because changing it changes every generated input.
    /// </summary>
    public const int CorpusSeed = 0x5EED54;

    /// <summary>Wall-clock budget for one operation over one input. Exceeding it fails the test.</summary>
    public static readonly TimeSpan CaseBudget = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("PDFEDITOR_FUZZ_CASE_SECONDS"),
            CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 20);

    /// <summary>
    /// Managed-allocation budget for one operation over one input (512 MiB). A parser that streams
    /// or rejects a decompression bomb stays far below it; one that materialises the bomb does not.
    /// </summary>
    public const long AllocationBudgetBytes = 512L * 1024 * 1024;

    /// <summary>Where offending inputs are dumped. Emitted next to the test binary so CI can collect it.</summary>
    public static string ArtifactDirectory { get; } = Path.Combine(
        Path.GetDirectoryName(typeof(FuzzHarness).Assembly.Location) ?? ".", "fuzz-artifacts");

    /// <summary>
    /// Exception types that mean "this code has a defect", not "this input was rejected". They are
    /// what a parser throws when it walks off the end of a buffer, dereferences a dictionary entry
    /// that was not there, or casts an object it never checked — i.e. an unhandled crash that .NET
    /// happens to make catchable. A caller cannot distinguish one of these from a genuine bug in
    /// the engine, and "Object reference not set to an instance of an object" is not an error
    /// message a user can act on.
    /// <para>
    /// A deny-list rather than an allow-list, because the engine legitimately surfaces typed
    /// failures from several sources (the engine's <c>PdfFormatException</c>, PDFtoImage's
    /// <c>PdfInvalidFormatException</c>, …) and new ones must not silently count as violations.
    /// </para>
    /// </summary>
    private static readonly Type[] DefectClassExceptions =
    {
        typeof(NullReferenceException),
        typeof(IndexOutOfRangeException),
        typeof(InvalidCastException),
        typeof(KeyNotFoundException),
        typeof(DivideByZeroException),
        typeof(NotImplementedException),
        typeof(AccessViolationException),
        typeof(OutOfMemoryException),
        typeof(StackOverflowException),
        typeof(System.Runtime.InteropServices.SEHException),
        typeof(BadImageFormatException),
    };

    /// <summary>
    /// Bare, meaningless exception types. Throwing <c>new Exception("…")</c> from a parser gives a
    /// caller nothing to branch on, so it is treated as a violation just like a defect class.
    /// </summary>
    private static readonly Type[] UntypedExceptions =
    {
        typeof(Exception), typeof(SystemException), typeof(ApplicationException),
    };

    private static bool IsRecoverable(Exception ex) =>
        !DefectClassExceptions.Any(t => t.IsInstanceOfType(ex)) &&
        !UntypedExceptions.Contains(ex.GetType());

    /// <summary>
    /// Runs <paramref name="operation"/> over <paramref name="input"/> under the full contract and
    /// returns what happened. Throws <see cref="FuzzContractViolationException"/> — never the
    /// operation's own exception — when the contract is broken.
    /// </summary>
    public static FuzzResult Run(string caseName, byte[] input, Action<byte[]> operation)
        => RunWithBudget(caseName, input, operation, CaseBudget);

    /// <summary>
    /// <see cref="Run"/> with an explicit time budget. Exposed so the harness's own tests can prove
    /// the timeout fires without making the suite wait the full production budget.
    /// </summary>
    public static FuzzResult RunWithBudget(string caseName, byte[] input, Action<byte[]> operation,
        TimeSpan budget)
    {
        Exception? thrown = null;
        long allocated = 0;
        var clock = Stopwatch.StartNew();

        // A dedicated thread, not the thread pool: a hung case must not starve the pool, and
        // Join(timeout) is the only way to put a hard wall-clock bound around code that may loop
        // forever. .NET cannot abort a runaway thread, so the thread is a background thread and is
        // deliberately abandoned on timeout — the process still exits when the run finishes.
        var worker = new Thread(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try { operation(input); }
            catch (Exception ex) { thrown = ex; }
            finally { allocated = GC.GetAllocatedBytesForCurrentThread() - before; }
        })
        { IsBackground = true, Name = "fuzz:" + caseName };

        worker.Start();
        bool finished = worker.Join(budget);
        clock.Stop();

        if (!finished)
            throw Violation(caseName, input,
                $"did not finish within {budget.TotalSeconds:F1}s — a hang, not a handled failure");

        if (thrown != null)
        {
            var ex = Unwrap(thrown);
            if (!IsRecoverable(ex))
                throw Violation(caseName, input,
                    $"threw {ex.GetType().FullName} — a defect-class exception, not a handled rejection " +
                    "(the engine walked off the end of something instead of validating it)." +
                    Environment.NewLine + ex);
            if (string.IsNullOrWhiteSpace(ex.Message))
                throw Violation(caseName, input, $"threw {ex.GetType().FullName} with an empty message");
        }

        if (allocated > AllocationBudgetBytes)
            throw Violation(caseName, input,
                $"allocated {allocated / (1024 * 1024)} MiB, over the " +
                $"{AllocationBudgetBytes / (1024 * 1024)} MiB budget — unbounded expansion");

        return thrown == null
            ? new FuzzResult(caseName, FuzzOutcome.Completed, null, clock.Elapsed.TotalMilliseconds)
            : new FuzzResult(caseName, FuzzOutcome.Rejected, Unwrap(thrown).GetType().Name,
                clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>Unwraps the reflection/aggregate wrappers so the real failure is judged.</summary>
    private static Exception Unwrap(Exception ex) => ex switch
    {
        TargetInvocationException { InnerException: { } inner } => Unwrap(inner),
        AggregateException { InnerExceptions.Count: 1 } agg => Unwrap(agg.InnerExceptions[0]),
        _ => ex,
    };

    /// <summary>Dumps the offending input and builds the failure message that points at it.</summary>
    private static FuzzContractViolationException Violation(string caseName, byte[] input, string what)
    {
        string path = DumpArtifact(caseName, input);
        return new FuzzContractViolationException(
            $"Fuzz case '{caseName}' {what}." + Environment.NewLine +
            $"Corpus seed: 0x{CorpusSeed:X} (reproducible). Input: {input.Length} bytes, " +
            $"sha256 {Convert.ToHexString(SHA256.HashData(input))[..16]}." + Environment.NewLine +
            $"Offending bytes written to: {path}");
    }

    /// <summary>Writes the input beside the test binary; never lets an I/O problem mask the failure.</summary>
    private static string DumpArtifact(string caseName, byte[] input)
    {
        try
        {
            Directory.CreateDirectory(ArtifactDirectory);
            var safe = new StringBuilder(caseName.Length);
            foreach (char c in caseName)
                safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
            string path = Path.Combine(ArtifactDirectory, safe + ".pdf");
            File.WriteAllBytes(path, input);
            return path;
        }
        catch (IOException ex) { return "<could not write artifact: " + ex.Message + ">"; }
        catch (UnauthorizedAccessException ex) { return "<could not write artifact: " + ex.Message + ">"; }
    }
}
