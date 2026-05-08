using System.Windows;
using System.Windows.Controls;
using HBSort.Services;
using HBSort.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// UX X.29 (v0.1.16): Code-Behind fuer den Verlauf-Tab.
/// Lade-Hook bei Tab-Wechsel + Click-Handler fuer Rueckgaengig-Button.
/// </summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();

        // Bei jedem Sichtbar-Werden frisch laden (analog InventoryListView).
        IsVisibleChanged += async (_, args) =>
        {
            if (args.NewValue is bool isVisible
                && isVisible
                && DataContext is HistoryViewModel vm)
            {
                try { await vm.RefreshAsync(); }
                catch (System.Exception ex) { Log.Warning(ex, "HistoryView Auto-Load fehlgeschlagen"); }
            }
        };
    }

    private async void UndoRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not HistoryRowViewModel row) return;
        if (DataContext is not HistoryViewModel vm) return;
        if (!b.IsEnabled) return;
        b.IsEnabled = false;
        try
        {
            var dialogs = App.Services.GetRequiredService<IDialogService>();
            var ok = await dialogs.ShowQuestionAsync(
                "Aktion rueckgaengig machen?",
                $"Diese Aktion rueckgaengig machen?\n\n" +
                $"Aktion: {row.TypeDisplay}\n" +
                $"Beschreibung: {row.Description}\n" +
                $"Zeitpunkt: {row.TimestampDisplay}\n\n" +
                $"Hinweis: das Rueckgaengigmachen wird selbst als Eintrag im Verlauf gespeichert.");
            if (!ok) return;

            await vm.UndoRowAsync(row);
        }
        finally
        {
            b.IsEnabled = true;
        }
    }
}
