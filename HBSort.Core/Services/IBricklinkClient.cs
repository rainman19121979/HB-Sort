namespace HBSort.Core.Services;

/// <summary>
/// Wrapper um die BrickLink-Store-API (BricklinkSharp).
/// Phase R1: Connection-Test.
/// Phase R2: GetItem/GetSubsets/GetColorList implementiert.
///
/// Eigene DTOs (BricklinkItemDto/SubsetDto/ColorDto) damit unser Service-Layer
/// nicht direkt von BricklinkSharp-Types abhaengt - Tests koennen den Wrapper
/// einfach mocken.
/// </summary>
public interface IBricklinkClient
{
    /// <summary>Liefert true wenn 4 Tokens hinterlegt sind und entschluesselt werden koennen.</summary>
    Task<bool> IsConfiguredAsync();

    /// <summary>
    /// Test-Call: laed Item "arc007" (Arctic Forscher Minifig).
    /// Liefert ein TestResult mit Success/ErrorMessage/ItemName.
    /// </summary>
    Task<BricklinkTestResult> TestConnectionAsync(CancellationToken ct = default);

    // --- Phase R2: echte Implementierung ---

    /// <summary>Holt Item-Details von der BL-API (GetItemAsync).</summary>
    Task<BricklinkItemDto> GetItemAsync(string itemType, string itemNo, CancellationToken ct = default);

    /// <summary>
    /// Holt die Subsets eines Parent-Items (z.B. Teile einer Minifigur).
    /// breakMinifigs=false: Minifigs in einem Set werden NICHT in Teile zerlegt.
    /// </summary>
    Task<List<BricklinkSubsetDto>> GetSubsetsAsync(string itemType, string itemNo, bool breakMinifigs, CancellationToken ct = default);

    /// <summary>Holt die komplette BL-Color-Liste (~250 Eintraege, einmalig).</summary>
    Task<List<BricklinkColorDto>> GetColorListAsync(CancellationToken ct = default);

    // --- Phase 5: Reverse-Lookup-Endpunkte ---

    /// <summary>
    /// "Wo kommt dieses Teil in dieser Farbe vor?" -> Liste der Parent-Items
    /// (Sets, Minifigs etc.). Wir filtern aufruferseitig auf parent_type='M'.
    /// </summary>
    Task<List<BricklinkSupersetEntryDto>> GetSupersetsAsync(
        string itemType, string itemNo, int colorId, CancellationToken ct = default);

    /// <summary>
    /// "In welchen Farben gibt es dieses Teil?" -> Liste von Color-IDs (mit Total-Quantity
    /// im BL-Catalog). Wird in Phase 5 fuer das Korrektur-Dropdown genutzt, damit der
    /// User nur sinnvolle Farben angeboten bekommt.
    /// </summary>
    Task<List<BricklinkKnownColorDto>> GetKnownColorsAsync(
        string itemType, string itemNo, CancellationToken ct = default);

    // --- v0.1.24-beta.6 Phase 1: BL-Store-Inventar ---

    /// <summary>
    /// Holt alle Verkaufs-Lots aus dem BL-Store des Users (BricklinkSharp:
    /// GetInventoryListAsync). Ein Lot = eine Artikel-Position im Store
    /// (z.B. "100x Plate 1x1 / Red / Used / 0.03 EUR"). Liefert DTOs - das
    /// Mapping nach BlInventoryLot-Entity passiert in BlInventoryService.
    /// </summary>
    Task<List<BricklinkInventoryLotDto>> GetInventoryAsync(CancellationToken ct = default);
}

/// <summary>Resultat eines TestConnection-Aufrufs.</summary>
public class BricklinkTestResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ItemName { get; init; }
    public long ResponseTimeMs { get; init; }
}

/// <summary>DTO fuer GetItem.</summary>
public class BricklinkItemDto
{
    public string ItemType { get; set; } = string.Empty;
    public string ItemNo { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? YearReleased { get; set; }
    public string? ImageUrl { get; set; }
    public double? Weight { get; set; }
    public double? DimX { get; set; }
    public double? DimY { get; set; }
    public double? DimZ { get; set; }
    public int? CategoryId { get; set; }
    /// <summary>Roh-JSON der API-Antwort (fuer Forensik / spaetere Migrationen).</summary>
    public string? RawJson { get; set; }
}

/// <summary>
/// DTO fuer einen Subsets-Eintrag. Match-Group: BL gruppiert alternative Teile
/// pro Match-No; alle bis auf den ersten Eintrag pro Gruppe sind IsAlternate=true.
/// </summary>
public class BricklinkSubsetDto
{
    public string ItemType { get; set; } = string.Empty;
    public string ItemNo { get; set; } = string.Empty;
    public int ColorId { get; set; }
    public int Quantity { get; set; }
    public int ExtraQuantity { get; set; }
    public bool IsAlternate { get; set; }
    public bool IsCounterpart { get; set; }
    public int MatchId { get; set; }
    /// <summary>Optional: Item-Name aus dem inneren Item-Objekt (zum Mit-Cachen).</summary>
    public string? ItemName { get; set; }
}

/// <summary>DTO fuer einen Eintrag in der Color-Liste.</summary>
public class BricklinkColorDto
{
    public int ColorId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Rgb { get; set; }
    public string? Type { get; set; }
}

/// <summary>
/// DTO fuer einen Eintrag aus der GetSupersets-Antwort, schon flachgeklopft:
/// pro Parent-Item ein Eintrag, mit AppearsAs-Klassifikation.
/// </summary>
public class BricklinkSupersetEntryDto
{
    public string ParentType { get; set; } = string.Empty;   // 'M', 'S', ...
    public string ParentNo { get; set; } = string.Empty;
    public string? ParentName { get; set; }
    public int Quantity { get; set; }
    /// <summary>BL: Regular / Alternate / Counterpart / Extra. Wir nehmen das als String.</summary>
    public string AppearsAs { get; set; } = "Regular";
    /// <summary>Color-ID der ANFRAGE (also Farbe, in der das angefragte Teil vorkommt).</summary>
    public int RequestedColorId { get; set; }
}

/// <summary>DTO fuer einen Eintrag aus der GetKnownColors-Antwort.</summary>
public class BricklinkKnownColorDto
{
    public int ColorId { get; set; }
    /// <summary>Anzahl Eintraege im BL-Catalog (informativ).</summary>
    public int Quantity { get; set; }
}

/// <summary>
/// v0.1.24-beta.6 Phase 1: DTO fuer einen Verkaufs-Lot aus dem BL-Store-
/// Inventar. Spiegelt die Felder aus BricklinkSharp.Inventory die wir
/// brauchen - die Service-Schicht mappt das auf <see cref="HBSort.Core.Models.BlInventoryLot"/>.
/// </summary>
public class BricklinkInventoryLotDto
{
    /// <summary>BL-seitige inventory_id (stabil pro Lot).</summary>
    public int LotId { get; set; }

    /// <summary>"P", "M", "S" etc. (Kurzform analog zu BricklinkSubsetDto.ItemType).</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>BL-Artikelnummer.</summary>
    public string ItemNo { get; set; } = string.Empty;

    /// <summary>BL-Color-ID. Null wenn das Item farblos ist.</summary>
    public int? ColorId { get; set; }

    /// <summary>Color-Name gecacht (z.B. "Red"). Null wenn ColorId=null.</summary>
    public string? ColorName { get; set; }

    /// <summary>Optionale User-Description aus dem Listing.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// v0.1.24-beta.8 Phase 3: optionale Remarks (User-Notiz pro Lot, typisch
    /// die physische Position im Shop).
    /// </summary>
    public string? Remarks { get; set; }

    /// <summary>Stueckzahl im Lot.</summary>
    public int Quantity { get; set; }

    /// <summary>Stueckpreis in Store-Waehrung.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>"N" = New, "U" = Used.</summary>
    public string Condition { get; set; } = "U";
}
