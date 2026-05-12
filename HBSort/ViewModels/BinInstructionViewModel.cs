using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HBSort.ViewModels;

/// <summary>
/// v0.1.22-beta.1 Block F: Konsolidierte VM fuer das Anweisungs-Overlay.
/// Vorher gab es zwei separate Klassen (BinInstructionViewModel +
/// BinInstructionGroupViewModel) mit fast identischer Struktur - eine fuer
/// Single-Mode (ein Bin + ein Item-Bild), eine fuer Group-Mode (mehrere
/// "Lege Teil X in Box Y"-Eintraege).
///
/// Jetzt eine VM mit Mode-Switch via <see cref="Mode"/>. Sichtbarkeit der
/// Single-/Group-Bereiche im UserControl per DataTrigger auf Mode.
///
/// Auto-Dismiss-Timer fuer den Single-Modus bleibt im ScanViewModel
/// (DispatcherTimer, Timer-Logik braucht WPF-Dispatcher). Group-Mode
/// hat keinen Auto-Dismiss (User-Spec: aktiv wegklicken).
/// </summary>
public partial class BinInstructionViewModel : ObservableObject
{
    /// <summary>Default-Header wenn der Aufrufer im Group-Mode keinen eigenen setzt.</summary>
    public const string DefaultHeaderText = "Lege folgende Teile in die jeweiligen Faecher:";

    /// <summary>True solange das Overlay angezeigt werden soll.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>
    /// Aktueller Modus. Single zeigt Label+ImageUrl prominent; Group zeigt
    /// HeaderText + Items-Liste. Die View bindet beide Bereiche und macht
    /// per DataTrigger den passenden sichtbar.
    /// </summary>
    [ObservableProperty]
    private BinInstructionMode _mode = BinInstructionMode.Single;

    // ----- Single-Mode -----

    /// <summary>Lagerfach-Label im Single-Mode, z.B. "Box 005".</summary>
    [ObservableProperty]
    private string _label = string.Empty;

    /// <summary>URL/Pfad zum Item-Bild im Single-Mode (Minifig oder Einzelteil).</summary>
    [ObservableProperty]
    private string? _imageUrl;

    // ----- Group-Mode -----

    /// <summary>
    /// Header-Text im Group-Mode. Verschiedene Pfade brauchen verschiedene
    /// Texte (siehe UX X.32 v0.1.19-beta.5):
    ///   - DismantleWizard / Reverse-Match: "Lege folgende Teile in die jeweiligen Faecher:"
    ///   - BuildSuggestion-Anlegen:           "Nimm folgende Teile aus den Faechern und lege die fertige Figur in das Ziel-Fach:"
    /// </summary>
    [ObservableProperty]
    private string _headerText = DefaultHeaderText;

    /// <summary>Item-Liste im Group-Mode.</summary>
    public ObservableCollection<BinInstructionItem> Items { get; } = new();

    /// <summary>
    /// Macht das Overlay im Single-Mode sichtbar (ein Bin + ein Item-Bild).
    /// Leeres Label wird als No-Op behandelt - sonst saehe der User ein
    /// "Lege diesen Eintrag in" ohne konkretes Ziel.
    /// </summary>
    public void ShowSingle(string binLabel, string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(binLabel)) return;
        Mode = BinInstructionMode.Single;
        Label = binLabel;
        ImageUrl = imageUrl;
        Items.Clear();
        IsVisible = true;
    }

    /// <summary>
    /// Macht das Overlay im Group-Mode sichtbar (mehrere "Lege X in Box Y"-
    /// Eintraege). Leere Liste wird als No-Op behandelt.
    /// </summary>
    public void ShowGroup(IEnumerable<BinInstructionItem> items, string? headerText = null)
    {
        var list = items?.ToList() ?? new List<BinInstructionItem>();
        if (list.Count == 0) return;

        Mode = BinInstructionMode.Group;
        HeaderText = string.IsNullOrWhiteSpace(headerText) ? DefaultHeaderText : headerText;
        Items.Clear();
        foreach (var i in list) Items.Add(i);
        Label = string.Empty;
        ImageUrl = null;
        IsVisible = true;
    }

    /// <summary>Schliesst das Overlay (manuell durch Klick / Hotkey / Timer).</summary>
    public void Dismiss()
    {
        IsVisible = false;
        Items.Clear();
        HeaderText = DefaultHeaderText;
        Label = string.Empty;
        ImageUrl = null;
    }
}

/// <summary>v0.1.22-beta.1 Block F: Mode-Switch im konsolidierten Overlay.</summary>
public enum BinInstructionMode
{
    Single,
    Group
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
