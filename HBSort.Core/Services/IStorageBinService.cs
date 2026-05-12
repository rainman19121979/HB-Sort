using HBSort.Core.Models;

namespace HBSort.Core.Services;

/// <summary>
/// Lager-Fach-Verwaltung. Schreibt nach userdata.db (EF Core).
/// </summary>
public interface IStorageBinService
{
    Task<List<StorageBin>> GetAllAsync(CancellationToken ct = default);
    Task<StorageBin?> GetByLabelAsync(string label, CancellationToken ct = default);
    Task<StorageBin?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Faecher die aktuell keine wartende Figur und keinen FloatingPart enthalten.</summary>
    Task<List<StorageBin>> GetFreeAsync(CancellationToken ct = default);

    /// <summary>Faecher die mindestens eine wartende Figur ODER FloatingParts enthalten.</summary>
    Task<List<StorageBin>> GetOccupiedAsync(CancellationToken ct = default);

    /// <summary>Erstes freies Fach (sortiert nach Label) oder null wenn keins frei.</summary>
    Task<StorageBin?> GetNextFreeAsync(CancellationToken ct = default);

    /// <summary>
    /// UX X.31 (v0.1.18) / UX X.32 v0.1.19-beta.4: Schlaegt ein Fach fuer eine
    /// wartende Figur vor.
    ///
    /// <paramref name="maxWaitingLimit"/> (Default 1, Backwards-Compat):
    /// - = 1: nur wirklich freie Faecher (keine wartende, keine Complete,
    ///        keine FloatingParts). UX-X.6-konform / strikte Trennung.
    /// - &gt; 1: bevorzugt Bin der bereits wartende Figuren enthaelt
    ///        (unter Limit) UND keine Complete-Figuren UND keine FloatingParts.
    ///        Sortierung: meiste wartende zuerst (Stapel waechst), dann Label.
    ///        Fallback: wirklich freies Fach.
    ///
    /// Liefert null wenn alle Faecher blockiert sind.
    /// </summary>
    Task<StorageBin?> SuggestBinForWaitingMinifigAsync(
        int maxWaitingLimit = 1,
        CancellationToken ct = default);

    /// <summary>
    /// UX X.31 (v0.1.18): Schlaegt ein Fach fuer eine Complete-Figur vor.
    /// Bevorzugt: Fach das bereits Complete-Figuren enthaelt, aber WENIGER als
    /// <paramref name="maxCompleteLimit"/> Stueck UND keine wartenden Figuren
    /// UND keine FloatingParts (sonst wuerde gemischt). Sortierung: Fach mit
    /// MEISTEN Complete-Figuren zuerst, danach Label.
    /// Fallback: naechstes wirklich freies Fach (analog SuggestBinForWaitingMinifigAsync).
    /// </summary>
    Task<StorageBin?> SuggestBinForCompleteMinifigAsync(int maxCompleteLimit, CancellationToken ct = default);

    /// <summary>
    /// UX X.31 (v0.1.18) / UX X.33 v0.1.19-beta.7 Block N:
    /// Schlaegt ein Fach fuer ein Einzelteil vor. Reihenfolge:
    ///   1) Stapel-Match: Bin mit gleicher PartNo+ColorId (FIFO nach AddedAt).
    ///      Hat IMMER Vorrang - Stapel wachsen weiter.
    ///   2) User-Mapping: wenn <paramref name="userMapping"/> die Kategorie
    ///      einem Bin zugeordnet hat, gewinnt das (auch ueber Default-Regel).
    ///   3) Default-Regel: erstes Bin OHNE Minifigs (ausser excluded) UND
    ///      OHNE einen FloatingPart der gleichen Brickognize-Kategorie aber
    ///      anderer PartNo. Verschiedene Kategorien duerfen sich teilen,
    ///      gleiche Kategorie mit anderer PartNo nicht.
    ///   4) Sonst null - Aufrufer zeigt Volle-Faecher-Banner.
    ///
    /// <paramref name="brickognizeCategory"/>: Kategorie des neuen Teils.
    /// Leer/null wird wie Pseudo-Kategorie "Unbekannt" behandelt - kollidiert
    /// in der Default-Regel mit anderen ungelabelten Items unterschiedlicher
    /// PartNo. Wichtig damit Migration A (Bestand mit Category=null) sich
    /// stabil verhaelt.
    ///
    /// <paramref name="excludeMinifigId"/>: Fach einer Figur die durch den
    /// Aufrufer-Pfad gleich verschwindet (z.B. zu zerlegende Figur) zaehlt
    /// als frei.
    ///
    /// <paramref name="userMapping"/>: optional. Aufrufer reicht typisch
    /// <see cref="AppSettings.CategoryToBinMapping"/> durch. Null = kein
    /// User-Mapping greift, nur Stapel + Default-Regel.
    /// </summary>
    Task<StorageBin?> SuggestBinForFloatingPartAsync(
        string blPartNo, int blColorId,
        string brickognizeCategory,
        int? excludeMinifigId = null,
        Dictionary<string, int>? userMapping = null,
        CancellationToken ct = default);

    Task<StorageBin> CreateSingleAsync(string label, string? notes = null, CancellationToken ct = default);

    /// <summary>
    /// Bulk-Anlage. Konflikte (Label existiert bereits) werden NICHT angelegt
    /// und in <see cref="BulkCreateResult.Conflicts"/> zurueckgegeben.
    /// </summary>
    Task<BulkCreateResult> CreateBulkAsync(IEnumerable<string> labels, CancellationToken ct = default);

    Task<bool> RenameAsync(int id, string newLabel, CancellationToken ct = default);

    /// <summary>
    /// Setzt FreedAt=now, loest TrackedMinifigs vom Fach (StorageBinId=null) und
    /// loescht alle FloatingParts in diesem Fach. Liefert false wenn das Fach
    /// nicht existiert.
    ///
    /// UX X.29 Block C (v0.1.16): schreibt jetzt zusaetzlich pro entkoppelter
    /// Minifig + pro geloeschtem FloatingPart ScanEvents mit UndoData. Die
    /// Aktion ist damit via Strg+Z / Verlauf-Tab teilweise rueckgaengigmachbar
    /// (Minifigs lassen sich zurueckverschieben, FloatingParts wiederherstellen).
    /// </summary>
    Task<bool> EmptyAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// UX X.29 Block C (v0.1.16): Vorab-Inventur fuer den "Fach leeren"-Workflow.
    /// Gibt zurueck wieviele wartende / komplette Figuren + FloatingParts im
    /// Bin liegen, ohne etwas zu aendern. UI zeigt damit eine differenzierte
    /// Confirmation - vor allem die Anzahl Complete-Figuren ist wichtig
    /// (die verlieren ihr Lagerfach beim Leeren).
    /// </summary>
    Task<BinEmptyPreview?> GetEmptyPreviewAsync(int id, CancellationToken ct = default);

    /// <summary>Loescht das Fach. Schlaegt fehl wenn noch Figuren oder FloatingParts darin sind.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Aktuelle Belegung (Anzahl + Liste der wartenden Figuren).</summary>
    Task<BinOccupancy> GetOccupancyAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Detaillierter Inhalt eines Faches: Bin selbst + alle TrackedMinifigs (egal welcher Status,
    /// solange dem Fach zugeordnet) + alle FloatingParts gruppiert nach (BlPartNo, ColorId).
    /// Wird vom BinDetailDialog genutzt.
    /// </summary>
    Task<BinDetailData?> GetDetailAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Phase 7: Vorab-Berechnung fuer den BSX-Export-Cleanup. Liefert die Faecher
    /// die NACH dem Loeschen der uebergebenen Minifig-IDs UND/ODER FloatingPart-IDs
    /// leer waeren (FreedAt=null, keine wartenden Figuren ausser den uebergebenen,
    /// keine FloatingParts ausser den uebergebenen).
    /// Setzt FreedAt NICHT - der Aufrufer entscheidet ob die Faecher freigegeben werden.
    /// floatingPartIdsToBeRemoved kann null oder leer sein - dann wird nur Minifig-
    /// Removal beruecksichtigt (Backwards-Compat zur urspruenglichen Phase-7-Variante).
    /// </summary>
    Task<List<StorageBin>> FindBinsThatWouldBeEmptyAsync(
        IEnumerable<int> minifigIdsToBeRemoved,
        IEnumerable<int>? floatingPartIdsToBeRemoved = null,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 7: setzt FreedAt=now fuer die uebergebenen Faecher.
    /// Pruefen ob die Faecher wirklich leer sind macht der Aufrufer.
    /// Liefert die Anzahl freigegebener Faecher.
    /// </summary>
    Task<int> ReleaseBinsAsync(IEnumerable<int> binIds, CancellationToken ct = default);

    /// <summary>
    /// v0.1.22-beta.1 Block G: zentraler Klassifikator. Liefert den
    /// aktuellen Typ eines Bins (Empty / FloatingOnly / WaitingMinifig /
    /// CompleteOnly) - der Typ ergibt sich aus dem Inhalt, es gibt keine
    /// "Bin.Type"-Spalte. Wartend+Complete-Mix wird als WaitingMinifig
    /// klassifiziert (Reifungspfad). Mix mit FloatingParts ohne wartende
    /// Figur ist ein Anomalie-Fall - die Klassifikation faellt auf
    /// FloatingOnly mit WRN-Log zurueck.
    /// </summary>
    Task<BinKind> GetBinKindAsync(int binId, CancellationToken ct = default);

    /// <summary>
    /// v0.1.22-beta.1 Block G: scannt einmalig die DB nach Mix-Bins die
    /// durch die neue Bin-Typ-Trennung nicht mehr entstehen sollten.
    /// Wird beim App-Start gerufen und liefert eine Liste der betroffenen
    /// Bin-Labels (fuer einen einmaligen User-Hinweis-Toast). Keine
    /// Auto-Reparatur - User entscheidet manuell ob/wie er die Mix-Bins
    /// aufloest.
    /// </summary>
    Task<List<BinMixWarning>> FindBestandMixBinsAsync(CancellationToken ct = default);
}

/// <summary>
/// v0.1.22-beta.1 Block G: drei Bin-Typen, implizit durch Inhalt.
/// Es gibt keine DB-Spalte fuer den Typ - der Klassifikator
/// <see cref="IStorageBinService.GetBinKindAsync"/> leitet ihn ab.
/// </summary>
public enum BinKind
{
    /// <summary>Keine TrackedMinifigs (irgendeines Status), keine FloatingParts.</summary>
    Empty,
    /// <summary>Nur FloatingParts, keine TrackedMinifigs.</summary>
    FloatingOnly,
    /// <summary>
    /// Mindestens eine wartende Figur. Komplette Figuren im selben Bin
    /// sind erlaubt (Reifungspfad). FloatingParts sind hier eine Anomalie
    /// - sollten durch Block-G-Sortier-Logik nicht mehr entstehen.
    /// </summary>
    WaitingMinifig,
    /// <summary>Nur komplette Figuren, keine wartenden, keine FloatingParts.</summary>
    CompleteOnly
}

/// <summary>v0.1.22-beta.1 Block G: ein Eintrag im Bestand-Mix-Scan.</summary>
public record BinMixWarning(
    int BinId,
    string Label,
    string Reason);

/// <summary>
/// UX X.29 Block C (v0.1.16): Vorab-Info fuer den "Fach leeren"-Workflow.
/// </summary>
// UX X.33 v0.1.19-beta.7: SoldMinifigsCount entfernt - der Sold-Status
// existiert nicht mehr (toter Code seit Anfang an, siehe TrackedMinifig.cs).
public record BinEmptyPreview(
    int BinId,
    string Label,
    int WaitingMinifigsCount,
    int CompleteMinifigsCount,
    int FloatingPartsCount);

/// <summary>Daten-Struktur fuer den BinDetailDialog.</summary>
public class BinDetailData
{
    public StorageBin Bin { get; init; } = null!;
    public List<TrackedMinifig> Minifigs { get; init; } = new();
    public List<FloatingPart> FloatingParts { get; init; } = new();
}

/// <summary>Ergebnis von CreateBulkAsync.</summary>
public class BulkCreateResult
{
    public List<StorageBin> Created { get; init; } = new();
    public List<string> Conflicts { get; init; } = new();
}

/// <summary>Belegungs-Snapshot eines Faches.</summary>
public class BinOccupancy
{
    public int MinifigCount { get; init; }
    public int FloatingPartCount { get; init; }
    public List<TrackedMinifig> Minifigs { get; init; } = new();
}
