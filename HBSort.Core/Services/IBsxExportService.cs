namespace HBSort.Core.Services;

/// <summary>
/// Phase 7: BSX-Export der kompletten Figuren ins BrickStore-XML-Format.
/// Erzeugt nur den XML-String; das Schreiben in eine Datei und das
/// nachgelagerte Cleanup (Figuren entfernen / Faecher freigeben) wird
/// vom UI-Layer gesteuert.
/// </summary>
public interface IBsxExportService
{
    /// <summary>
    /// Generiert BSX-XML fuer die uebergebenen TrackedMinifig-IDs.
    /// Pro Figur wird ein &lt;Item&gt;-Eintrag mit ItemTypeID=M erzeugt.
    /// Wirft <see cref="System.ArgumentException"/> wenn keine IDs uebergeben
    /// werden, und <see cref="System.InvalidOperationException"/> wenn keine
    /// der IDs in der DB gefunden wird.
    /// </summary>
    Task<string> GenerateBsxAsync(
        IEnumerable<int> trackedMinifigIds,
        BsxExportOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Optionen fuer einen BSX-Export. Die Defaults entsprechen "gebrauchte
/// Minifig, ins Inventory aufnehmen, Preis im BrickStore setzen".
/// </summary>
public record BsxExportOptions(
    /// <summary>"U"=Used, "N"=New (BrickStore-Konvention).</summary>
    string Condition = "U",
    /// <summary>"I"=Include (im Inventory), "X"=Exclude.</summary>
    string Status = "I",
    /// <summary>0 = "Preis spaeter in BrickStore setzen".</summary>
    decimal DefaultPrice = 0m,
    /// <summary>Optionaler Remark-Text. Wenn null: "HBSort {Datum}".</summary>
    string? Remark = null);
