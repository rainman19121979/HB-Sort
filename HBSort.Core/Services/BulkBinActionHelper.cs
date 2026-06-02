using System.Text;

namespace HBSort.Core.Services;

/// <summary>
/// Reine (UI-freie) Hilfslogik fuer die Bulk-Bin-Aktionen im Settings-Tab
/// "Lagerfaecher" (v0.1.25-beta.1).
///
/// Warum hier in HBSort.Core und nicht im View-Code-Behind?
/// - Die zwei fehleranfaelligen Stellen sind (1) die Aggregation des
///   Ergebnis-Reports (wie viele geloescht / uebersprungen / Fehler) und
///   (2) die Text-Generierung mit Kuerzung ("... und N weitere"). Beides
///   ist reine Logik ohne WPF und laesst sich so mit xUnit testen, ohne
///   ein Fenster hosten zu muessen.
/// - Der Code-Behind ruft nur diese Methoden auf und zeigt das Ergebnis
///   ueber den IDialogService an.
///
/// WICHTIG (Architektur): Diese Klasse macht KEINE DB-Zugriffe. Sie nimmt
/// nur bereits ermittelte Daten entgegen (z.B. Counts aus
/// GetEmptyPreviewAsync) und buendelt sie zu Text/Report. Die eigentliche
/// Bulk-Schleife ueber EmptyAsync/DeleteAsync laeuft im Code-Behind.
/// </summary>
public static class BulkBinActionHelper
{
    /// <summary>
    /// Wie viele Fach-Labels in der Vorschau einzeln aufgelistet werden,
    /// bevor auf "... und N weitere" gekuerzt wird.
    /// </summary>
    public const int MaxLabelsInPreview = 8;

    /// <summary>
    /// Baut aus einer Label-Liste eine gekuerzte Aufzaehlung. Bei mehr als
    /// <see cref="MaxLabelsInPreview"/> Eintraegen werden die ersten N gezeigt
    /// und der Rest als "... und X weitere" zusammengefasst.
    /// Beispiel: "Box 1, Box 2, Box 3 ... und 5 weitere".
    /// Leere Liste -> leerer String.
    /// </summary>
    public static string BuildTruncatedLabelList(IReadOnlyList<string> labels)
    {
        if (labels is null || labels.Count == 0) return string.Empty;

        if (labels.Count <= MaxLabelsInPreview)
            return string.Join(", ", labels);

        var shown = labels.Take(MaxLabelsInPreview);
        var remaining = labels.Count - MaxLabelsInPreview;
        return string.Join(", ", shown) + $" ... und {remaining} weitere";
    }

    /// <summary>
    /// Erzeugt den Vorschau-Text fuer das Bulk-LOESCHEN (vor der Aktion,
    /// destruktive Bestaetigung).
    ///
    /// Aufbau (Reihenfolge bewusst):
    /// 1. Summen-Zeile zuerst (traegt die Sicherheitslast, da kein Cap).
    /// 2. gekuerzte Liste der Faecher die geloescht werden.
    /// 3. falls vorhanden: gekuerzte Liste der uebersprungenen (belegten) Faecher.
    /// </summary>
    /// <param name="deletableLabels">Labels der wirklich freien Faecher, die geloescht werden.</param>
    /// <param name="skippedLabels">Labels der belegten Faecher, die uebersprungen werden.</param>
    public static string BuildDeletePreview(
        IReadOnlyList<string> deletableLabels,
        IReadOnlyList<string> skippedLabels)
    {
        var sb = new StringBuilder();

        // 1. Summen-Zeile zuerst.
        if (skippedLabels.Count > 0)
        {
            sb.AppendLine(
                $"{deletableLabels.Count} Fach/Faecher werden geloescht, " +
                $"{skippedLabels.Count} werden uebersprungen (belegt).");
        }
        else
        {
            sb.AppendLine($"{deletableLabels.Count} Fach/Faecher werden endgueltig geloescht.");
        }

        // 2. Liste der zu loeschenden Faecher.
        if (deletableLabels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Wird geloescht:");
            sb.AppendLine("  " + BuildTruncatedLabelList(deletableLabels));
        }

        // 3. Liste der uebersprungenen (belegten) Faecher.
        if (skippedLabels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Wird uebersprungen (erst leeren/exportieren):");
            sb.AppendLine("  " + BuildTruncatedLabelList(skippedLabels));
        }

        sb.AppendLine();
        sb.Append("Loeschen kann nicht rueckgaengig gemacht werden. Fortfahren?");
        return sb.ToString();
    }

    /// <summary>
    /// Erzeugt den Vorschau-Text fuer das Bulk-LEEREN (vor der Aktion).
    ///
    /// Aufbau:
    /// 1. Summen-Zeile zuerst (X Faecher werden geleert, davon Y mit kompletten Figuren).
    /// 2. gekuerzte Liste der zu leerenden Faecher.
    /// 3. falls Complete-Figuren betroffen: Achtung-Block (Wortlaut aus Einzel-Pfad).
    /// </summary>
    /// <param name="emptyableLabels">Labels der Faecher mit Inhalt, die geleert werden.</param>
    /// <param name="completeMinifigsTotal">Summe der kompletten Figuren ueber alle markierten Faecher.</param>
    public static string BuildEmptyPreview(
        IReadOnlyList<string> emptyableLabels,
        int completeMinifigsTotal)
    {
        var sb = new StringBuilder();

        // 1. Summen-Zeile zuerst.
        if (completeMinifigsTotal > 0)
        {
            sb.AppendLine(
                $"{emptyableLabels.Count} Fach/Faecher werden geleert - " +
                $"davon mit insgesamt {completeMinifigsTotal} kompletten Figur(en).");
        }
        else
        {
            sb.AppendLine($"{emptyableLabels.Count} Fach/Faecher werden geleert.");
        }

        // 2. Liste der zu leerenden Faecher.
        if (emptyableLabels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Wird geleert:");
            sb.AppendLine("  " + BuildTruncatedLabelList(emptyableLabels));
        }

        sb.AppendLine();
        sb.AppendLine("Beim Leeren werden:");
        sb.AppendLine("  - Wartende Figuren vom Fach geloest (bleiben in der DB)");
        sb.AppendLine("  - Einzelteile geloescht");

        // 3. Achtung-Block bei Complete-Figuren (Wortlaut aus dem Einzel-Pfad,
        //    SettingsWindow.xaml.cs:237-249).
        if (completeMinifigsTotal > 0)
        {
            sb.AppendLine();
            sb.AppendLine(
                $"ACHTUNG: {completeMinifigsTotal} KOMPLETT zusammengebaute Figur(en) verlieren " +
                "beim Leeren ihr Lagerfach (StorageBinId=null) und erscheinen im Temporaeren " +
                "Inventar ohne Fach-Zuordnung.");
        }

        sb.AppendLine();
        sb.Append("Strg+Z (Verlauf-Tab) macht jede Entkopplung/Loeschung einzeln rueckgaengig. Fortfahren?");
        return sb.ToString();
    }

    /// <summary>
    /// Erzeugt den Ergebnis-Report-Text NACH einer Bulk-Aktion.
    ///
    /// Zeigt die drei Mengen aus dem <see cref="BulkBinReport"/> auf:
    /// erfolgreich / uebersprungen (mit Grund) / Fehler (mit Grund). Damit
    /// werden Skip-Count, Teil-Erfolg und Race-Fehler sichtbar - die
    /// fehlende Undo-Kompensation fuers Loeschen.
    /// </summary>
    /// <param name="report">Aggregiertes Ergebnis der Bulk-Schleife.</param>
    /// <param name="actionVerbPast">Vergangenheits-Verb, z.B. "geloescht" oder "geleert".</param>
    public static string BuildResultReport(BulkBinReport report, string actionVerbPast)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{report.SuccessfulCount} Fach/Faecher {actionVerbPast}.");

        if (report.Skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{report.Skipped.Count} uebersprungen:");
            foreach (var (label, reason) in report.Skipped)
            {
                sb.AppendLine($"  - {label}: {reason}");
            }
        }

        if (report.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"{report.Errors.Count} Fehler:");
            foreach (var (label, message) in report.Errors)
            {
                sb.AppendLine($"  - {label}: {message}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Aggregiertes Ergebnis einer Bulk-Bin-Aktion (Leeren oder Loeschen).
///
/// Bewusst eine veraenderbare Sammel-Struktur: der Code-Behind fuellt sie
/// waehrend der Schleife Fach fuer Fach. Jede Liste haelt das Fach-Label
/// plus einen menschenlesbaren Grund/Fehlertext.
/// </summary>
public sealed class BulkBinReport
{
    /// <summary>Anzahl erfolgreich verarbeiteter Faecher.</summary>
    public int SuccessfulCount { get; set; }

    /// <summary>
    /// Uebersprungene Faecher: (Label, Grund). Vorfilter VOR der Schleife
    /// (z.B. belegt beim Loeschen) ODER no-op (z.B. bereits leer beim Leeren).
    /// </summary>
    public List<(string Label, string Reason)> Skipped { get; } = new();

    /// <summary>
    /// Faecher die WAEHREND der Schleife einen Fehler warfen: (Label, Message).
    /// Typischer Fall: Race - das Fach wurde zwischen Vorschau und Ausfuehrung
    /// befuellt, DeleteAsync wirft InvalidOperationException.
    /// </summary>
    public List<(string Label, string Message)> Errors { get; } = new();
}
