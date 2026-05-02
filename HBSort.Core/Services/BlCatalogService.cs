using System.Diagnostics;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Models.Exceptions;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des BL-Catalog-Service mit Stale-While-Revalidate.
///
/// Cache-Logik:
///   * GetMinifigDetails: Cache liefern wenn vorhanden und (full ODER nicht stale).
///     Stale full -> refresh via GetItem; nicht-vorhanden -> GetItem; subset reicht
///     aktuell nicht fuer "Details" (in Phase R2-R3 reicht aber subset, weil wir
///     nur Name/Image brauchen – siehe CLAUDE.md "Use-Case mehr braucht").
///   * GetMinifigParts: Cache liefern wenn vorhanden und nicht stale.
///     Stale -> refresh; nicht-vorhanden -> GetSubsets.
///     Beim Refresh: Subsets ersetzen UND uebergreifend bl_items mit Subset-Eintraegen befuellen.
///   * GetPartDetails: subset reicht in dieser Phase. Full-Refresh nur wenn nicht-vorhanden
///     oder stale full.
///   * GetAllColors: 1x holen, dann lebenslang cachen.
///
/// Bei Auth-Fehlern (401/403) WERFEN wir – der Aufrufer (UI) muss reagieren.
/// Bei Rate-Limit (429) oder Netzwerk-Fehlern: stale Cache-Eintrag zurueckgeben,
/// nur warning loggen.
/// </summary>
public class BlCatalogService : IBlCatalogService
{
    private readonly IBlCacheRepository _cache;
    private readonly IBricklinkClient _client;
    private readonly ISettingsService _settings;
    private readonly IBricklinkRateLimiter _rateLimiter;

    public BlCatalogService(
        IBlCacheRepository cache,
        IBricklinkClient client,
        ISettingsService settings,
        IBricklinkRateLimiter rateLimiter)
    {
        _cache = cache;
        _client = client;
        _settings = settings;
        _rateLimiter = rateLimiter;
    }

    private int StaleDays => _settings.Current.Bricklink.CacheStaleDays;

    /// <summary>
    /// Hilfsmethode: Call gegen die BL-API + Rate-Limit-Gate + Tracking + Fehler-Klassifikation.
    /// Wirft BricklinkAuthException wenn 401/403 (Aufrufer muss reagieren).
    /// Wirft RateLimitBlockedException wenn Hard-Limit erreicht ist – Aufrufer kann darauf
    /// mit Cache-Fallback reagieren.
    /// Wirft die Original-Exception bei anderen Fehlern damit der Caller stale-cache servieren kann.
    /// </summary>
    private async Task<T> CallApiTrackedAsync<T>(
        string method, string? itemType, string? itemNo,
        Func<Task<T>> apiCall, CancellationToken ct)
    {
        // 1) Rate-Limit-Gate
        if (!await _rateLimiter.CanMakeCallAsync(ct))
        {
            Log.Warning("BL Rate-Limit blockiert {Method}({Type},{No}) – Hard-Threshold erreicht.",
                method, itemType, itemNo);
            throw new RateLimitBlockedException(
                $"BL Hard-Limit erreicht. Call '{method}' wird blockiert; nur Cache verfuegbar.");
        }

        // 2) Call ausfuehren mit Stopwatch + Fehler-Klassifikation
        var sw = Stopwatch.StartNew();
        int statusCode = 200;
        bool success = false;
        try
        {
            var result = await apiCall();
            success = true;
            return result;
        }
        catch (BricklinkAuthException)
        {
            statusCode = 401;
            throw;
        }
        catch (BricklinkRateLimitException)
        {
            statusCode = 429;
            throw;
        }
        catch (Exception)
        {
            // Andere Fehler: 500-Bucket. Detail-Klassifikation (404 vs Network) ist
            // bei BricklinkSharp aktuell schwierig und nicht entscheidend fuers Tracking.
            statusCode = 500;
            throw;
        }
        finally
        {
            sw.Stop();
            try
            {
                await _rateLimiter.LogCallAsync(method, itemType, itemNo,
                    (int)sw.ElapsedMilliseconds, statusCode, success, ct);
            }
            catch (Exception logEx)
            {
                Log.Warning(logEx, "BL Rate-Limit LogCall geworfen (sollte nicht passieren)");
            }
        }
    }

    // ========================================================================
    // Minifig
    // ========================================================================

    public async Task<BlItem?> GetMinifigDetailsAsync(string blMinifigId, CancellationToken ct = default)
    {
        return await GetItemDetailsCoreAsync("M", blMinifigId, ct);
    }

    public async Task<List<BlSubset>> GetMinifigPartsAsync(string blMinifigId, CancellationToken ct = default)
    {
        // Cache-Hit nur wenn mind. ein Eintrag "echt" ist (nicht aus GetSupersets).
        // Single-Row-Pseudo-Eintraege aus GetSupersets duerfen NICHT als komplette
        // Teileliste interpretiert werden.
        var cached = await _cache.GetSubsetsAsync("M", blMinifigId, ct);
        var hasRealEntries = cached.Count > 0 && cached.Any(s => !s.IsFromSupersets);
        if (hasRealEntries)
        {
            var oldest = cached.Min(s => s.FetchedAt);
            var ageDays = (DateTime.UtcNow - oldest).TotalDays;
            if (ageDays <= StaleDays)
            {
                Log.Debug("BL-Cache-Hit Subsets M:{No} ({Count} Teile, {Days:F1}d alt)",
                    blMinifigId, cached.Count, ageDays);
                return cached;
            }
            Log.Debug("BL-Cache stale Subsets M:{No} ({Days:F1}d) – refresh", blMinifigId, ageDays);
        }
        else if (cached.Count > 0)
        {
            // Vorhanden, aber alle Eintraege aus GetSupersets -> Liste ist unvollstaendig.
            Log.Debug("BL-Cache nur Pseudo-Eintraege M:{No} ({Count}) – force-fetch", blMinifigId, cached.Count);
        }

        // Cache-Miss oder stale -> BL-Call (durch Rate-Limiter geleitet)
        try
        {
            var dtoList = await CallApiTrackedAsync(
                "GetSubsets", "M", blMinifigId,
                () => _client.GetSubsetsAsync("M", blMinifigId, breakMinifigs: false, ct),
                ct);

            var subsets = dtoList.Select(d => new BlSubset
            {
                ParentType = "M",
                ParentNo = blMinifigId,
                ItemType = d.ItemType,
                ItemNo = d.ItemNo,
                ColorId = d.ColorId,
                Quantity = d.Quantity,
                ExtraQuantity = d.ExtraQuantity,
                IsAlternate = d.IsAlternate,
                IsCounterpart = d.IsCounterpart,
                MatchId = d.MatchId,
                FetchedAt = DateTime.UtcNow,
                IsFromSupersets = false  // echte Daten aus GetSubsets
            }).ToList();

            await _cache.ReplaceSubsetsAsync("M", blMinifigId, subsets, ct);

            // UEBERGREIFEND: enthaltene Teile in bl_items als 'subset' cachen.
            // Schutz-Regel im Repo verhindert dass full-Daten ueberschrieben werden.
            var coCachedItems = dtoList
                .Where(d => !string.IsNullOrEmpty(d.ItemNo))
                .GroupBy(d => (d.ItemType, d.ItemNo))
                .Select(g => new BlItem
                {
                    ItemType = g.Key.ItemType,
                    ItemNo = g.Key.ItemNo,
                    Name = g.First().ItemName ?? string.Empty,
                    DataCompleteness = DataCompleteness.Subset,
                    FetchedAt = DateTime.UtcNow
                })
                .ToList();
            if (coCachedItems.Count > 0)
            {
                await _cache.UpsertItemsAsync(coCachedItems, ct);
                Log.Debug("BL Subset->Items uebergreifend gecacht: {Count}", coCachedItems.Count);
            }

            return subsets;
        }
        catch (BricklinkAuthException)
        {
            throw;
        }
        catch (RateLimitBlockedException)
        {
            // Eigener Hard-Stop -> stale Cache liefern, KEIN Throw
            Log.Warning("BL Hard-Limit blockt GetSubsets({No}) – serve stale cache ({Count})",
                blMinifigId, cached.Count);
            return cached;
        }
        catch (BricklinkRateLimitException ex)
        {
            Log.Warning(ex, "BL Rate-Limit beim GetSubsets({No}) – serve stale cache", blMinifigId);
            return cached;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL GetSubsets({No}) fehlgeschlagen – serve stale cache", blMinifigId);
            return cached;
        }
    }

    // ========================================================================
    // Part
    // ========================================================================

    public async Task<BlItem?> GetPartDetailsAsync(string blPartNo, CancellationToken ct = default)
    {
        return await GetItemDetailsCoreAsync("P", blPartNo, ct);
    }

    /// <summary>
    /// Gemeinsame Lookup-Logik fuer Items (Minifig + Part + Set).
    /// Phase R2/R3: ein Subset-Eintrag reicht zur Anzeige (Name, Image-URL aus dem Cache,
    /// Image kommt eh ueber den BricklinkImageProvider – nicht ueber das Item-Objekt).
    /// Daher: subset = OK; full hat Vorrang wenn ohnehin frisch.
    /// </summary>
    private async Task<BlItem?> GetItemDetailsCoreAsync(string itemType, string itemNo, CancellationToken ct)
    {
        var cached = await _cache.GetItemAsync(itemType, itemNo, ct);
        if (cached != null)
        {
            var age = DateTime.UtcNow - cached.FetchedAt;
            var stale = age.TotalDays > StaleDays;

            // Subset-Daten zaehlen NICHT als stale (sie sind eh nur ein Schnellzugriff –
            // Phase R2-R3 nutzt sie nur fuer Name; ein "echter" Refresh ist nicht noetig).
            if (cached.DataCompleteness == DataCompleteness.Subset)
            {
                Log.Debug("BL-Cache subset-hit {Type}:{No}", itemType, itemNo);
                return cached;
            }

            if (!stale)
            {
                Log.Debug("BL-Cache full-hit {Type}:{No}", itemType, itemNo);
                return cached;
            }
            Log.Debug("BL-Cache stale full {Type}:{No} ({Days:F1}d) – refresh", itemType, itemNo, age.TotalDays);
        }

        // Cache-Miss oder stale full -> BL-Call (durch Rate-Limiter geleitet)
        try
        {
            var dto = await CallApiTrackedAsync(
                "GetItem", itemType, itemNo,
                () => _client.GetItemAsync(itemType, itemNo, ct),
                ct);

            var item = new BlItem
            {
                ItemType = itemType,
                ItemNo = dto.ItemNo,
                Name = dto.Name,
                YearReleased = dto.YearReleased,
                ImageUrl = dto.ImageUrl,
                Weight = dto.Weight,
                DimX = dto.DimX,
                DimY = dto.DimY,
                DimZ = dto.DimZ,
                CategoryId = dto.CategoryId,
                JsonFull = dto.RawJson,
                DataCompleteness = DataCompleteness.Full,
                FetchedAt = DateTime.UtcNow
            };
            await _cache.UpsertItemAsync(item, ct);
            return item;
        }
        catch (BricklinkAuthException)
        {
            throw;
        }
        catch (RateLimitBlockedException)
        {
            Log.Warning("BL Hard-Limit blockt GetItem({Type},{No}) – serve {What} cache",
                itemType, itemNo, cached == null ? "no" : "stale");
            return cached;
        }
        catch (BricklinkRateLimitException ex)
        {
            Log.Warning(ex, "BL Rate-Limit bei GetItem({Type},{No}) – serve stale cache", itemType, itemNo);
            return cached;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL GetItem({Type},{No}) fehlgeschlagen", itemType, itemNo);
            return cached;
        }
    }

    // ========================================================================
    // Colors
    // ========================================================================

    public async Task<List<BlColor>> GetAllColorsAsync(CancellationToken ct = default)
    {
        var cached = await _cache.GetAllColorsAsync(ct);
        if (cached.Count > 0) return cached;

        // Erstbefuellung (durch Rate-Limiter geleitet)
        try
        {
            var dtoList = await CallApiTrackedAsync(
                "GetColorList", null, null,
                () => _client.GetColorListAsync(ct),
                ct);

            var colors = dtoList.Select(d => new BlColor
            {
                ColorId = d.ColorId,
                Name = d.Name,
                Rgb = d.Rgb,
                Type = d.Type,
                FetchedAt = DateTime.UtcNow
            }).ToList();
            await _cache.UpsertColorsAsync(colors, ct);
            Log.Information("BL-Color-Liste initial gecacht ({Count} Farben)", colors.Count);
            return colors;
        }
        catch (BricklinkAuthException)
        {
            throw;
        }
        catch (RateLimitBlockedException)
        {
            Log.Warning("BL Hard-Limit blockt GetColorList – Cache bleibt leer");
            return cached;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL GetColorList fehlgeschlagen – Cache bleibt leer");
            return cached;
        }
    }

    // ========================================================================
    // Reverse-Lookup
    // ========================================================================

    public async Task<List<string>> FindWaitingMinifigsForPartAsync(
        string blPartNo, int blColorId, IEnumerable<string> waitingMinifigIds, CancellationToken ct = default)
    {
        var parents = await _cache.FindParentsByItemAsync("P", blPartNo, blColorId, ct);
        // Schnittmenge mit den uebergebenen wartenden Figuren
        var waitingSet = waitingMinifigIds.ToHashSet();
        return parents.Where(p => waitingSet.Contains(p)).ToList();
    }

    // ========================================================================
    // Cache-Verwaltung
    // ========================================================================

    public Task<BlCacheStats> GetCacheStatsAsync(CancellationToken ct = default) => _cache.GetStatsAsync(ct);

    public Task ClearCacheAsync(CancellationToken ct = default) => _cache.ClearAllAsync(ct);

    public Task<int> ClearStaleAsync(CancellationToken ct = default) => _cache.ClearStaleAsync(StaleDays, ct);

    // ========================================================================
    // Phase 5: Supersets + KnownColors + EnsureFullSubsets
    // ========================================================================

    public async Task<List<BlSubset>> GetSupersetsAsync(string blPartNo, int blColorId, CancellationToken ct = default)
    {
        // Cache-First: bl_subsets mit item_no=blPartNo + color_id=blColorId. Es existiert
        // kein "primary key" auf der Reverse-Seite – wir treffen die Eintraege ueber Index.
        // Stale-Check pro Eintrag (oldest); wenn alles frisch -> Cache-Hit reicht.
        var cachedParents = await _cache.FindParentsByItemAsync("P", blPartNo, blColorId, ct);
        var cachedSubsets = new List<BlSubset>();
        foreach (var parentNo in cachedParents)
        {
            // Wir holen die Subsets-Eintraege fuer Parent=M:parentNo und filtern auf das gesuchte Teil.
            var parentSubsets = await _cache.GetSubsetsAsync("M", parentNo, ct);
            cachedSubsets.AddRange(parentSubsets.Where(s =>
                s.ItemNo == blPartNo && s.ColorId == blColorId));
        }

        // Heuristik: wenn wir Eintraege haben UND der aelteste nicht stale ist -> Cache-Hit.
        // Reverse-Lookup im Cache liefert ggf. nur die Treffer, die ueber Subsets-Calls
        // bereits angekommen sind. Das sind ggf. nicht ALLE Minifigs (nur die, deren
        // Teileliste schon mal abgefragt wurde). Daher trotzdem ggf. BL-Call.
        if (cachedSubsets.Count > 0)
        {
            var oldest = cachedSubsets.Min(s => s.FetchedAt);
            var ageDays = (DateTime.UtcNow - oldest).TotalDays;
            if (ageDays <= StaleDays)
            {
                Log.Debug("BL-Cache-Hit Supersets P:{No}/C:{C} ({Count} Parents, {Days:F1}d)",
                    blPartNo, blColorId, cachedSubsets.Count, ageDays);
                return cachedSubsets;
            }
        }

        try
        {
            var dtoList = await CallApiTrackedAsync(
                "GetSupersets", "P", blPartNo,
                () => _client.GetSupersetsAsync("P", blPartNo, blColorId, ct),
                ct);

            // Filter: nur Minifig-Parents in dieser Phase. Sets ('S') ueberspringen wir,
            // weil unser Workflow Minifigs sammelt.
            var minifigEntries = dtoList.Where(d => d.ParentType == "M").ToList();

            // Cache-Updates:
            // - Pro Parent: einen bl_subsets-Eintrag fuer (M, parentNo, P, blPartNo, blColorId, qty).
            //   Wir koennen NICHT die ganze Subsets-Liste der Minifig ableiten (kennen wir nicht aus
            //   der Supersets-Antwort), sondern nur diesen einen Eintrag schreiben. Das mischt
            //   sich mit echten "vollstaendigen" Eintraegen aus GetSubsets – das ist OK,
            //   aber wir muessen aufpassen das nicht zu ueberschreiben.
            // - Items: Parent als 'subset'-Eintrag.
            foreach (var entry in minifigEntries)
            {
                ct.ThrowIfCancellationRequested();

                // "Komplett" heisst: existiert mind. ein "echter" Eintrag (nicht IsFromSupersets).
                // Eine Minifig kann auch nur 1 Teil haben, deshalb darf Count==1 NICHT
                // automatisch als "komplett" interpretiert werden.
                var existing = await _cache.GetSubsetsAsync("M", entry.ParentNo, ct);
                bool alreadyComplete = existing.Count > 0 && existing.Any(s => !s.IsFromSupersets);
                if (!alreadyComplete)
                {
                    var single = new BlSubset
                    {
                        ParentType = "M",
                        ParentNo = entry.ParentNo,
                        ItemType = "P",
                        ItemNo = blPartNo,
                        ColorId = blColorId,
                        Quantity = entry.Quantity,
                        ExtraQuantity = 0,
                        IsAlternate = entry.AppearsAs == "Alternate",
                        IsCounterpart = entry.AppearsAs == "Counterpart",
                        MatchId = 0,
                        FetchedAt = DateTime.UtcNow,
                        // WICHTIG: Single-Row-Eintrag aus Supersets ist KEINE komplette
                        // Teileliste. Beim spaeteren GetMinifigPartsAsync MUSS die echte
                        // Liste via API geholt werden.
                        IsFromSupersets = true
                    };
                    await _cache.ReplaceSubsetsAsync("M", entry.ParentNo, new[] { single }, ct);
                }

                // Item (Parent) im bl_items-Cache als 'subset'.
                await _cache.UpsertItemAsync(new BlItem
                {
                    ItemType = "M",
                    ItemNo = entry.ParentNo,
                    Name = entry.ParentName ?? string.Empty,
                    DataCompleteness = DataCompleteness.Subset,
                    FetchedAt = DateTime.UtcNow
                }, ct);
            }

            // Resultat: alle minifigEntries als BlSubset (auch wenn der Cache "alreadyComplete" hatte,
            // muessen wir den Treffer in der Liste haben).
            var result = minifigEntries.Select(e => new BlSubset
            {
                ParentType = "M",
                ParentNo = e.ParentNo,
                ItemType = "P",
                ItemNo = blPartNo,
                ColorId = blColorId,
                Quantity = e.Quantity,
                IsAlternate = e.AppearsAs == "Alternate",
                IsCounterpart = e.AppearsAs == "Counterpart",
                FetchedAt = DateTime.UtcNow
            }).ToList();

            Log.Information("BL Supersets P:{No}/C:{C} -> {Count} Minifig-Treffer", blPartNo, blColorId, result.Count);
            return result;
        }
        catch (BricklinkAuthException)
        {
            throw;
        }
        catch (RateLimitBlockedException)
        {
            Log.Warning("BL Hard-Limit blockt GetSupersets({No},{C}) – serve cache ({Count})",
                blPartNo, blColorId, cachedSubsets.Count);
            return cachedSubsets;
        }
        catch (BricklinkRateLimitException ex)
        {
            Log.Warning(ex, "BL Rate-Limit beim GetSupersets({No},{C}) – serve cache", blPartNo, blColorId);
            return cachedSubsets;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL GetSupersets({No},{C}) fehlgeschlagen – serve cache", blPartNo, blColorId);
            return cachedSubsets;
        }
    }

    public async Task<bool> EnsureFullSubsetsAsync(string blMinifigId, CancellationToken ct = default)
    {
        // Vollstaendigkeit anhand des IsFromSupersets-Flags pruefen, NICHT anhand der
        // Anzahl. Single-Row-Eintraege aus GetSupersets sind Pseudo-Daten (nur ein Teil)
        // und duerfen NICHT als komplette Teileliste gelten – sonst wuerde "Diese Figur
        // anlegen" eine 1/1-Pseudo-Figur produzieren.
        var cached = await _cache.GetSubsetsAsync("M", blMinifigId, ct);
        var allReal = cached.Count > 0 && cached.All(s => !s.IsFromSupersets);
        if (allReal)
        {
            var oldest = cached.Min(s => s.FetchedAt);
            var ageDays = (DateTime.UtcNow - oldest).TotalDays;
            if (ageDays <= StaleDays) return true;
        }

        Log.Information("EnsureFullSubsets({No}): force-fetch (cached={Count}, allReal={Real})",
            blMinifigId, cached.Count, allReal);

        // Sonst BL-Call (laeuft ueber Rate-Limiter im GetMinifigPartsAsync)
        var subs = await GetMinifigPartsAsync(blMinifigId, ct);
        return subs.Count > 0;
    }

    public Task<List<BlMinifigSubsetMatch>> FindMinifigsContainingPartAsync(
        string blPartNo, int blColorId, CancellationToken ct = default)
        => _cache.FindMinifigsContainingPartAsync(blPartNo, blColorId, ct);

    public async Task<List<BlColor>> GetKnownColorsAsync(string blPartNo, CancellationToken ct = default)
    {
        // 1) Cache-Hit?
        var cachedFetched = await _cache.GetKnownColorsFetchedAtAsync(blPartNo, ct);
        if (cachedFetched.HasValue)
        {
            var ageDays = (DateTime.UtcNow - cachedFetched.Value).TotalDays;
            if (ageDays <= StaleDays)
            {
                var cachedIds = await _cache.GetKnownColorIdsAsync(blPartNo, ct);
                var allColors = await _cache.GetAllColorsAsync(ct);
                var idSet = new HashSet<int>(cachedIds);
                return allColors.Where(c => idSet.Contains(c.ColorId)).ToList();
            }
        }

        // 2) Cache-Miss / stale -> BL-Call
        try
        {
            var dtoList = await CallApiTrackedAsync(
                "GetKnownColors", "P", blPartNo,
                () => _client.GetKnownColorsAsync("P", blPartNo, ct),
                ct);

            var colorIds = dtoList.Select(d => d.ColorId).Distinct().ToList();
            await _cache.ReplaceKnownColorsAsync(blPartNo, colorIds, ct);

            var allColors = await _cache.GetAllColorsAsync(ct);
            var idSet = new HashSet<int>(colorIds);
            var result = allColors.Where(c => idSet.Contains(c.ColorId)).ToList();
            Log.Information("BL KnownColors P:{No} -> {Count} Farben", blPartNo, result.Count);
            return result;
        }
        catch (BricklinkAuthException) { throw; }
        catch (RateLimitBlockedException)
        {
            Log.Warning("BL Hard-Limit blockt GetKnownColors({No}) – serve cache (ggf. leer)", blPartNo);
            var fallbackIds = await _cache.GetKnownColorIdsAsync(blPartNo, ct);
            var allColors = await _cache.GetAllColorsAsync(ct);
            var idSet = new HashSet<int>(fallbackIds);
            return allColors.Where(c => idSet.Contains(c.ColorId)).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BL GetKnownColors({No}) fehlgeschlagen – serve cache", blPartNo);
            var fallbackIds = await _cache.GetKnownColorIdsAsync(blPartNo, ct);
            var allColors = await _cache.GetAllColorsAsync(ct);
            var idSet = new HashSet<int>(fallbackIds);
            return allColors.Where(c => idSet.Contains(c.ColorId)).ToList();
        }
    }
}
