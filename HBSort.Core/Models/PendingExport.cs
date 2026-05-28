namespace HBSort.Core.Models;

/// <summary>
/// v0.1.24-beta.12 Phase 4: ein offener Mass-Update-Export-Eintrag.
/// Wird beim XML-Generieren angelegt und beim Verify (erfolgreich)
/// wieder geloescht. Wartet darauf, dass der User das XML bei BrickLink
/// hochlaedt und HBSort die Aenderung bestaetigt.
///
/// <para>Lebenszyklus:</para>
/// <list type="number">
///   <item><see cref="HBSort.Core.Services.IBlInventoryService.GenerateMassUpdateXmlAsync"/>
///   schreibt fuer jedes Lot mit <c>ReservedQuantity &gt; 0</c> einen
///   Eintrag mit Soll-Werten nach BL-Update.</item>
///   <item><see cref="HBSort.Core.Services.IBlInventoryService.VerifyExportAsync"/>
///   prueft das frische BL-Inventar gegen die Eintraege; erfolgreiche
///   werden geloescht + Reserviert→Collected konvertiert.</item>
/// </list>
///
/// <para>Snapshot-Strategie: bei Re-Generate wird die alte Pending-Liste
/// komplett ersetzt - der User soll nicht zwei parallele Export-Versuche
/// zu unterschiedlichen Zeitpunkten verwalten muessen.</para>
/// </summary>
public class PendingExport
{
    public int Id { get; set; }

    /// <summary>BL-Lot-Id (= BlInventoryLot.LotId).</summary>
    public int LotId { get; set; }

    /// <summary>
    /// Soll-Menge nach BL-Update. Bei <see cref="ShouldDelete"/>=true
    /// inhaltlich egal (BL liefert das Lot dann gar nicht mehr).
    /// </summary>
    public int ExpectedQuantity { get; set; }

    /// <summary>
    /// True wenn <c>ReservedQuantity &gt;= Quantity</c> war - der Mass-Update
    /// loescht das Lot dann komplett (DELETE statt QTY-Reduktion).
    /// </summary>
    public bool ShouldDelete { get; set; }

    /// <summary>UTC-Zeitpunkt der XML-Generierung.</summary>
    public DateTime ExportedAt { get; set; }
}
