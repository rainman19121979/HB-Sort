using System.Windows;
using HBSort.Core.Models;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// v0.1.24-beta.8 Phase 3 (Fix 3): Reservierungs-Dialog mit einer Karte
/// pro Lot. Lot-Karten gruppiert nach Condition (Neu / Gebraucht); jede
/// Karte zeigt die Shop-Position (Remarks) und die verfuegbare Menge. Klick
/// auf eine Karte reserviert genau dieses Lot.
/// </summary>
public partial class BlReserveDialog : Window
{
    /// <summary>Pro Lot eine ViewRow - bereitet Anzeige-Strings vor.</summary>
    public sealed class LotRow
    {
        public BlInventoryLot Lot { get; }
        public string LocationDisplay { get; }
        public string QuantityDisplay { get; }

        public LotRow(BlInventoryLot lot)
        {
            Lot = lot;
            LocationDisplay = string.IsNullOrWhiteSpace(lot.Remarks)
                ? "(kein Lagerplatz)"
                : lot.Remarks!;
            var avail = lot.Quantity - lot.ReservedQuantity;
            QuantityDisplay = $"{avail}×";
        }
    }

    public sealed class ViewData
    {
        public string HeaderLine { get; init; } = string.Empty;
        public string BodyLine { get; init; } = string.Empty;
        public bool HasNew { get; init; }
        public bool HasUsed { get; init; }
        public IReadOnlyList<LotRow> NewLots { get; init; } = Array.Empty<LotRow>();
        public IReadOnlyList<LotRow> UsedLots { get; init; } = Array.Empty<LotRow>();
    }

    /// <summary>Das vom User gewaehlte Lot - null wenn abgebrochen.</summary>
    public BlInventoryLot? SelectedLot { get; private set; }

    public BlReserveDialog(SummaryPartViewModel partVm, string minifigName, BlAvailabilityInfo availability)
    {
        InitializeComponent();

        DataContext = new ViewData
        {
            HeaderLine = $"BL-Lot reservieren fuer '{partVm.PartName}'",
            BodyLine = $"Dieses Teil ist nicht im HBSort-Lager, aber {availability.NewQty}× als Neu " +
                       $"und {availability.UsedQty}× als Gebraucht in deinem BrickLink-Shop verfuegbar. " +
                       $"Klicke ein Lot an, um es fuer '{minifigName}' zu reservieren.",
            HasNew = availability.HasNew,
            HasUsed = availability.HasUsed,
            NewLots = availability.NewLots.Select(l => new LotRow(l)).ToList(),
            UsedLots = availability.UsedLots.Select(l => new LotRow(l)).ToList()
        };
    }

    /// <summary>Klick auf eine Lot-Karte: das gewaehlte Lot zurueckgeben.</summary>
    private void LotCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is LotRow row)
        {
            SelectedLot = row.Lot;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedLot = null;
        DialogResult = false;
        Close();
    }
}
