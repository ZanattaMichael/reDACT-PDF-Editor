using System.Linq;
using System.Net.Http;
using PdfEditor.Core;

namespace PdfEditor.Tests;

public class UrlExtractionTests
{
    [Fact]
    public void ExtractLinks_FindsTheUrl_AndItsPage()
    {
        byte[] pdf = TestPdfs.WithLinkTo("https://example.com/report");

        var links = UrlTools.ExtractLinks(pdf);

        var link = Assert.Single(links);
        Assert.Equal(1, link.Page);
        Assert.Equal("https://example.com/report", link.Url);
    }

    [Fact]
    public void ExtractLinks_CapturesTheHotspotRectangle()
    {
        // WithLinkTo puts the link annotation at Rect [72 700 272 720].
        byte[] pdf = TestPdfs.WithLinkTo("https://example.com");

        var link = Assert.Single(UrlTools.ExtractLinks(pdf));

        Assert.Equal(72, link.X, 0.5);
        Assert.Equal(700, link.Y, 0.5);
        Assert.Equal(200, link.Width, 0.5);
        Assert.Equal(20, link.Height, 0.5);
    }

    [Fact]
    public void ExtractLinks_NoLinks_ReturnsEmpty()
    {
        byte[] pdf = TestPdfs.WithText(("no links", 72, 700, 12));
        Assert.Empty(UrlTools.ExtractLinks(pdf));
    }

    [Fact]
    public void ExtractLinkAnnotations_IncludesUriAndNonUriLinks()
    {
        // A doc with a web link and a JavaScript link (like Salesforce "Close Window").
        byte[] pdf = WithLinks();

        var all = UrlTools.ExtractLinkAnnotations(pdf);

        // Both link annotations are returned, each with its hotspot rectangle...
        Assert.Equal(2, all.Count);
        Assert.Contains(all, l => l.Kind == "uri" && l.Url == "https://example.com" && l.Width > 0);
        Assert.Contains(all, l => l.Kind == "javascript" && l.Url == "" && l.Width > 0);

        // ...while URI-only extraction (used for scanning) still returns just the web link.
        var uris = UrlTools.ExtractLinks(pdf);
        Assert.Equal("https://example.com", Assert.Single(uris).Url);
    }

    /// <summary>A page with one URI link and one JavaScript-action link annotation.</summary>
    private static byte[] WithLinks()
    {
        var doc = PdfEditor.Core.Pdf.PdfDocument.CreateNew();
        var page = doc.AddNewPage(595, 842);
        page.AddAnnotation(Link(72, 700, "URI", PdfEditor.Core.Pdf.PdfName.URI,
            new PdfEditor.Core.Pdf.PdfString(System.Text.Encoding.ASCII.GetBytes("https://example.com"))));
        page.AddAnnotation(Link(72, 660, "JavaScript", PdfEditor.Core.Pdf.PdfName.JS,
            PdfEditor.Core.Pdf.PdfString.FromText("window.close();")));
        return doc.Save();
    }

    private static PdfEditor.Core.Pdf.PdfDictionary Link(float x, float y, string kind,
        PdfEditor.Core.Pdf.PdfName key, PdfEditor.Core.Pdf.PdfObject value)
    {
        var action = new PdfEditor.Core.Pdf.PdfDictionary();
        action.Put(PdfEditor.Core.Pdf.PdfName.S, PdfEditor.Core.Pdf.PdfName.Of(kind));
        action.Put(key, value);
        var link = new PdfEditor.Core.Pdf.PdfDictionary();
        link.Put(PdfEditor.Core.Pdf.PdfName.Subtype, PdfEditor.Core.Pdf.PdfName.Link);
        link.Put(PdfEditor.Core.Pdf.PdfName.Rect, new PdfEditor.Core.Pdf.PdfRect(x, y, 200, 20).ToArray());
        link.Put(PdfEditor.Core.Pdf.PdfName.A, action);
        return link;
    }
}

public class UrlClassifierTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=x", "green")]
    [InlineData("https://mail.google.com", "green")]
    [InlineData("https://en.wikipedia.org/wiki/PDF", "green")]
    [InlineData("https://irs.gov/forms", "green")]
    public void KnownSites_AreGreen(string url, string expected)
        => Assert.Equal(expected, UrlClassifier.Rate(url).Level);

    [Theory]
    [InlineData("https://github.com/user/repo", "code-hosting")]
    [InlineData("https://gitlab.com/x", "code-hosting")]
    [InlineData("https://www.dropbox.com/s/abc/file.zip", "file-hosting")]
    [InlineData("https://bit.ly/3xyz", "url-shortener")]
    public void CodeAndFileHosting_AreYellow(string url, string category)
    {
        var (level, cat, _) = UrlClassifier.Rate(url);
        Assert.Equal("yellow", level);
        Assert.Equal(category, cat);
    }

    [Theory]
    [InlineData("https://free-prizes.xyz/claim")]
    [InlineData("https://download.tk/thing")]
    [InlineData("javascript:alert(1)")]
    public void RiskyShapes_AreRed(string url)
        => Assert.Equal("red", UrlClassifier.Rate(url).Level);

    [Fact]
    public void UnrecognisedSite_DefaultsToYellowCaution_NeverSilentlyGreen()
    {
        var (level, category, _) = UrlClassifier.Rate("https://some-random-blog.example/post");
        Assert.Equal("yellow", level);
        Assert.Equal("unknown", category);
    }

    [Fact]
    public void Classify_CarriesPageAndSource()
    {
        var verdict = UrlClassifier.Classify(new PdfLink(3, "https://github.com/x"));
        Assert.Equal(3, verdict.Page);
        Assert.Equal("heuristic", verdict.Source);
        Assert.Equal("yellow", verdict.Level);
    }
}

public class CloudflareMergeTests
{
    private static UrlVerdict Heuristic(string level = "yellow") =>
        new(1, "https://x.example", level, "unknown", "heuristic");

    [Fact]
    public void Merge_MaliciousFlag_ForcesRed_FromCloudflare()
    {
        var merged = CloudflareUrlScanner.Merge(Heuristic("green"), cloudflareMalicious: true);
        Assert.Equal("red", merged.Level);
        Assert.Equal("malicious", merged.Category);
        Assert.Equal("cloudflare", merged.Source);
    }

    [Fact]
    public void Merge_CleanFlag_KeepsHeuristicLevel_ButMarksSource()
    {
        var merged = CloudflareUrlScanner.Merge(Heuristic("yellow"), cloudflareMalicious: false);
        Assert.Equal("yellow", merged.Level);
        Assert.Equal("cloudflare", merged.Source);
    }

    [Fact]
    public void Merge_NoCloudflareResult_LeavesHeuristicUntouched()
    {
        var h = Heuristic("green");
        var merged = CloudflareUrlScanner.Merge(h, cloudflareMalicious: null);
        Assert.Equal("green", merged.Level);
        Assert.Equal("heuristic", merged.Source);
    }

    [Fact]
    public async Task ScanAsync_WithoutCredentials_FallsBackToHeuristic()
    {
        var links = new[] { new PdfLink(1, "https://github.com/x"), new PdfLink(1, "https://google.com") };
        var verdicts = await CloudflareUrlScanner.ScanAsync(links, creds: null);

        Assert.Collection(verdicts,
            v => { Assert.Equal("yellow", v.Level); Assert.Equal("heuristic", v.Source); },
            v => { Assert.Equal("green", v.Level); Assert.Equal("heuristic", v.Source); });
    }

    // A scripted Cloudflare endpoint: the scan POST returns a uuid, then the result GET returns
    // "not ready" a set number of times before yielding a verdict with the given malicious flag.
    private sealed class FakeCloudflare : HttpMessageHandler
    {
        private readonly bool _malicious;
        private readonly int _notReadyTimes;
        private int _polls;
        public int Submits { get; private set; }

        public FakeCloudflare(bool malicious, int notReadyTimes = 0)
        {
            _malicious = malicious;
            _notReadyTimes = notReadyTimes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                Submits++;
                return Task.FromResult(Json(System.Net.HttpStatusCode.OK, """{"uuid":"abc-123"}"""));
            }
            if (_polls++ < _notReadyTimes)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            string flag = _malicious ? "true" : "false";
            return Task.FromResult(Json(System.Net.HttpStatusCode.OK,
                "{\"verdicts\":{\"overall\":{\"malicious\":" + flag + "}}}"));
        }

        private static HttpResponseMessage Json(System.Net.HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private static readonly CloudflareCredentials Creds = new("acct", "token");

    [Fact]
    public async Task ScanAsync_Cloudflare_MaliciousVerdict_ForcesRed()
    {
        var links = new[] { new PdfLink(1, "https://google.com") }; // heuristic green
        using var http = new HttpClient(new FakeCloudflare(malicious: true));

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero);

        var v = Assert.Single(verdicts);
        Assert.Equal("red", v.Level);
        Assert.Equal("cloudflare", v.Source);
    }

    [Fact]
    public async Task ScanAsync_Cloudflare_PollsUntilReady_ThenReturnsClean()
    {
        var links = new[] { new PdfLink(1, "https://github.com/x") };
        using var http = new HttpClient(new FakeCloudflare(malicious: false, notReadyTimes: 2));

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero);

        var v = Assert.Single(verdicts);
        Assert.Equal("yellow", v.Level); // heuristic level kept
        Assert.Equal("cloudflare", v.Source);
    }

    [Fact]
    public async Task ScanAsync_Cloudflare_DeduplicatesRepeatedUrls()
    {
        var links = new[]
        {
            new PdfLink(1, "https://dup.example"), new PdfLink(2, "https://dup.example"),
        };
        var handler = new FakeCloudflare(malicious: true);
        using var http = new HttpClient(handler);

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero);

        Assert.Equal(2, verdicts.Count);
        Assert.Equal(1, handler.Submits); // the identical URL is only submitted once
    }

    // A thread-safe scripted endpoint that records how many requests are in flight at once, so a
    // test can prove the scans actually run concurrently rather than one after another.
    private sealed class ConcurrentCloudflare : System.Net.Http.HttpMessageHandler
    {
        private int _inFlight;
        private int _submits;
        public int MaxConcurrent;
        public int Submits => _submits;

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                int now = System.Threading.Interlocked.Increment(ref _inFlight);
                int seen;
                do { seen = MaxConcurrent; } while (now > seen &&
                    System.Threading.Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen);
                System.Threading.Interlocked.Increment(ref _submits);
                try { await Task.Delay(30, ct); }
                finally { System.Threading.Interlocked.Decrement(ref _inFlight); }
                return Json(System.Net.HttpStatusCode.OK, """{"uuid":"abc-123"}""");
            }
            return Json(System.Net.HttpStatusCode.OK,
                "{\"verdicts\":{\"overall\":{\"malicious\":false}}}");
        }

        private static System.Net.Http.HttpResponseMessage Json(System.Net.HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task ScanAsync_ScansDistinctUrlsConcurrently_NotOneAtATime()
    {
        // Six distinct URLs. Serial scanning (the #82 bug) would only ever have one request in
        // flight; with bounded concurrency more than one runs at once.
        var links = Enumerable.Range(0, 6).Select(i => new PdfLink(1, $"https://example.com/{i}")).ToArray();
        var handler = new ConcurrentCloudflare();
        using var http = new HttpClient(handler);

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero,
            maxConcurrency: 4);

        Assert.Equal(6, verdicts.Count);
        Assert.True(handler.MaxConcurrent > 1,
            $"expected concurrent scans, but only {handler.MaxConcurrent} was ever in flight");
        Assert.True(handler.MaxConcurrent <= 4, "concurrency exceeded the requested bound");
    }

    [Fact]
    public async Task ScanAsync_CapsHowManyUrlsAreSubmitted()
    {
        // Five distinct URLs but a cap of two: only two reach Cloudflare, the rest keep the
        // local heuristic so a link-bomb document cannot enqueue unbounded scans.
        var links = Enumerable.Range(0, 5).Select(i => new PdfLink(1, $"https://google.com/{i}")).ToArray();
        var handler = new ConcurrentCloudflare();
        using var http = new HttpClient(handler);

        var verdicts = await CloudflareUrlScanner.ScanAsync(links, Creds, http, TimeSpan.Zero,
            maxConcurrency: 4, maxUrls: 2);

        Assert.Equal(5, verdicts.Count);
        Assert.Equal(2, handler.Submits);
        // Exactly the two scanned links carry a Cloudflare source; the other three stay heuristic.
        Assert.Equal(2, verdicts.Count(v => v.Source == "cloudflare"));
        Assert.Equal(3, verdicts.Count(v => v.Source == "heuristic"));
    }

}
