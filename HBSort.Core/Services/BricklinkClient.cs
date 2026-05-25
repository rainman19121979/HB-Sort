using System.Diagnostics;
using System.Net;
using BricklinkSharp.Client;
using HBSort.Core.Models.Exceptions;
using Serilog;

namespace HBSort.Core.Services;

/// <summary>
/// Default-Implementierung des BL-Clients ueber BricklinkSharp.
///
/// Architektur-Hinweis: BricklinkSharp nutzt einen globalen statischen
/// <see cref="BricklinkClientConfiguration"/>-Singleton. Wir setzen die
/// Konfiguration vor jedem Client-Build frisch (Lock fuer parallel-safety),
/// damit Token-Aenderungen via Settings sofort wirksam werden.
/// </summary>
public class BricklinkClient : IBricklinkClient
{
    private readonly IBricklinkTokenStorage _tokenStorage;
    private readonly IBricklinkRateLimiter? _rateLimiter;
    private readonly object _configLock = new();

    /// <summary>
    /// Konstruktor. Der RateLimiter ist optional damit Tests einen Client ohne
    /// Limiter bauen koennen; in der Production-DI wird er immer gesetzt.
    /// Wenn vorhanden, zaehlt jeder TestConnection-Call mit (LogCall nach Erfolg/Fehler).
    /// GetItem/GetSubsets/GetColorList werden ueber den BlCatalogService bereits
    /// getrackt - dort wuerden wir doppelt zaehlen, wenn der Client das auch tun wuerde.
    /// Daher: TestConnection ist die EINZIGE Methode hier mit eigenem Tracking.
    /// </summary>
    public BricklinkClient(IBricklinkTokenStorage tokenStorage, IBricklinkRateLimiter? rateLimiter = null)
    {
        _tokenStorage = tokenStorage;
        _rateLimiter = rateLimiter;
    }

    public async Task<bool> IsConfiguredAsync()
    {
        if (!_tokenStorage.HasTokens()) return false;
        try
        {
            var tokens = await _tokenStorage.LoadAsync();
            return tokens != null && tokens.IsComplete;
        }
        catch
        {
            return false;
        }
    }

    public async Task<BricklinkTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        // Rate-Limit-Gate: ein TestConnection ist ein echter BL-Call und zaehlt mit.
        if (_rateLimiter != null && !await _rateLimiter.CanMakeCallAsync(ct))
        {
            return new BricklinkTestResult
            {
                Success = false,
                ErrorMessage = "Eigener Hard-Limit erreicht. Test waere ein API-Call und wird blockiert."
            };
        }

        var sw = Stopwatch.StartNew();
        BricklinkSharp.Client.IBricklinkClient? client = null;
        int statusCode = 200;
        bool success = false;
        try
        {
            client = await CreateClientAsync();
            var item = await client.GetItemAsync(ItemType.Minifig, "arc007");
            sw.Stop();
            success = true;

            // BL-API liefert Namen HTML-kodiert (z.B. &#40; statt "(") - an dieser
            // Boundary einmal dekodieren, damit alles dahinter sauber ist.
            var name = WebUtility.HtmlDecode(item?.Name ?? "(no name)");
            Log.Information("BL TestConnection erfolgreich, ItemName='{Name}' ({Ms} ms)", name, sw.ElapsedMilliseconds);

            return new BricklinkTestResult
            {
                Success = true,
                ItemName = name,
                ResponseTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (BricklinkAuthException ex)
        {
            sw.Stop();
            statusCode = 401;
            Log.Warning(ex, "BL TestConnection: Auth fehlt/falsch");
            return new BricklinkTestResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ResponseTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Wenn die Exception einen erkennbaren HTTP-Status traegt, uebernehmen wir ihn.
            statusCode = (ex.Message ?? "").Contains("403") ? 403
                       : (ex.Message ?? "").Contains("429") ? 429
                       : 500;
            Log.Warning(ex, "BL TestConnection fehlgeschlagen");
            return new BricklinkTestResult
            {
                Success = false,
                ErrorMessage = TranslateError(ex),
                ResponseTimeMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            client?.Dispose();

            // Auch fehlgeschlagene Calls zaehlen mit (BL zaehlt sie laut Doku auch).
            if (_rateLimiter != null)
            {
                try
                {
                    await _rateLimiter.LogCallAsync("TestConnection", "M", "arc007",
                        (int)sw.ElapsedMilliseconds, statusCode, success, ct);
                }
                catch (Exception logEx)
                {
                    Log.Warning(logEx, "BL TestConnection LogCall geworfen");
                }
            }
        }
    }

    // ========================================================================
    // Phase R2: GetItem / GetSubsets / GetColorList
    // ========================================================================

    public async Task<BricklinkItemDto> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = await CreateClientAsync();
        try
        {
            var blType = ParseItemType(itemType);
            var item = await client.GetItemAsync(blType, itemNo);
            sw.Stop();
            Log.Information("BL GetItem({Type}, {No}) -> '{Name}' ({Ms} ms)",
                itemType, itemNo, item?.Name, sw.ElapsedMilliseconds);

            if (item == null)
            {
                throw new InvalidOperationException($"BL GetItem({itemType},{itemNo}) lieferte null.");
            }

            return new BricklinkItemDto
            {
                ItemType = itemType,
                ItemNo = item.Number ?? itemNo,
                // HTML-Decode: BL liefert Namen mit Numeric-Entities (z.B. &#40;) - einmalig
                // an der API-Boundary dekodieren, damit Cache und UI sauber sind.
                Name = WebUtility.HtmlDecode(item.Name ?? string.Empty),
                YearReleased = item.YearReleased > 0 ? item.YearReleased : null,
                // BricklinkSharp liefert "https://..." mit ggf. fuehrendem Slash - wir nehmen ImageUrl roh.
                ImageUrl = item.ImageUrl,
                Weight = SafeDouble(item.Weight),
                DimX = SafeDouble(item.DimX),
                DimY = SafeDouble(item.DimY),
                DimZ = SafeDouble(item.DimZ),
                CategoryId = item.CategoryId > 0 ? item.CategoryId : null,
                RawJson = null
            };
        }
        catch (Exception ex) when (!(ex is BricklinkAuthException))
        {
            sw.Stop();
            throw TranslateBricklinkException(ex);
        }
    }

    public async Task<List<BricklinkSubsetDto>> GetSubsetsAsync(
        string itemType, string itemNo, bool breakMinifigs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = await CreateClientAsync();
        try
        {
            var blType = ParseItemType(itemType);
            var subsetEntries = await client.GetSubsetsAsync(blType, itemNo, breakMinifigs: breakMinifigs);
            sw.Stop();
            Log.Information("BL GetSubsets({Type}, {No}) -> {Count} Match-Groups ({Ms} ms)",
                itemType, itemNo, subsetEntries?.Length ?? 0, sw.ElapsedMilliseconds);

            var result = new List<BricklinkSubsetDto>();
            if (subsetEntries == null) return result;

            foreach (var entry in subsetEntries)
            {
                if (entry.Entries == null || entry.Entries.Length == 0) continue;

                // entry.MatchNo gruppiert Match-Alternative Teile.
                // Erster Eintrag pro Match-Group ist der "primaere", Rest sind Alternativen.
                bool isFirstInGroup = true;
                foreach (var sub in entry.Entries)
                {
                    if (sub.Item == null) continue;
                    var typeStr = ItemTypeToShortString(sub.Item.Type);

                    result.Add(new BricklinkSubsetDto
                    {
                        ItemType = typeStr,
                        ItemNo = sub.Item.Number ?? string.Empty,
                        ColorId = sub.ColorId,
                        Quantity = sub.Quantity,
                        ExtraQuantity = sub.ExtraQuantity,
                        // Wenn die Match-Group mehrere Eintraege hat, sind alle nach dem
                        // ersten alternativ. BL liefert "IsAlternate" auf der Eintragsebene
                        // bereits korrekt fuer einige Faelle - wir kombinieren zur Sicherheit.
                        IsAlternate = !isFirstInGroup || sub.IsAlternate,
                        IsCounterpart = sub.IsCounterpart,
                        MatchId = entry.MatchNo,
                        // HTML-Decode an der API-Boundary (siehe GetItemAsync).
                        ItemName = WebUtility.HtmlDecode(sub.Item.Name)
                    });
                    isFirstInGroup = false;
                }
            }

            return result;
        }
        catch (Exception ex) when (!(ex is BricklinkAuthException))
        {
            sw.Stop();
            throw TranslateBricklinkException(ex);
        }
    }

    public async Task<List<BricklinkColorDto>> GetColorListAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = await CreateClientAsync();
        try
        {
            var colors = await client.GetColorListAsync();
            sw.Stop();
            Log.Information("BL GetColorList -> {Count} Farben ({Ms} ms)",
                colors?.Length ?? 0, sw.ElapsedMilliseconds);

            var result = new List<BricklinkColorDto>();
            if (colors == null) return result;
            foreach (var c in colors)
            {
                // BricklinkSharp-Color: ColorId, Name, HtmlCode (RGB-Hex ohne #), Type
                result.Add(new BricklinkColorDto
                {
                    ColorId = c.ColorId,
                    // HTML-Decode an der API-Boundary (siehe GetItemAsync).
                    Name = WebUtility.HtmlDecode(c.Name ?? string.Empty),
                    Rgb = c.HtmlCode,
                    Type = c.Type
                });
            }
            return result;
        }
        catch (Exception ex) when (!(ex is BricklinkAuthException))
        {
            sw.Stop();
            throw TranslateBricklinkException(ex);
        }
    }

    // ========================================================================
    // Phase 5: Supersets + KnownColors
    // ========================================================================

    public async Task<List<BricklinkSupersetEntryDto>> GetSupersetsAsync(
        string itemType, string itemNo, int colorId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = await CreateClientAsync();
        try
        {
            var blType = ParseItemType(itemType);
            // BricklinkSharp: GetSupersetsAsync(type, no, colorId) -> Superset[]
            var supersets = await client.GetSupersetsAsync(blType, itemNo, colorId);
            sw.Stop();
            var totalEntries = supersets?.Sum(s => s.Entries?.Length ?? 0) ?? 0;
            Log.Information("BL GetSupersets({Type}, {No}, color={Color}) -> {Total} Eintraege ({Ms} ms)",
                itemType, itemNo, colorId, totalEntries, sw.ElapsedMilliseconds);

            var result = new List<BricklinkSupersetEntryDto>();
            if (supersets == null) return result;

            foreach (var sup in supersets)
            {
                if (sup.Entries == null) continue;
                foreach (var e in sup.Entries)
                {
                    if (e.Item == null) continue;
                    var typeStr = ItemTypeToShortString(e.Item.Type);
                    result.Add(new BricklinkSupersetEntryDto
                    {
                        ParentType = typeStr,
                        ParentNo = e.Item.Number ?? string.Empty,
                        // HTML-Decode an der API-Boundary (siehe GetItemAsync).
                        ParentName = WebUtility.HtmlDecode(e.Item.Name),
                        Quantity = e.Quantity,
                        AppearsAs = e.AppearsAs.ToString(),
                        RequestedColorId = sup.ColorId
                    });
                }
            }
            return result;
        }
        catch (Exception ex) when (!(ex is BricklinkAuthException))
        {
            sw.Stop();
            throw TranslateBricklinkException(ex);
        }
    }

    public async Task<List<BricklinkKnownColorDto>> GetKnownColorsAsync(
        string itemType, string itemNo, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = await CreateClientAsync();
        try
        {
            var blType = ParseItemType(itemType);
            var known = await client.GetKnownColorsAsync(blType, itemNo);
            sw.Stop();
            Log.Information("BL GetKnownColors({Type}, {No}) -> {Count} Farben ({Ms} ms)",
                itemType, itemNo, known?.Length ?? 0, sw.ElapsedMilliseconds);

            var result = new List<BricklinkKnownColorDto>();
            if (known == null) return result;
            foreach (var k in known)
            {
                result.Add(new BricklinkKnownColorDto
                {
                    ColorId = k.ColorId,
                    Quantity = k.Quantity
                });
            }
            return result;
        }
        catch (Exception ex) when (!(ex is BricklinkAuthException))
        {
            sw.Stop();
            throw TranslateBricklinkException(ex);
        }
    }

    // ========================================================================
    // v0.1.24-beta.6 Phase 1: BL-Store-Inventar
    // ========================================================================

    public async Task<List<BricklinkInventoryLotDto>> GetInventoryAsync(CancellationToken ct = default)
    {
        // Rate-Limit-Gate: GetInventoryList ist ein echter BL-Call. Analog
        // zu TestConnectionAsync zaehlen wir den Aufruf mit (auch fehlgeschlagene
        // Calls, BL zaehlt sie ebenfalls).
        if (_rateLimiter != null && !await _rateLimiter.CanMakeCallAsync(ct))
        {
            throw new BricklinkRateLimitException(
                "Eigener Hard-Limit erreicht - Inventar-Sync wird blockiert. " +
                "Bitte spaeter erneut versuchen oder Hard-Threshold in den Einstellungen anpassen.");
        }

        var sw = Stopwatch.StartNew();
        var client = await CreateClientAsync();
        int statusCode = 200;
        bool success = false;
        try
        {
            // BricklinkSharp.IBricklinkClient.GetInventoryListAsync() liefert
            // alle Lots des Stores als Inventory[]. Phase 1 nutzt KEINE Filter -
            // wir holen den kompletten Bestand und speichern ihn lokal.
            var lots = await client.GetInventoryListAsync();
            sw.Stop();
            success = true;
            Log.Information("BL GetInventoryList -> {Count} Lots ({Ms} ms)",
                lots?.Length ?? 0, sw.ElapsedMilliseconds);

            var result = new List<BricklinkInventoryLotDto>();
            if (lots == null) return result;
            foreach (var lot in lots)
            {
                if (lot.Item == null) continue;
                var typeStr = ItemTypeToShortString(lot.Item.Type);
                // BL liefert ColorId=0 fuer farblose Items (Minifig/Set/Box) -
                // wir normalisieren das auf null fuer die EF-Spalte.
                int? colorId = lot.ColorId > 0 ? lot.ColorId : (int?)null;
                result.Add(new BricklinkInventoryLotDto
                {
                    LotId = lot.InventoryId,
                    ItemType = typeStr,
                    ItemNo = lot.Item.Number ?? string.Empty,
                    ColorId = colorId,
                    ColorName = colorId.HasValue
                        ? WebUtility.HtmlDecode(lot.ColorName ?? string.Empty)
                        : null,
                    // Description ist die User-Beschreibung des Lots im Store.
                    // HTML-Decode an der API-Boundary (siehe GetItemAsync).
                    Description = string.IsNullOrEmpty(lot.Description)
                        ? null
                        : WebUtility.HtmlDecode(lot.Description),
                    Quantity = lot.Quantity,
                    UnitPrice = lot.UnitPrice,
                    Condition = ConditionToShortString(lot.Condition)
                });
            }
            return result;
        }
        catch (Exception ex) when (!(ex is BricklinkAuthException))
        {
            sw.Stop();
            var msg = ex.Message ?? string.Empty;
            statusCode = msg.Contains("401") ? 401
                       : msg.Contains("403") ? 403
                       : msg.Contains("429") ? 429
                       : 500;
            throw TranslateBricklinkException(ex);
        }
        finally
        {
            client?.Dispose();
            if (_rateLimiter != null)
            {
                try
                {
                    await _rateLimiter.LogCallAsync("GetInventoryList", "-", "-",
                        (int)sw.ElapsedMilliseconds, statusCode, success, ct);
                }
                catch (Exception logEx)
                {
                    Log.Warning(logEx, "BL GetInventoryList LogCall geworfen");
                }
            }
        }
    }

    /// <summary>
    /// Mappt BricklinkSharp.Condition (Enum: New/Used) auf das BL-Standard-
    /// Kuerzel "N"/"U". Default "U" fuer Unbekannt - in der Praxis liefert die
    /// API immer einen der zwei Werte.
    /// </summary>
    private static string ConditionToShortString(Condition c) => c switch
    {
        Condition.New  => "N",
        Condition.Used => "U",
        _              => "U"
    };

    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>
    /// Erzeugt einen BricklinkSharp-Client mit der aktuellen Token-Konfiguration.
    /// Wirft BricklinkAuthException wenn Tokens fehlen oder nicht entschluesselbar sind.
    /// </summary>
    private async Task<BricklinkSharp.Client.IBricklinkClient> CreateClientAsync()
    {
        if (!_tokenStorage.HasTokens())
        {
            throw new BricklinkAuthException(
                "Keine BrickLink-Tokens konfiguriert. Bitte in den Einstellungen unter 'BrickLink-API' eingeben.");
        }

        var tokens = await _tokenStorage.LoadAsync()
            ?? throw new BricklinkAuthException("BrickLink-Tokens konnten nicht geladen werden.");

        if (!tokens.IsComplete)
        {
            throw new BricklinkAuthException("BrickLink-Tokens sind unvollstaendig (alle vier Felder muessen befuellt sein).");
        }

        lock (_configLock)
        {
            BricklinkClientConfiguration.Instance.TokenValue = tokens.TokenValue;
            BricklinkClientConfiguration.Instance.TokenSecret = tokens.TokenSecret;
            BricklinkClientConfiguration.Instance.ConsumerKey = tokens.ConsumerKey;
            BricklinkClientConfiguration.Instance.ConsumerSecret = tokens.ConsumerSecret;
        }

        return BricklinkClientFactory.Build();
    }

    /// <summary>
    /// Wandelt unsere kurzen Type-Strings ('M','P','S','B','G','I','O','C') in den
    /// BricklinkSharp-Enum. Wir akzeptieren auch Langformen ('Minifig', 'Part'...) -
    /// macht das Aufrufer-Coden einfacher.
    /// </summary>
    private static ItemType ParseItemType(string s) => (s ?? "").ToUpperInvariant() switch
    {
        "M" or "MINIFIG"     => ItemType.Minifig,
        "P" or "PART"        => ItemType.Part,
        "S" or "SET"         => ItemType.Set,
        "B" or "BOOK"        => ItemType.Book,
        "G" or "GEAR"        => ItemType.Gear,
        "I" or "INSTRUCTION" => ItemType.Instruction,
        "O" or "ORIGINALBOX" or "ORIGINAL_BOX" => ItemType.OriginalBox,
        "C" or "CATALOG"     => ItemType.Catalog,
        _ => throw new ArgumentException($"Unbekannter ItemType '{s}'", nameof(s))
    };

    private static string ItemTypeToShortString(ItemType t) => t switch
    {
        ItemType.Minifig     => "M",
        ItemType.Part        => "P",
        ItemType.Set         => "S",
        ItemType.Book        => "B",
        ItemType.Gear        => "G",
        ItemType.Instruction => "I",
        ItemType.OriginalBox => "O",
        ItemType.Catalog     => "C",
        _                    => "?"
    };

    /// <summary>Konvertiert Doubles, behandelt NaN/0 als nicht-vorhanden.</summary>
    private static double? SafeDouble(double v)
    {
        if (double.IsNaN(v) || v == 0) return null;
        return v;
    }

    /// <summary>
    /// Uebersetzt Exceptions aus BricklinkSharp in unsere Custom-Exceptions.
    /// BricklinkSharp wirft eine eigene BricklinkException-Hierarchie; wir
    /// erkennen den HTTP-Status anhand der Message + InnerException.
    /// </summary>
    private static Exception TranslateBricklinkException(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("401")) return new BricklinkAuthException("Authentifizierung fehlgeschlagen (HTTP 401). Tokens pruefen.", ex);
        if (msg.Contains("403")) return new BricklinkAuthException("Zugriff verweigert (HTTP 403). Externe IP im BL-Consumer-Profile pruefen.", ex);
        if (msg.Contains("429")) return new BricklinkRateLimitException("BrickLink-Rate-Limit erreicht (HTTP 429).");
        return ex;
    }

    private static string TranslateError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("401")) return "Authentifizierung fehlgeschlagen (HTTP 401). Tokens pruefen.";
        if (msg.Contains("403")) return "Zugriff verweigert (HTTP 403). Ist die externe IP im BL-Consumer-Profile eingetragen?";
        if (msg.Contains("404")) return "Test-Item 'arc007' nicht gefunden - BL-API antwortet aber.";
        if (msg.Contains("429")) return "Rate-Limit erreicht (HTTP 429).";
        return $"Verbindungsfehler: {ex.Message}";
    }
}
