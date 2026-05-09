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
    [ObservableProperty]
    private bool _isVisible;

    public ObservableCollection<BinInstructionItem> Items { get; } = new();

    /// <summary>Setzt die Item-Liste und macht das Overlay sichtbar.</summary>
    public void Show(IEnumerable<BinInstructionItem> items)
    {
        var list = items?.ToList() ?? new List<BinInstructionItem>();
        if (list.Count == 0) return;

        Items.Clear();
        foreach (var i in list) Items.Add(i);
        IsVisible = true;
    }

    /// <summary>Schliesst das Overlay (manuell durch Klick / Hotkey).</summary>
    public void Dismiss()
    {
        IsVisible = false;
        Items.Clear();
    }
}

/// <summary>
/// UX X.32 Block C (v0.1.19): ein Eintrag im Sammel-Popup.
/// "Lege {ItemLabel} ({QuantityText}) in {BinLabel}"+Bild.
/// </summary>
public class BinInstructionItem
{
    public string ItemLabel { get; init; } = string.Empty;
    public string QuantityText { get; init; } = string.Empty;
    public string BinLabel { get; init; } = string.Empty;
    public string? ImageUrl { get; init; }
}
