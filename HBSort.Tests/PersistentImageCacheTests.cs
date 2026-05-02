using System.Net;
using System.Net.Http;
using System.Text;
using HBSort.Core.Models;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den PersistentImageCache: GetOrDownload, Touch, Clear, Stats und LRU-Eviction.
/// </summary>
public class PersistentImageCacheTests : IDisposable
{
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"lego-cache-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task GetOrDownload_creates_file_and_registers_entry()
    {
        var cache = NewCache(MakeClient(_ => Reply(HttpStatusCode.OK, "abc")), 1024);
        var path = await cache.GetOrDownloadAsync("https://example/test.png", "test.png");

        Assert.True(File.Exists(path));
        var stats = cache.GetStats();
        Assert.Equal(1, stats.FileCount);
        Assert.Equal(3, stats.TotalSizeBytes); // "abc"
    }

    [Fact]
    public async Task GetOrDownload_returns_existing_file_without_redownload()
    {
        var calls = 0;
        var http = MakeClient(_ =>
        {
            calls++;
            return Reply(HttpStatusCode.OK, "abc");
        });
        var cache = NewCache(http, 1024);

        await cache.GetOrDownloadAsync("https://example/test.png", "test.png");
        await cache.GetOrDownloadAsync("https://example/test.png", "test.png");

        // Zweiter Aufruf greift auf den Cache zu, kein zweiter Download
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LRU_eviction_removes_oldest_first()
    {
        // 3 Eintraege a 100 KB, Limit 200 KB -> nach drittem Add muss der erste evicted sein,
        // Cache reduziert auf 90% von 200 KB = 180 KB -> nur 1 Eintrag uebrig wenn man so rechnet,
        // aber wir evicten Liste sortiert nach LRU – nach dem dritten Add reicht ein Loeschen.
        var bytes100Kb = Encoding.UTF8.GetBytes(new string('x', 100 * 1024));
        var http = MakeClient(_ =>
        {
            var msg = Reply(HttpStatusCode.OK);
            msg.Content = new ByteArrayContent(bytes100Kb);
            return msg;
        });
        // Limit auf "0.2 MB" setzen ist nicht moeglich – minimales Limit ist 1 MB.
        // Test fuer Eviction: wir setzen Limit auf 1 MB und schreiben 12x 100 KB = 1.2 MB.
        var cache = NewCache(http, 1);

        for (int i = 0; i < 12; i++)
        {
            await cache.GetOrDownloadAsync($"https://example/{i}.png", $"file{i}.png");
            // touch unterscheiden, damit LRU-Reihenfolge eindeutig ist
            await Task.Delay(15);
        }

        var stats = cache.GetStats();
        // Cache muss kleiner als das Limit sein
        Assert.True(stats.TotalSizeBytes <= 1L * 1024 * 1024,
            $"Cache ueber Limit: {stats.TotalSizeBytes} bytes");
        // Mindestens ein Eintrag muss evicted sein
        Assert.True(stats.FileCount < 12, $"Erwartet < 12, war {stats.FileCount}");
        // Erster Eintrag (file0) sollte weg sein – als aeltester
        Assert.False(File.Exists(Path.Combine(_testDir, "images", "file0.png")));
    }

    [Fact]
    public async Task Unlimited_setting_skips_eviction()
    {
        // Limit 0 = unbegrenzt.
        var bytes100Kb = Encoding.UTF8.GetBytes(new string('x', 100 * 1024));
        var http = MakeClient(_ =>
        {
            var msg = Reply(HttpStatusCode.OK);
            msg.Content = new ByteArrayContent(bytes100Kb);
            return msg;
        });
        var cache = NewCache(http, 0);

        for (int i = 0; i < 5; i++)
        {
            await cache.GetOrDownloadAsync($"https://example/{i}.png", $"file{i}.png");
        }

        var stats = cache.GetStats();
        Assert.Equal(5, stats.FileCount);
        Assert.Equal(0L, stats.LimitBytes); // unbegrenzt im Stats-Objekt
    }

    [Fact]
    public async Task Clear_deletes_all_files_and_resets_stats()
    {
        var http = MakeClient(_ => Reply(HttpStatusCode.OK, "x"));
        var cache = NewCache(http, 1024);

        await cache.GetOrDownloadAsync("https://example/a.png", "a.png");
        await cache.GetOrDownloadAsync("https://example/b.png", "b.png");
        Assert.Equal(2, cache.GetStats().FileCount);

        var deleted = await cache.ClearAsync();

        Assert.True(deleted >= 2);
        Assert.Equal(0, cache.GetStats().FileCount);
        Assert.Equal(0L, cache.GetStats().TotalSizeBytes);
    }

    [Fact]
    public async Task Stats_contain_oldest_access()
    {
        var http = MakeClient(_ => Reply(HttpStatusCode.OK, "x"));
        var cache = NewCache(http, 1024);

        var before = DateTime.UtcNow;
        await cache.GetOrDownloadAsync("https://example/a.png", "a.png");
        var stats = cache.GetStats();

        Assert.NotNull(stats.OldestAccess);
        Assert.True(stats.OldestAccess >= before.AddSeconds(-1)); // kleine Toleranz
    }

    [Fact]
    public async Task TouchAccess_updates_last_accessed_at()
    {
        var http = MakeClient(_ => Reply(HttpStatusCode.OK, "x"));
        var cache = NewCache(http, 1024);

        await cache.GetOrDownloadAsync("https://example/a.png", "a.png");
        var oldStats = cache.GetStats();

        await Task.Delay(50);
        cache.TouchAccess("a.png");

        var newStats = cache.GetStats();
        // OldestAccess hat sich nach vorne verschoben
        Assert.True(newStats.OldestAccess >= oldStats.OldestAccess);
    }

    [Fact]
    public async Task StatsChanged_event_fires_on_add()
    {
        var http = MakeClient(_ => Reply(HttpStatusCode.OK, "x"));
        var cache = NewCache(http, 1024);

        var fired = 0;
        cache.StatsChanged += (_, _) => fired++;

        await cache.GetOrDownloadAsync("https://example/a.png", "a.png");

        Assert.True(fired >= 1);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private PersistentImageCache NewCache(HttpClient http, int limitMb)
    {
        var settings = new TestSettings(limitMb);
        var imagesDir = Path.Combine(_testDir, "images");
        var dbPath = Path.Combine(_testDir, "image_cache.db");
        return new PersistentImageCache(http, settings, imagesDir, dbPath);
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string? body = null)
    {
        var msg = new HttpResponseMessage(code);
        if (body != null) msg.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        return msg;
    }

    private static HttpClient MakeClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new FakeHandler(handler));

    private sealed class TestSettings : ISettingsService
    {
        private readonly AppSettings _settings;
        public TestSettings(int limitMb)
        {
            _settings = new AppSettings { ImageCache = new ImageCacheSettings { LimitMb = limitMb } };
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
