using LegoMinifigSorter.Core.Models;

namespace LegoMinifigSorter.Core.Services;

/// <summary>
/// Phase 5: zentraler Service fuer Modus-B (Einzelteil-Scan).
///
/// Zustaendigkeiten:
///   1. Lookup eines Teils + Farbe -> Liste der wartenden Figuren die das Teil brauchen
///   2. Aktion "Teil zu Figur zuordnen" (QuantityCollected++)
///   3. Aktion "Teil als Floating-Part lagern"
///   4. Aktion "Neue Figur aus Supersets-Treffer sammeln" (anlegen + Trigger-Teil mitgeben)
///   5. Inverse-Aktion "Teil von Figur entfernen" (QuantityCollected=0)
/// </summary>
public interface IPartLookupService
{
    /// <summary>
    /// Liest Teil-Stammdaten (Name, Farbe, RGB) aus dem Cache und ermittelt
    /// alle wartenden Figuren die dieses Teil noch brauchen.
    /// </summary>
    Task<PartLookupResult> LookupPartAsync(string blPartNo, int blColorId, CancellationToken ct = default);

    /// <summary>
    /// Erhoeht QuantityCollected eines konkreten TrackedMinifigPart um 1
    /// (cap auf QuantityNeeded). Setzt Status=Complete + CompletedAt wenn alle
    /// Parts der Figur jetzt komplett sind. Schreibt einen ScanEvent.
    /// </summary>
    /// <returns>true wenn die Figur durch diese Aktion komplett wurde.</returns>
    Task<bool> AssignPartToMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default);

    /// <summary>
    /// Inverse-Aktion: setzt QuantityCollected auf 0. Falls die Figur vorher
    /// Complete war, wird sie auf Waiting zurueckgesetzt (CompletedAt=null).
    /// </summary>
    /// <returns>true wenn der Eintrag existierte und veraendert wurde.</returns>
    Task<bool> UnassignPartFromMinifigAsync(int trackedMinifigPartId, CancellationToken ct = default);

    /// <summary>
    /// Legt das Teil als FloatingPart in das angegebene Fach. Wenn schon ein
    /// FloatingPart mit gleichem (BlPart, BlColor, Bin) existiert, wird Quantity addiert.
    /// </summary>
    Task<FloatingPart> AddPartToFloatingAsync(
        string blPartNo, int blColorId, string partName, string colorName,
        int quantity, int storageBinId, CancellationToken ct = default);

    /// <summary>
    /// "Diese Figur sammeln" aus dem Supersets-Dialog: legt eine neue
    /// TrackedMinifig (Status=Waiting) im gewaehlten Fach an, baut die
    /// RequiredParts aus dem BL-Cache (oder via 1 BL-Call wenn noetig),
    /// setzt das Trigger-Teil sofort auf collected und macht Reverse-Match
    /// gegen FloatingParts.
    /// </summary>
    Task<TrackedMinifig> CollectMinifigFromSupersetAsync(
        string blMinifigId, int storageBinId,
        string triggerPartNo, int triggerColorId, int triggerQuantity,
        CancellationToken ct = default);

    /// <summary>
    /// Loescht einen FloatingPart-Eintrag komplett aus der DB (User-Aktion in
    /// der Lagerliste). Liefert true wenn der Eintrag existierte.
    /// </summary>
    Task<bool> DeleteFloatingPartAsync(int floatingPartId, CancellationToken ct = default);

    /// <summary>
    /// Findet alle Lagerfaecher in denen das Teil (BL-Part-No + BL-Color-Id) bereits
    /// als Einzelteil liegt. Sortiert absteigend nach Gesamt-Menge im Fach – das
    /// erste Element ist das "Smart-Default-Fach" fuer den DismantleWizard.
    /// </summary>
    Task<List<FloatingPartLocation>> FindFloatingLocationsAsync(
        string blPartNo, int blColorId, CancellationToken ct = default);
}

/// <summary>Ein Fach das ein bestimmtes Teil bereits als Einzelteil enthaelt.</summary>
public record FloatingPartLocation(
    int StorageBinId,
    string StorageBinLabel,
    int TotalQuantity);

/// <summary>Ergebnis eines LookupPartAsync-Aufrufs.</summary>
public record PartLookupResult(
    string BlPartNo,
    int BlColorId,
    string PartName,
    string ColorName,
    string? ColorRgb,
    List<WaitingMinifigMatch> WaitingMatches,
    List<BlCatalogMatch> BlCatalogMatches);

/// <summary>
/// Eine Minifig aus dem BL-Catalog-Cache die das angefragte Teil enthaelt
/// (aber NICHT in den wartenden Figuren des Users vorkommt).
/// </summary>
public record BlCatalogMatch(
    string BlMinifigId,
    string? MinifigName,
    string? MinifigImageUrl,
    int QuantityInMinifig);

/// <summary>
/// Eine wartende Figur die das angefragte Teil noch braucht.
/// </summary>
public record WaitingMinifigMatch(
    int TrackedMinifigPartId,
    int TrackedMinifigId,
    string BlMinifigId,
    string MinifigName,
    string? MinifigImageUrl,
    string? StorageBinLabel,
    int StorageBinId,
    int QuantityNeeded,
    int QuantityCollected,
    bool IsAlternate);
