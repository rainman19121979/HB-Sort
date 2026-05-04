using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using HBSort.Core.Models;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des Image-Providers (BL-First, Brickognize-Fallback).
///
/// URL-Patterns:
///   Teil mit Farbe:  https://img.bricklink.com/ItemImage/PN/{bl_color}/{bl_part}.png
///   Teil ohne Farbe: https://img.bricklink.com/ItemImage/PN/0/{bl_part}.png
///   Minifigur:       https://img.bricklink.com/ItemImage/MN/0/{bl_minifig}.png
///   Set:             https://img.bricklink.com/ItemImage/SN/0/{bl_set}.png
///
/// Ablauf pro Request:
///   1. Wenn Setting "BL-Bilder bevorzugen" aus -> direkt Brickognize-Fallback.
///   2. Sonst Kandidaten-URLs in Praeferenz-Reihenfolge bauen.
///   3. Pro Kandidat: HEAD-Request mit 3 Sek Timeout.
///      a) 200 OK -> URL via PersistentImageCache laden + Pfad zurueckgeben.
///      b) 404 oder Timeout -> naechster Kandidat.
///   4. Alle Kandidaten fehlgeschlagen -> Brickognize-img_url ueber Cache laden.
///
/// In-Memory-URL-Cache (Valid/Missing) verhindert wiederholte HEAD-Requests
/// fuer dieselbe URL waehrend einer Session.
/// </summary>
public class BricklinkImageProvider : IPartImageProvider
{
    private const string BlImageBase = "https://img.bricklink.com/ItemImage/";

    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(3);

    private enum UrlState { Valid, Missing }

    /// <summary>Bekannte URL-Status fuer die Lebenszeit dieser Instanz.</summary>
    private readonly ConcurrentDictionary<string, UrlState> _urlCache = new();

    private readonly HttpClient _http;
    private readonly IExternalIdResolver _idResolver;
    private readonly ISettingsService _settings;
    private readonly IPersistentImageCache _imageCache;

    public BricklinkImageProvider(
        HttpClient http,
        IExternalIdResolver idResolver,
        ISettingsService settings,
        IPersistentImageCache imageCache)
    {
        _http = http;
        _idResolver = idResolver;
        _settings = settings;
        _imageCache = imageCache;
    }

    public async Task<string> GetImageFileAsync(BrickognizeItem item, int? bricklinkColorId, CancellationToken ct = default)
    {
        // Setting "BL-Bilder bevorzugen" aus? -> direkt Brickognize-Fallback (im Cache).
        if (!_settings.Current.ImageCache.PreferBricklinkImages)
        {
            return await DownloadFallbackOrEmptyAsync(item, ct);
        }

        var type = ParseType(item.Type);
        var ids = _idResolver.Resolve(item.ExternalSites, type);

        if (string.IsNullOrEmpty(ids.BricklinkId))
        {
            // Sticker / Unknown / fehlende BL-ID -> Fallback Brickognize.
            Log.Debug("ImageProvider: keine BL-ID fuer Item {Id} ({Type}), Fallback Brickognize", item.Id, item.Type);
            return await DownloadFallbackOrEmptyAsync(item, ct);
        }

        // BL-Color-ID nur fuer Teile durchreichen; Minifigs/Sets ignorieren Farbe.
        // Brickognize liefert BL-Color-IDs direkt - kein Mapping noetig.
        int? colorForUrl = (type == BrickognizeItemType.Part && bricklinkColorId.HasValue && bricklinkColorId.Value >= 0)
            ? bricklinkColorId
            : null;

        var candidates = BuildCandidateUrls(type, ids.BricklinkId!, colorForUrl);
        var fetched = await TryFetchFromCandidatesAsync(candidates, ct);
        if (fetched != null) return fetched;

        // Letzter Fallback: Brickognize.
        Log.Debug("ImageProvider: alle BL-Kandidaten fehlgeschlagen, Fallback Brickognize fuer {Id}", item.Id);
        return await DownloadFallbackOrEmptyAsync(item, ct);
    }

    public async Task<string> GetImageFileByBlAsync(string bricklinkType, string bricklinkId, int? bricklinkColorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bricklinkId)) return string.Empty;

        // Wenn die Settings BL-Bilder deaktiviert haben, gibt es hier keinen Brickognize-Fallback
        // (wir haben kein Item-Objekt mit ImgUrl). Wir liefern dann einen leeren String.
        if (!_settings.Current.ImageCache.PreferBricklinkImages)
        {
            return string.Empty;
        }

        var type = bricklinkType.ToUpperInvariant() switch
        {
            "M" => BrickognizeItemType.Minifig,
            "P" => BrickognizeItemType.Part,
            "S" => BrickognizeItemType.Set,
            _   => BrickognizeItemType.Unknown
        };

        var candidates = BuildCandidateUrls(type, bricklinkId, bricklinkColorId);
        var fetched = await TryFetchFromCandidatesAsync(candidates, ct);
        return fetched ?? string.Empty;
    }

    /// <summary>
    /// Iteriert ueber die Kandidaten-URLs in Praeferenz-Reihenfolge:
    /// pro URL erst Cache-Status pruefen, dann HEAD-Request, dann Download.
    /// Liefert den lokalen Pfad sobald ein Kandidat klappt, sonst null.
    /// </summary>
    private async Task<string?> TryFetchFromCandidatesAsync(
        IEnumerable<(string url, string cacheKey)> candidates, CancellationToken ct)
    {
        foreach (var (url, cacheKey) in candidates)
        {
            // 1) URL bereits getestet?
            if (_urlCache.TryGetValue(url, out var state))
            {
                if (state == UrlState.Missing) continue;
                try
                {
                    var path = await _imageCache.GetOrDownloadAsync(url, cacheKey, ct);
                    Log.Debug("ImageProvider: Cache liefert {Path} (URL bekannt-valid)", path);
                    return path;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ImageProvider: Cache/Download fuer bekannt-valide URL fehlgeschlagen {Url}", url);
                    _urlCache[url] = UrlState.Missing;
                    continue;
                }
            }

            // 2) HEAD-Request
            var exists = await CheckUrlExistsAsync(url, ct);
            if (!exists)
            {
                _urlCache[url] = UrlState.Missing;
                Log.Debug("ImageProvider: BL-Bild fehlt {Url}", url);
                continue;
            }

            _urlCache[url] = UrlState.Valid;
            Log.Information("ImageProvider: BL-Bild gefunden {Url}", url);
            try
            {
                return await _imageCache.GetOrDownloadAsync(url, cacheKey, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ImageProvider: Download fehlgeschlagen trotz HEAD ok: {Url}", url);
                _urlCache[url] = UrlState.Missing;
            }
        }
        return null;
    }

    /// <summary>
    /// Laed das Brickognize-Bild ueber den Cache. Wenn es ueberhaupt keine URL gibt
    /// oder der Download fehlschlaegt, wird ein leerer String zurueckgegeben -
    /// die UI zeigt dann eben kein Bild an.
    /// </summary>
    private async Task<string> DownloadFallbackOrEmptyAsync(BrickognizeItem item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.ImgUrl)) return string.Empty;

        var key = "bo_" + Md5(item.ImgUrl) + GuessExtension(item.ImgUrl);
        try
        {
            return await _imageCache.GetOrDownloadAsync(item.ImgUrl, key, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageProvider: Brickognize-Fallback-Download fehlgeschlagen {Url}", item.ImgUrl);
            return string.Empty;
        }
    }

    // ========================================================================
    // URL-Kandidaten + Cache-Keys
    // ========================================================================

    /// <summary>
    /// Liste der zu probierenden URLs in Praeferenz-Reihenfolge.
    /// Cache-Key folgt dem Schema:
    ///   part_{bl_part}_c{bl_color}.png
    ///   fig_{bl_minifig}.png
    ///   set_{bl_set}.png
    /// Sonderzeichen in IDs (z.B. "/") werden in cacheKey escaped.
    /// </summary>
    private static IEnumerable<(string url, string cacheKey)> BuildCandidateUrls(
        BrickognizeItemType type, string bricklinkId, int? bricklinkColorId)
    {
        var safeId = SanitizeId(bricklinkId);

        switch (type)
        {
            case BrickognizeItemType.Part:
                if (bricklinkColorId.HasValue)
                {
                    yield return (
                        $"{BlImageBase}PN/{bricklinkColorId.Value}/{bricklinkId}.png",
                        $"part_{safeId}_c{bricklinkColorId.Value}.png");
                }
                yield return (
                    $"{BlImageBase}PN/0/{bricklinkId}.png",
                    $"part_{safeId}_c0.png");
                break;

            case BrickognizeItemType.Minifig:
                yield return (
                    $"{BlImageBase}MN/0/{bricklinkId}.png",
                    $"fig_{safeId}.png");
                break;

            case BrickognizeItemType.Set:
                yield return (
                    $"{BlImageBase}SN/0/{bricklinkId}.png",
                    $"set_{safeId}.png");
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Macht eine ID dateinamen-sicher (Slashes, Doppelpunkte etc. ersetzen).
    /// BL-Part-Nummern enthalten gelegentlich solche Zeichen.
    /// </summary>
    private static string SanitizeId(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }

    /// <summary>MD5-Hex eines Strings (fuer Brickognize-Fallback-Cache-Key).</summary>
    private static string Md5(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = MD5.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Versucht aus der Endung der URL eine sinnvolle Datei-Extension abzuleiten.
    /// Brickognize liefert .webp; wir kennen aber nicht alle Faelle, daher mit
    /// Fallback auf .png.
    /// </summary>
    private static string GuessExtension(string url)
    {
        var lastDot = url.LastIndexOf('.');
        if (lastDot < 0) return ".png";
        var ext = url[lastDot..].ToLowerInvariant();
        // Query-/Fragment-Anteile abschneiden
        var qIdx = ext.IndexOfAny(new[] { '?', '#' });
        if (qIdx >= 0) ext = ext[..qIdx];
        return ext.Length > 1 && ext.Length <= 6 ? ext : ".png";
    }

    // ========================================================================
    // HTTP-Helpers
    // ========================================================================

    private async Task<bool> CheckUrlExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HeadTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (TaskCanceledException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (Exception ex)
        {
            Log.Warning(ex, "ImageProvider: unerwarteter Fehler bei HEAD {Url}", url);
            return false;
        }
    }

    private static BrickognizeItemType ParseType(string raw) => raw.ToLowerInvariant() switch
    {
        "part"    => BrickognizeItemType.Part,
        "fig"     => BrickognizeItemType.Minifig,
        "set"     => BrickognizeItemType.Set,
        "sticker" => BrickognizeItemType.Sticker,
        _         => BrickognizeItemType.Unknown
    };
}
