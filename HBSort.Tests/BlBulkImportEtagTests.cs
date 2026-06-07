using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// UX X.29 (v0.1.16): Tests fuer ETag-Caching + Hash-Skip im
/// BlBulkImportService. Verwendet einen MockHttpMessageHandler statt echter
/// GitHub-Calls. BlCacheRepository ist ein Stub - wir interessieren uns
/// nur fuer den HTTP-Pfad.
/// </summary>
public class BlBulkImportEtagTests
{
    [Fact]
    public async Task ImportFromGitHub_returns_skipped_on_HTTP_304()
    {
        var handler = new MockHandler((req) =>
        {
            // Server-Bestaetigung dass der ETag-Header geschickt wurde.
            Assert.Contains(req.Headers.IfNoneMatch, e => e.Tag == "\"abc123\"");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var http = new HttpClient(handler);
        var cache = new StubCacheRepo();
        var sut = new BlBulkImportService(cache, http);

        var result = await sut.ImportFromGitHubAsync(
            previousEtag: "\"abc123\"",
            previousContentHash: null);

        Assert.True(result.Skipped);
        Assert.Contains("304", result.SkipReason);
        Assert.Equal(0, result.ItemsImported);
        Assert.Equal(0, result.InventoriesImported);
        Assert.Equal(0, cache.UpsertCalls);
        Assert.Equal(0, cache.BulkInsertCalls);
    }

    [Fact]
    public async Task ImportFromGitHub_passes_etag_through_when_unchanged()
    {
        // Bei 304 soll der vorhandene ETag im Result zurueckgereicht werden,
        // damit der Aufrufer ihn unveraendert in den Settings haelt.
        var handler = new MockHandler(req =>
            new HttpResponseMessage(HttpStatusCode.NotModified));
        var http = new HttpClient(handler);
        var sut = new BlBulkImportService(new StubCacheRepo(), http);

        var result = await sut.ImportFromGitHubAsync(
            previousEtag: "\"my-tag\"",
            previousContentHash: "deadbeef");

        Assert.Equal("\"my-tag\"", result.NewEtag);
        Assert.Equal("deadbeef", result.NewContentHash);
    }

    [Fact]
    public async Task ImportFromGitHub_skips_when_content_hash_matches()
    {
        // Server liefert 200 + ZIP-Bytes, aber der Hash ist identisch zum
        // gespeicherten Hash -> Service ueberspringt Entpacken/Import.
        var zipBytes = MakeMinimalZipBytes();
        var expectedHash = Convert.ToHexString(SHA256.HashData(zipBytes));

        var handler = new MockHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"new-tag\"");
            return resp;
        });
        var http = new HttpClient(handler);
        var cache = new StubCacheRepo();
        var sut = new BlBulkImportService(cache, http);

        var result = await sut.ImportFromGitHubAsync(
            previousEtag: null,                    // kein ETag -> If-None-Match wird nicht gesendet
            previousContentHash: expectedHash);    // gleicher Hash erwartet

        Assert.True(result.Skipped);
        Assert.Contains("Hash", result.SkipReason);
        // Trotz Skip kommt der neue ETag durch - waere sonst beim
        // naechsten Lauf wieder ein "first run" mit voller Last.
        Assert.Equal("\"new-tag\"", result.NewEtag);
        Assert.Equal(expectedHash, result.NewContentHash, ignoreCase: true);
        // Kein Cache-Write.
        Assert.Equal(0, cache.UpsertCalls);
        Assert.Equal(0, cache.BulkInsertCalls);
    }

    [Fact]
    public async Task ImportFromGitHub_processes_when_no_etag_and_no_previous_hash()
    {
        // Frische Installation: weder ETag noch Hash bekannt.
        // Service muss Download + Entpacken + Import durchziehen.
        var zipBytes = MakeMinimalZipBytes();

        var handler = new MockHandler(req =>
        {
            // Sicherstellen: kein If-None-Match Header (weil previousEtag=null)
            Assert.Empty(req.Headers.IfNoneMatch);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"first-tag\"");
            return resp;
        });
        var http = new HttpClient(handler);
        var sut = new BlBulkImportService(new StubCacheRepo(), http);

        var result = await sut.ImportFromGitHubAsync(
            previousEtag: null,
            previousContentHash: null);

        Assert.False(result.Skipped);
        Assert.Equal("\"first-tag\"", result.NewEtag);
        Assert.NotNull(result.NewContentHash);
        // Hash ist deterministisch fuer die gleichen Bytes
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(zipBytes)),
            result.NewContentHash,
            ignoreCase: true);
    }

    [Fact]
    public async Task ImportFromGitHub_processes_when_hash_differs()
    {
        // Server liefert NEUE ZIP-Bytes (Hash unterscheidet sich).
        // Service muss durchimportieren.
        var oldHash = Convert.ToHexString(SHA256.HashData(new byte[] { 0x99 }));
        var newZipBytes = MakeMinimalZipBytes();

        var handler = new MockHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(newZipBytes)
            };
            return resp;
        });
        var http = new HttpClient(handler);
        var sut = new BlBulkImportService(new StubCacheRepo(), http);

        var result = await sut.ImportFromGitHubAsync(
            previousEtag: "\"old-tag\"",
            previousContentHash: oldHash);

        Assert.False(result.Skipped);
        Assert.NotEqual(oldHash.ToLowerInvariant(),
            (result.NewContentHash ?? "").ToLowerInvariant());
    }

    /// <summary>Erzeugt ein winziges valides ZIP mit einer einzigen Dummy-Datei.</summary>
    private static byte[] MakeMinimalZipBytes()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("dummy.txt", CompressionLevel.NoCompression);
            using var w = new StreamWriter(entry.Open());
            w.Write("hello");
        }
        return ms.ToArray();
    }

    /// <summary>HTTP-Mock-Handler: ein Lambda pro Request.</summary>
    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public MockHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request));
    }

    /// <summary>
    /// Stub fuer IBlCacheRepository - zaehlt nur Aufrufe, fuehrt nichts aus.
    /// Skipped-Pfade duerfen NIEMALS UpsertItems oder BulkInsertSubsets rufen.
    /// Alle anderen Methoden sind throw NotImplementedException - werden in
    /// diesen Tests nicht aufgerufen.
    /// </summary>
    private sealed class StubCacheRepo : IBlCacheRepository
    {
        public int UpsertCalls { get; private set; }
        public int BulkInsertCalls { get; private set; }

        public Task UpsertItemsAsync(IEnumerable<BlItem> items, CancellationToken ct = default)
        {
            UpsertCalls++;
            return Task.CompletedTask;
        }
        public Task<int> BulkInsertSubsetsAsync(IEnumerable<BlSubset> subsets, CancellationToken ct = default)
        {
            BulkInsertCalls++;
            return Task.FromResult(0);
        }

        // ---- Restliche Interface-Methoden, alle NotImplemented ----
        public Task<BlItem?> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertItemAsync(BlItem item, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> IsItemStaleAsync(string itemType, string itemNo, int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, int>> GetCategoryIdsForPartsAsync(IEnumerable<string> partNumbers, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, string>> GetItemNamesAsync(IEnumerable<string> partNumbers, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<(string ItemType, string ItemNo), BlItemSummary>> GetItemSummariesAsync(IEnumerable<(string ItemType, string ItemNo)> keys, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlSubset>> GetSubsetsAsync(string parentType, string parentNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, List<BlSubset>>> GetSubsetsBulkAsync(string parentType, IReadOnlyList<string> parentNos, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceSubsetsAsync(string parentType, string parentNo, IEnumerable<BlSubset> subsets, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindParentsByItemAsync(string itemType, string itemNo, int colorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(string blPartNo, int blColorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FindMinifigsContainingPartsAsync(IReadOnlyList<(string PartNo, int ColorId)> parts, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpsertColorsAsync(IEnumerable<BlColor> colors, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<int>> GetKnownColorIdsAsync(string partNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTime?> GetKnownColorsFetchedAtAsync(string partNo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplaceKnownColorsAsync(string partNo, IEnumerable<int> colorIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BlCacheStats> GetStatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearStaleAsync(int staleDays, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAllAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogApiCallAsync(string method, string? itemType, string? itemNo, int responseTimeMs, int statusCode, bool success, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetCallCountSinceAsync(DateTime since, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DateTime?> GetOldestCallInWindowAsync(TimeSpan window, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneApiCallLogAsync(int olderThanDays = 7, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HBSort.Core.Models.Pricing.PriceResult?> GetCachedPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int staleDays, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task UpsertPriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, HBSort.Core.Models.Pricing.PriceResult price, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<HBSort.Core.Models.Pricing.CachedPriceLookup?> GetCachedPriceWithStaleFlagAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, int ttlDays, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<bool> DeletePriceAsync(string itemType, string itemNo, int colorId, string guideType, string newOrUsed, string region, string currency, CancellationToken ct = default, string vatMode = "N", string countryCode = "") => throw new NotImplementedException();
        public Task<int> ClearAllPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> ClearEmptyPricesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> GetPriceCacheCountAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }
}
