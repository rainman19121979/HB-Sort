using System.Net;
using System.Net.Http;
using System.Text;
using HBSort.Core.Models;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den BricklinkImageProvider mit einem FakeHandler-Mock.
/// Pruefen URL-Generierung pro Typ, Fallback-Verhalten bei 404 und korrekte
/// Verwendung des persistenten Caches.
/// </summary>
public class BricklinkImageProviderTests : IDisposable
{
    /// <summary>
    /// Eigene Test-Pfade pro Instanz, damit Tests sich nicht beeinflussen
    /// und keine Production-Cache-Files reinwirken.
    /// </summary>
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"lego-image-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); }
        catch { /* best effort */ }
    }

    // Brickognize liefert BL-Color-IDs direkt; Tests rufen
    // GetImageFileAsync mit BL-Color-ID auf (kein RB->BL-Mapping mehr).

    [Fact]
    public async Task Part_with_color_uses_bricklink_color_url()
    {
        var item = MakePartItem("3001");
        var requested = new List<string>();
        var http = MakeClient(req =>
        {
            requested.Add($"{req.Method} {req.RequestUri}");
            return Reply(HttpStatusCode.OK, "fake-png-bytes");
        });

        var sut = NewProvider(http);
        // BL-Color 3 = Yellow, direkt ohne Mapping
        var path = await sut.GetImageFileAsync(item, bricklinkColorId: 3);

        // 1× HEAD + 1× GET fuer dieselbe URL
        Assert.Equal(2, requested.Count);
        Assert.Contains(requested, r => r.StartsWith("HEAD ") &&
            r.Contains("/PN/3/3001.png"));
        Assert.Contains(requested, r => r.StartsWith("GET ") &&
            r.Contains("/PN/3/3001.png"));

        // Lokaler Pfad zurueckgegeben + Datei tatsaechlich da
        Assert.True(File.Exists(path), $"Cache-File nicht angelegt: {path}");
        Assert.EndsWith("part_3001_c3.png", path);
    }

    [Fact]
    public async Task Part_without_color_uses_color_zero_url()
    {
        var item = MakePartItem("3001");
        var http = MakeClient(_ => Reply(HttpStatusCode.OK, "x"));

        var sut = NewProvider(http);
        var path = await sut.GetImageFileAsync(item, bricklinkColorId: null);

        Assert.EndsWith("part_3001_c0.png", path);
    }

    [Fact]
    public async Task Part_color_404_falls_back_to_color_zero()
    {
        var item = MakePartItem("3001");
        var http = MakeClient(req =>
        {
            // Erste URL (Farbe 3) lieferte 404, zweite URL (Farbe 0) liefert 200
            if (req.RequestUri!.AbsolutePath.Contains("/PN/3/"))
                return Reply(HttpStatusCode.NotFound);
            return Reply(HttpStatusCode.OK, "x");
        });

        var sut = NewProvider(http);
        var path = await sut.GetImageFileAsync(item, bricklinkColorId: 3);

        Assert.EndsWith("part_3001_c0.png", path);
    }

    [Fact]
    public async Task All_bl_urls_404_falls_back_to_brickognize()
    {
        var item = MakePartItem("9999");
        item.ImgUrl = "https://cdn.brickognize.example/9999.webp";

        var http = MakeClient(req =>
        {
            // BL-Domain liefert 404, brickognize-Domain liefert 200
            if (req.RequestUri!.Host.Contains("bricklink.com"))
                return Reply(HttpStatusCode.NotFound);
            return Reply(HttpStatusCode.OK, "fallback-bytes");
        });

        var sut = NewProvider(http);
        var path = await sut.GetImageFileAsync(item, bricklinkColorId: 3);

        // Brickognize-Fallback nutzt das bo_<md5>-Schema
        Assert.True(File.Exists(path));
        Assert.Contains("bo_", path);
        Assert.EndsWith(".webp", path);
    }

    [Fact]
    public async Task Minifig_uses_MN_url()
    {
        var item = MakeMinifigItem("cty0969");
        var http = MakeClient(_ => Reply(HttpStatusCode.OK, "x"));

        var sut = NewProvider(http);
        var path = await sut.GetImageFileAsync(item, bricklinkColorId: null);

        Assert.EndsWith("fig_cty0969.png", path);
    }

    [Fact]
    public async Task Set_uses_SN_url()
    {
        var item = new BrickognizeItem
        {
            Id = "75300-1",
            Type = "set",
            ExternalSites = new List<BrickognizeExternalSite>
            {
                new() { Name = "bricklink", Url = "https://www.bricklink.com/v2/catalog/catalogitem.page?S=75300-1" }
            }
        };
        var http = MakeClient(_ => Reply(HttpStatusCode.OK, "x"));
        var sut = NewProvider(http);

        var path = await sut.GetImageFileAsync(item, bricklinkColorId: null);

        Assert.EndsWith("set_75300-1.png", path);
    }

    [Fact]
    public async Task Sticker_skips_bricklink_and_uses_brickognize()
    {
        var item = new BrickognizeItem
        {
            Id = "stk001",
            Type = "sticker",
            ImgUrl = "https://cdn.brickognize.example/stk001.webp",
            ExternalSites = new List<BrickognizeExternalSite>()
        };
        var requested = new List<string>();
        var http = MakeClient(req =>
        {
            requested.Add($"{req.Method} {req.RequestUri}");
            return Reply(HttpStatusCode.OK, "x");
        });

        var sut = NewProvider(http);
        var path = await sut.GetImageFileAsync(item, bricklinkColorId: null);

        // Kein HEAD-Request - direkt GET fuer das Brickognize-Bild
        Assert.DoesNotContain(requested, r => r.StartsWith("HEAD "));
        Assert.Contains("bo_", path);
    }

    [Fact]
    public async Task Disabled_setting_skips_bricklink_entirely()
    {
        var item = MakePartItem("3001");
        item.ImgUrl = "https://cdn.brickognize.example/3001.webp";

        var requested = new List<string>();
        var http = MakeClient(req =>
        {
            requested.Add($"{req.Method} {req.RequestUri}");
            return Reply(HttpStatusCode.OK, "x");
        });

        var settings = new TestSettingsService(preferBl: false, limitMb: 1024);
        var cache = NewCache(http, settings);
        var sut = new BricklinkImageProvider(http, new ExternalIdResolver(), settings, cache);

        var path = await sut.GetImageFileAsync(item, bricklinkColorId: 3);

        Assert.DoesNotContain(requested, r => r.Contains("img.bricklink.com"));
        Assert.Contains("bo_", path);
    }

    [Fact]
    public async Task Bricklink_color_id_passed_through_directly()
    {
        // BL 86 (Light Bluish Gray) wird direkt durchgereicht, ohne Mapping-Lookup.
        var item = MakePartItem("3001");
        var requested = new List<string>();
        var http = MakeClient(req =>
        {
            requested.Add(req.RequestUri!.AbsoluteUri);
            return Reply(HttpStatusCode.OK, "x");
        });

        var sut = NewProvider(http);
        var path = await sut.GetImageFileAsync(item, bricklinkColorId: 86);

        Assert.EndsWith("part_3001_c86.png", path);
        Assert.Contains(requested, r => r.Contains("/PN/86/3001.png"));
    }

    [Fact]
    public async Task Repeated_calls_use_disk_cache()
    {
        // Beim zweiten Aufruf darf KEIN HTTP-Request mehr fliessen (Cache-Hit).
        var item = MakePartItem("3001");
        var requested = new List<string>();
        var http = MakeClient(req =>
        {
            requested.Add(req.Method.Method);
            return Reply(HttpStatusCode.OK, "x");
        });

        var sut = NewProvider(http);
        await sut.GetImageFileAsync(item, bricklinkColorId: 3);
        var firstCount = requested.Count;

        // Zweiter Aufruf: aus Cache
        await sut.GetImageFileAsync(item, bricklinkColorId: 3);
        Assert.Equal(firstCount, requested.Count);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static BrickognizeItem MakePartItem(string blPartNum) => new()
    {
        Id = blPartNum,
        Type = "part",
        ExternalSites = new List<BrickognizeExternalSite>
        {
            new() { Name = "bricklink",
                    Url = $"https://www.bricklink.com/v2/catalog/catalogitem.page?P={blPartNum}" }
        }
    };

    private static BrickognizeItem MakeMinifigItem(string blMinifigId) => new()
    {
        Id = blMinifigId,
        Type = "fig",
        ExternalSites = new List<BrickognizeExternalSite>
        {
            new() { Name = "bricklink",
                    Url = $"https://www.bricklink.com/v2/catalog/catalogitem.page?M={blMinifigId}" }
        }
    };

    private static HttpResponseMessage Reply(HttpStatusCode code, string? body = null)
    {
        var msg = new HttpResponseMessage(code);
        if (body != null) msg.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        return msg;
    }

    private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new FakeHandler(handler)) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Default-Provider mit eigenem Test-Cache-Dir + DB.
    /// </summary>
    private BricklinkImageProvider NewProvider(HttpClient http)
    {
        var settings = new TestSettingsService(preferBl: true, limitMb: 1024);
        var cache = NewCache(http, settings);
        return new BricklinkImageProvider(http, new ExternalIdResolver(), settings, cache);
    }

    private PersistentImageCache NewCache(HttpClient http, ISettingsService settings)
    {
        var imagesDir = Path.Combine(_testDir, "images");
        var dbPath = Path.Combine(_testDir, "image_cache.db");
        Directory.CreateDirectory(imagesDir);
        return new PersistentImageCache(http, settings, imagesDir, dbPath);
    }

    /// <summary>Fake fuer ISettingsService mit ImageCacheSettings.</summary>
    private sealed class TestSettingsService : ISettingsService
    {
        private readonly AppSettings _settings;

        public TestSettingsService(bool preferBl, int limitMb)
        {
            _settings = new AppSettings
            {
                ImageCache = new ImageCacheSettings
                {
                    PreferBricklinkImages = preferBl,
                    LimitMb = limitMb,
                    PreloadOnMinifigScan = false
                }
            };
        }

        public AppSettings Current => _settings;
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
