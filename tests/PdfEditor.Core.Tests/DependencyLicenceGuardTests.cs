using System.Text.RegularExpressions;
using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

/// <summary>
/// Pins the outcome of #170: reDACT no longer depends on iText (AGPL) or on BouncyCastle, which
/// only ever came in as iText's crypto provider. Both are easy to re-add by accident — a package
/// restored from muscle memory, a test helper copied from an old branch — so the guard checks
/// the project files, the sources and the built assembly.
/// </summary>
public class DependencyLicenceGuardTests
{
    private static readonly Regex ForbiddenPackage = new(
        @"<PackageReference\s+Include=""(itext[^""]*|[^""]*bouncy[^""]*)""", RegexOptions.IgnoreCase);

    private static readonly Regex ForbiddenCode = new(
        @"\busing\s+iText\b|\biText\.(Kernel|IO|Forms|Layout|Signatures|Commons|Bouncycastle|Pdfa|Pdfua|Svg|StyledXmlParser)\b|\bOrg\.BouncyCastle\b");

    [Fact]
    public void NoProjectReferencesITextOrBouncyCastle()
    {
        var offenders = RepoFiles("*.csproj", "*.props", "*.targets")
            .Where(f => ForbiddenPackage.IsMatch(File.ReadAllText(f)))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0, "AGPL/BouncyCastle package re-added in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoSourceUsesITextOrBouncyCastle()
    {
        var offenders = RepoFiles("*.cs")
            .Where(f => ForbiddenCode.IsMatch(File.ReadAllText(f)))
            .Select(Relative)
            .ToList();
        Assert.True(offenders.Count == 0, "iText/BouncyCastle API used in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CoreAssemblyReferencesNeitherLibrary()
    {
        var references = typeof(Redactor).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");
        Assert.DoesNotContain(references, n =>
            n.StartsWith("itext", StringComparison.OrdinalIgnoreCase)
            || n.Contains("BouncyCastle", StringComparison.OrdinalIgnoreCase));
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
