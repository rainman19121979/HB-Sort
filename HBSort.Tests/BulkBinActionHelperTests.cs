using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// v0.1.25-beta.1: Tests fuer die UI-freie Bulk-Bin-Hilfslogik
/// (BulkBinActionHelper). Konzentriert die testbare Logik (Report-
/// Aggregation, Vorschau-/Report-Text-Generierung inkl. Kuerzung) hier,
/// damit kein WPF-Fenster gehostet werden muss.
/// </summary>
public class BulkBinActionHelperTests
{
    // ====================================================================
    // BuildTruncatedLabelList - Kuerzung "... und N weitere"
    // ====================================================================

    [Fact]
    public void TruncatedLabelList_empty_returns_empty_string()
    {
        var result = BulkBinActionHelper.BuildTruncatedLabelList(new List<string>());
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void TruncatedLabelList_single_label()
    {
        var result = BulkBinActionHelper.BuildTruncatedLabelList(new List<string> { "Box 1" });
        Assert.Equal("Box 1", result);
    }

    [Fact]
    public void TruncatedLabelList_at_limit_shows_all_without_suffix()
    {
        // Genau MaxLabelsInPreview (8) Eintraege -> alle, kein Suffix.
        var labels = Enumerable.Range(1, BulkBinActionHelper.MaxLabelsInPreview)
            .Select(i => $"Box {i}").ToList();

        var result = BulkBinActionHelper.BuildTruncatedLabelList(labels);

        Assert.DoesNotContain("weitere", result);
        Assert.Contains("Box 8", result);
    }

    [Fact]
    public void TruncatedLabelList_over_limit_truncates_with_remaining_count()
    {
        // 10 Eintraege -> erste 8 + "... und 2 weitere".
        var labels = Enumerable.Range(1, 10).Select(i => $"Box {i}").ToList();

        var result = BulkBinActionHelper.BuildTruncatedLabelList(labels);

        Assert.Contains("Box 1", result);
        Assert.Contains("Box 8", result);
        Assert.DoesNotContain("Box 9", result);
        Assert.Contains("... und 2 weitere", result);
    }

    [Fact]
    public void TruncatedLabelList_far_over_limit_correct_remaining()
    {
        var labels = Enumerable.Range(1, 25).Select(i => $"Fach {i}").ToList();

        var result = BulkBinActionHelper.BuildTruncatedLabelList(labels);

        // 25 - 8 = 17 weitere
        Assert.Contains("... und 17 weitere", result);
    }

    // ====================================================================
    // BuildDeletePreview - Summen-Zeile zuerst
    // ====================================================================

    [Fact]
    public void DeletePreview_no_skipped_shows_only_delete_count()
    {
        var deletable = new List<string> { "Box 1", "Box 2" };
        var skipped = new List<string>();

        var text = BulkBinActionHelper.BuildDeletePreview(deletable, skipped);

        Assert.Contains("2 Fach/Faecher werden endgueltig geloescht", text);
        Assert.DoesNotContain("uebersprungen", text);
        Assert.Contains("Box 1", text);
        // Hinweis auf fehlendes Undo.
        Assert.Contains("nicht rueckgaengig", text);
    }

    [Fact]
    public void DeletePreview_with_skipped_shows_sum_line_first()
    {
        var deletable = new List<string> { "Box 1", "Box 2", "Box 3" };
        var skipped = new List<string> { "Box 4", "Box 5" };

        var text = BulkBinActionHelper.BuildDeletePreview(deletable, skipped);

        // Summen-Zeile muss zuerst stehen (vor den Listen).
        var firstLine = text.Split('\n')[0];
        Assert.Contains("3 Fach/Faecher werden geloescht", firstLine);
        Assert.Contains("2 werden uebersprungen", firstLine);

        // Beide Listen vorhanden.
        Assert.Contains("Wird geloescht:", text);
        Assert.Contains("Wird uebersprungen", text);
        Assert.Contains("Box 4", text);
    }

    // ====================================================================
    // BuildEmptyPreview - Achtung-Block bei Complete-Figuren
    // ====================================================================

    [Fact]
    public void EmptyPreview_no_complete_no_warning()
    {
        var emptyable = new List<string> { "Box 1" };

        var text = BulkBinActionHelper.BuildEmptyPreview(emptyable, completeMinifigsTotal: 0);

        Assert.Contains("1 Fach/Faecher werden geleert", text);
        Assert.DoesNotContain("ACHTUNG", text);
    }

    [Fact]
    public void EmptyPreview_with_complete_shows_warning_and_sum()
    {
        var emptyable = new List<string> { "Box 1", "Box 2" };

        var text = BulkBinActionHelper.BuildEmptyPreview(emptyable, completeMinifigsTotal: 3);

        // Summen-Zeile mit Complete-Zahl.
        Assert.Contains("2 Fach/Faecher werden geleert", text);
        Assert.Contains("3 kompletten Figur(en)", text);
        // Achtung-Block.
        Assert.Contains("ACHTUNG", text);
        Assert.Contains("3 KOMPLETT zusammengebaute Figur(en)", text);
    }

    // ====================================================================
    // BuildResultReport - Aggregation nach der Aktion
    // ====================================================================

    [Fact]
    public void ResultReport_pure_success()
    {
        var report = new BulkBinReport { SuccessfulCount = 5 };

        var text = BulkBinActionHelper.BuildResultReport(report, "geloescht");

        Assert.Contains("5 Fach/Faecher geloescht", text);
        Assert.DoesNotContain("uebersprungen", text);
        Assert.DoesNotContain("Fehler", text);
    }

    [Fact]
    public void ResultReport_with_skipped_and_errors()
    {
        var report = new BulkBinReport { SuccessfulCount = 8 };
        report.Skipped.Add(("Box 2", "belegt - erst leeren"));
        report.Skipped.Add(("Box 5", "enthaelt komplette Figur(en) - erst exportieren/leeren"));
        report.Errors.Add(("Box 9", "wurde zwischenzeitlich befuellt"));

        var text = BulkBinActionHelper.BuildResultReport(report, "geloescht");

        Assert.Contains("8 Fach/Faecher geloescht", text);
        Assert.Contains("2 uebersprungen", text);
        Assert.Contains("Box 2: belegt - erst leeren", text);
        Assert.Contains("Box 5: enthaelt komplette Figur(en)", text);
        Assert.Contains("1 Fehler", text);
        Assert.Contains("Box 9: wurde zwischenzeitlich befuellt", text);
    }

    [Fact]
    public void ResultReport_zero_success_only_skipped()
    {
        // Edge-Case: alle markierten waren belegt -> 0 geloescht, nur Skips.
        var report = new BulkBinReport { SuccessfulCount = 0 };
        report.Skipped.Add(("Box 1", "belegt - erst leeren"));

        var text = BulkBinActionHelper.BuildResultReport(report, "geloescht");

        Assert.Contains("0 Fach/Faecher geloescht", text);
        Assert.Contains("1 uebersprungen", text);
    }

    // ====================================================================
    // BulkBinReport - aggregiert die drei Mengen korrekt
    // ====================================================================

    [Fact]
    public void Report_aggregates_mixed_bin_kinds()
    {
        // Simuliert eine gemischte Auswahl: leer / Waiting / Floating /
        // Complete / Mix - so wie der Code-Behind die Vorfilter-Logik
        // einsortieren wuerde.
        var report = new BulkBinReport();

        // 2 leere Faecher -> erfolgreich geloescht.
        report.SuccessfulCount = 2;
        // Waiting -> uebersprungen.
        report.Skipped.Add(("Waiting-Fach", "belegt - erst leeren"));
        // Floating -> uebersprungen.
        report.Skipped.Add(("Floating-Fach", "belegt - erst leeren"));
        // Complete -> uebersprungen (eigener Grund).
        report.Skipped.Add(("Complete-Fach", "enthaelt komplette Figur(en) - erst exportieren/leeren"));
        // Mix (Waiting + Floating) -> uebersprungen.
        report.Skipped.Add(("Mix-Fach", "belegt - erst leeren"));

        Assert.Equal(2, report.SuccessfulCount);
        Assert.Equal(4, report.Skipped.Count);
        Assert.Empty(report.Errors);

        // Genau ein Complete-Skip mit dem differenzierten Grund.
        Assert.Single(report.Skipped, s => s.Reason.Contains("komplette Figur"));
    }
}
