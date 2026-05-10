using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HBSort.ViewModels;

/// <summary>
/// UX X.32 Block C (v0.1.19): Sammel-Popup-VM. Wird vom Direkt-Zerlegen-
/// Pfad und vom Reverse-Match (>=2 Teile konsumiert) genutzt - statt
/// mehrerer Einzel-Toasts erscheint ein Modal-Overlay mit allen
/// "Lege X in Box Y"-Anweisungen auf einen Blick.
///
/// Lebt als Sub-Property des ScanViewModel; der Auto-Dismiss-Timer wird
/// hier NICHT verwaltet - Sammel-Popup bleibt sichtbar bis der User aktiv
/// schliesst (User-Spec: "kein Auto-Close, immer aktiv wegklicken").
/// </summary>
public partial class BinInstructionGroupViewModel : ObservableObject
{
    /// <summary>Default-Header wenn der Aufrufer keinen eigenen setzt.</summary>
    public const string DefaultHeaderText = "Lege folgende Teile in die jeweiligen Faecher:";

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>
    /// UX X.32 v0.1.19-beta.5 (User-Befund): konfigurierbarer Header-Text
    /// im Overlay. Verschiedene Pfade brauchen verschiedene Texte:
    ///   - DismantleWizard / Reverse-Match: "Lege folgende Teile in die jeweiligen Faecher:"
    ///   - BuildSuggestion-Anlegen:           "Nimm folgende Teile aus den Faechern und lege die fertige Figur in das Ziel-Fach:"
    /// </summary>
    [ObservableProperty]
    private string _headerText = DefaultHeaderText;

    public ObservableCollection<BinInstructionItem> Items { get; } = new();

    /// <summary>Setzt die Item-Liste und macht das Overlay sichtbar.</summary>
    public void Show(IEnumerable<BinInstructionItem> items, string? headerText = null)
    {
        var list = items?.ToList() ?? new List<BinInstructionItem>();
        if (list.Count == 0) return;

        HeaderText = string.IsNullOrWhiteSpace(headerText) ? DefaultHeaderText : headerText;
        Items.Clear();
        foreach (var i in list) Items.Add(i);
        IsVisible = true;
    }

    /// <summary>Schliesst das Overlay (manuell durch Klick / Hotkey).</summary>
    public void Dismiss()
    {
        IsVisible = false;
        Items.Clear();
        HeaderText = DefaultHeaderText;
    }
}

/// <summary>
/// UX X.32 Block C (v0.1.19): ein Eintrag im Sammel-Popup.
/// "Lege {ItemLabel} ({QuantityText}) in {BinLabel}"+Bild.
///
/// UX X.32 v0.1.19-beta.6: ObservableObject damit der UI-Layer das
/// ImageUrl async nachladen kann (z.B. ueber IPartImageProvider) -
/// das Bild erscheint dann automatisch sobald der Disk-Cache-Pfad
/// geliefert wird, ohne das Popup zu blockieren.
/// </summary>
public partial class BinInstructionItem : ObservableObject
{
    public string ItemLabel { get; init; } = string.Empty;
    public string QuantityText { get; init; } = string.Empty;
    public string BinLabel { get; init; } = string.Empty;

    [ObservableProperty]
    private string? _imageUrl;

    /// <summary>
    /// UX X.32 v0.1.19-beta.6: True markiert ein "Ziel-Eintrag" - wo der
    /// User etwas HINEINlegt (z.B. die fertige Figur). False (Default)
    /// markiert ein "Quell-Eintrag" - wo der User etwas RAUSnimmt
    /// (Reverse-Match-Teile aus den Quell-Faechern). Das Overlay-Template
    /// trennt Quell- und Ziel-Items optisch (Separator + leichter Tint).
    /// </summary>
    public bool IsTargetItem { get; init; }
}
