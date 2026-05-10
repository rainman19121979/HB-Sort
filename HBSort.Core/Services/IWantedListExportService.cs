namespace HBSort.Core.Services;

/// <summary>
/// Erzeugt BrickLink-Wanted-XML-Dateien mit den **fehlenden Teilen** der
/// wartenden Figuren - aggregiert nach (PartNo, ColorId). Damit kann der
/// User die Datei direkt auf der BrickLink-Webseite hochladen oder den
/// XML-Code per Zwischenablage in das BL-Eingabe-Textfeld einfuegen.
///
/// UX X.32 v0.1.19-beta.6 (User-Befund): Format zurueck auf BL-Wanted-XML.
/// Vorher (beta.4-beta.5) war BSX, aber die BrickLink-Webseite akzeptiert
/// nur ihr eigenes XML-Format beim Upload. Inhalt unveraendert: pro
/// fehlendem Teil ein Eintrag, Mengen ueber Figuren aggregiert.
///
/// "Fehlend" = QuantityNeeded - QuantityCollected pro TrackedMinifigPart.
/// Teile bei denen Needed == Collected gilt, werden uebersprungen.
/// </summary>
public interface IWantedListExportService
{
    /// <summary>
    /// Generiert eine einzelne BL-Wanted-XML-Datei mit den fehlenden Teilen
    /// ALLER wartender Figuren zusammengefasst (Mengen werden ueber Figuren-
    /// Grenzen hinweg per (PartNo, ColorId) summiert).
    /// Wirft <see cref="System.InvalidOperationException"/> wenn keine wartenden
    /// Figuren mit fehlenden Teilen existieren.
    /// </summary>
    Task<string> GenerateCombinedAsync(CancellationToken ct = default);

    /// <summary>
    /// Generiert pro wartender Figur eine eigene BL-Wanted-XML-Datei. Liefert
    /// eine Liste aus (vorgeschlagener Dateiname, XML-Inhalt). Figuren ohne
    /// fehlende Teile werden uebersprungen.
    /// </summary>
    Task<List<WantedListExportFile>> GeneratePerMinifigAsync(CancellationToken ct = default);
}

/// <summary>
/// Eine vorgeschlagene Wanted-List-Datei. Der Aufrufer entscheidet ueber den
/// finalen Speicher-Pfad; FileName ist nur ein Vorschlag (Sonderzeichen sind
/// schon raus, Dateinamens-sicher).
/// </summary>
public record WantedListExportFile(string FileName, string Xml);
