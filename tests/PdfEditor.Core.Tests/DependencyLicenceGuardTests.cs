using System.Text.RegularExpressions;
using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Pins the outcome of #170: reDACT no longer depends on iText (AGPL), including the iText adapter
/// that 2.x reached BouncyCastle through. BouncyCastle itself is MIT and is now the engine's one
/// cryptography library, used directly as <c>BouncyCastle.Cryptography</c>. iText is easy to re-add
/// by accident — a package restored from muscle memory, a test helper copied from an old branch —
/// so the guard checks the project files, the sources and the built assembly.
/// </summary>
public class DependencyLicenceGuardTests
{
    private const string BouncyCastle = "BouncyCastle.Cryptography";

    private static readonly Regex PackageReference = new(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase);

    private static readonly Regex ForbiddenCode = new(
        @"\busing\s+iText\b|\biText\.(Kernel|IO|Forms|Layout|Signatures|Commons|Bouncycastle|Pdfa|Pdfua|Svg|StyledXmlParser)\b");

    [Fact]
    public void NoProjectReferencesITextOrAnotherBouncyCastlePackage()
    {
        var offenders = RepoFiles("*.csproj", "*.props", "*.targets")
            .SelectMany(f => PackageReference.Matches(File.ReadAllText(f)).Select(m => (File: f, Package: m.Groups[1].Value)))
            .Where(r => r.Package.Contains("itext", StringComparison.OrdinalIgnoreCase)
                        || (r.Package.Contains("bouncy", StringComparison.OrdinalIgnoreCase)
                            && !r.Package.Equals(BouncyCastle, StringComparison.OrdinalIgnoreCase)))
            .Select(r => $"{Relative(r.File)} ({r.Package})")
            .ToList();
        Assert.True(offenders.Count == 0,
            $"iText (AGPL) or a BouncyCastle package other than {BouncyCastle} referenced in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoSourceUsesIText()
    {
        var offenders = RepoFiles("*.cs")
            .Where(f => ForbiddenCode.IsMatch(File.ReadAllText(f)))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0, "iText API used in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CoreAssemblyReferencesBouncyCastleAndNotIText()
    {
        var references = typeof(Redactor).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        Assert.DoesNotContain(references, n => n.StartsWith("itext", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, n => n.Equals(BouncyCastle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// All of the engine's cryptography is BouncyCastle, so it behaves the same on every platform
    /// rather than on whatever the system's OpenSSL, CNG or Security framework allows. The tests may
    /// use .NET's cryptography, and do, as a second opinion on BouncyCastle's.
    /// </summary>
    [Fact]
    public void SourcesUseBouncyCastleForCryptography()
    {
        var offenders = RepoFiles("*.cs", "*.csproj")
            .Where(f => Relative(f).StartsWith("src", StringComparison.Ordinal)
                        && File.ReadAllText(f).Contains("System.Security.Cryptography", StringComparison.Ordinal))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0,
            "System.Security.Cryptography used in (use BouncyCastle instead): " + string.Join(", ", offenders));
    }

    /// <summary>Files under src/ and tests/, skipping build output.</summary>
    private static IEnumerable<string> RepoFiles(params string[] patterns)
    {
        string root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        return new[] { "src", "tests" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => patterns.SelectMany(p => Directory.EnumerateFiles(d, p, SearchOption.AllDirectories)))
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}")
                        && !f.EndsWith(nameof(DependencyLicenceGuardTests) + ".cs", StringComparison.Ordinal));
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot(), path);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PdfEditor.sln"))) return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root (PdfEditor.sln).");
    }
}
