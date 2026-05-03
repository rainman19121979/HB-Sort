namespace HBSort.Core.Services;

/// <summary>
/// Erzeugt BrickLink-Wanted-List-XML-Dateien aus den fehlenden Teilen
/// wartender Figuren (Status=Waiting).
///
/// Format-Referenz: https://www.bricklink.com/help.asp?helpID=207
/// (INVENTORY mit ITEM-Elementen: ITEMTYPE=P, ITEMID, COLOR, MINQTY).
///
/// "Fehlend" = QuantityNeeded - QuantityCollected pro TrackedMinifigPart.
/// Teile bei denen Needed == Collected gilt, werden uebersprungen.
/// </summary>
public interface IWantedListExportService
{
    /// <summary>
    /// Generiert ein einzelnes Wanted-List-XML mit den fehlenden Teilen ALLER
    /// wartender Figuren zusammengefasst (Mengen werden ueber Figuren-Grenzen
    /// hinweg per (PartNo, ColorId) summiert).
    /// Wirft <see cref="System.InvalidOperationException"/> wenn keine wartenden
    /// Figuren mit fehlenden Teilen existieren.
    /// </summary>
    Task<string> GenerateCombinedAsync(CancellationToken ct = default);

    /// <summary>
    /// Generiert pro wartender Figur eine eigene Wanted-List. Liefert eine Liste
    /// aus (vorgeschlagener Dateiname, XML-Inhalt). Figuren ohne fehlende Teile
    /// werden uebersprungen.
    /// </summary>
    Task<List<WantedListExportFile>> GeneratePerMinifigAsync(CancellationToken ct = default);
}

/// <summary>
/// Eine vorgeschlagene Wanted-List-Datei. Der Aufrufer entscheidet ueber den
/// finalen Speicher-Pfad; FileName ist nur ein Vorschlag (Sonderzeichen sind
/// schon raus, Dateinamens-sicher).
/// </summary>
public record WantedListExportFile(string FileName, string Xml);
