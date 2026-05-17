using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Models;

namespace HBSort.Views;

/// <summary>
/// Minimaler Lagerfach-Picker — modaler Dialog mit ComboBox + OK/Cancel.
///
/// Historisch (Phase 5) als <c>internal static class</c> in
/// <c>SupersetsDialog.xaml.cs</c> eingebettet gewesen. v0.1.24-beta.1 Phase 3
/// (Aufgabe G) loescht den SupersetsDialog komplett (Anlegen-Pfad lebt jetzt
/// im <see cref="CollectMinifigWizardDialog"/>), aber der BinPickerDialog wird
/// von <c>InventoryListView.xaml.cs</c> (Bulk-Move-Pfad) weiterhin als
/// Lagerfach-Auswahl genutzt — Bulk-Move braucht einen schlanken Picker, kein
/// 2-stufiger Wizard. Daher extrahiert in eine eigene Datei.
///
/// Code-baut die UI programmatisch auf, nutzt ModernWpf-Style fuer
/// konsistente Optik (CenterOwner + ModernWpf-Chrome).
/// </summary>
internal static class BinPickerDialog
{
    public static StorageBin? Show(Window owner, IReadOnlyList<StorageBin> bins, StorageBin? defaultBin)
    {
        var dialog = new Window
        {
            Title = "Lagerfach waehlen",
            Width = 400, Height = 180,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        ModernWpf.Controls.Primitives.WindowHelper.SetUseModernWindowStyle(dialog, true);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var prompt = new TextBlock { Text = "In welches Fach soll das Item verschoben werden?", Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetRow(prompt, 0);
        grid.Children.Add(prompt);

        var combo = new ComboBox { DisplayMemberPath = nameof(StorageBin.Label) };
        foreach (var b in bins) combo.Items.Add(b);
        if (defaultBin != null) combo.SelectedItem = defaultBin;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        Grid.SetRow(combo, 1);
        grid.Children.Add(combo);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(buttons, 3);
        var cancel = new Button { Content = "Abbrechen", Width = 100, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "OK", Width = 100, IsDefault = true };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        grid.Children.Add(buttons);

        dialog.Content = grid;

        StorageBin? result = null;
        ok.Click += (_, _) =>
        {
            result = combo.SelectedItem as StorageBin;
            dialog.DialogResult = true;
            dialog.Close();
        };
        cancel.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };

        var ok2 = dialog.ShowDialog();
        return ok2 == true ? result : null;
    }
}
