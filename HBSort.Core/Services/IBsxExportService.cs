namespace HBSort.Core.Services;

/// <summary>
/// Phase 7: BSX-Export ins BrickStore-XML-Format.
/// Erzeugt nur den XML-String; das Schreiben in eine Datei und das nachgelagerte
/// Cleanup (Figuren / FloatingParts entfernen, leere Faecher freigeben) wird
/// vom UI-Layer gesteuert.
///
/// Phase-7-Erweiterung: Es koennen sowohl komplette Figuren (TrackedMinifig)
/// als auch lose Einzelteile (FloatingPart) in einer einzigen BSX-Datei
/// exportiert werden. Mindestens eine der beiden ID-Listen muss befuellt sein.
/// </summary>
public interface IBsxExportService
{
    /// <summary>
    /// Generiert BSX-XML fuer die uebergebenen TrackedMinifig-IDs UND/ODER
    /// FloatingPart-IDs. Im XML werden zuerst die Minifigs (ItemTypeID=M),
    /// danach die Einzelteile (ItemTypeID=P) als &lt;Item&gt;-Eintraege geschrieben.
    /// Wirft <see cref="System.ArgumentException"/> wenn beide Listen leer sind,
    /// und <see cref="System.InvalidOperationException"/> wenn keine der IDs in
    /// der DB gefunden wird.
    /// </summary>
    Task<string> GenerateBsxAsync(
        IReadOnlyList<int> trackedMinifigIds,
        IReadOnlyList<int> floatingPartIds,
        BsxExportOptions options,
        CancellationToken ct = default);
}

/// <summary>
/// Optionen fuer einen BSX-Export. Die Defaults entsprechen "gebrauchte
/// Minifig, ins Inventory aufnehmen, Preis im BrickStore setzen".
///
/// UX X.14 (2026-05-04): das frueher hier vorhandene <c>Remark</c>-Feld
/// wurde entfernt - &lt;Remarks&gt; pro Item wird jetzt automatisch mit
/// dem Lagerfach-Label aus dem Datenmodell befuellt. Es gibt im UI auch
/// kein Remark-Eingabefeld mehr.
/// </summary>
public record BsxExportOptions(
    /// <summary>"U"=Used, "N"=New (BrickStore-Konvention).</summary>
    string Condition = "U",
    /// <summary>"I"=Include (im Inventory), "X"=Exclude.</summary>
    string Status = "I",
    /// <summary>0 = "Preis spaeter in BrickStore setzen".</summary>
    decimal DefaultPrice = 0m);
