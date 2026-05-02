using System.Windows;
using System.Windows.Controls;
using HBSort.Core.Models;
using HBSort.ViewModels;

namespace HBSort.Views;

/// <summary>
/// Phase 5: Dialog "Wo kommt das Teil vor?".
/// Aktionen Zuordnen + Diese-Figur-sammeln werden ueber den ViewModel ausgefuehrt;
/// fuer "Sammeln" wird vorher ein einfacher Lagerfach-Auswahl-Dialog gezeigt.
/// </summary>
public partial class SupersetsDialog : Window
{
    private readonly SupersetsDialogViewModel _viewModel;

    public SupersetsDialog(SupersetsDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not SupersetItemViewModel item) return;
        await _viewModel.AssignToWaitingAsync(item);
    }

    private async void Collect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not SupersetItemViewModel item) return;

        // Hinweis bei Kategorie 3 (NameOnly) – holt erst noch Subsets ueber BL-API.
        if (item.Category == SupersetCategory.NameOnly)
        {
            var ok = MessageBox.Show(
                $"'{item.MinifigName}' ist nur per Name bekannt.\n" +
                "Beim Sammeln wird die komplette Teileliste ueber einen BL-API-Call geladen.\n\n" +
                "Fortfahren?",
                "BL-API-Call noetig", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
        }

        // Lagerfach-Auswahl
        var bins = await _viewModel.GetAllBinsAsync();
        var nextFree = await _viewModel.GetNextFreeBinAsync();
        var bin = BinPickerDialog.Show(this, bins, nextFree);
        if (bin == null) return;

        await _viewModel.CollectMinifigAsync(item, bin.Id);
    }
}

/// <summary>
/// Minimaler Lagerfach-Picker – modaler Dialog mit ComboBox + OK/Cancel.
/// Fuer Phase 5 ausreichend; spaeter koennen wir das durch ein vollwertigeres
/// UI ersetzen wenn noetig.
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

        var prompt = new TextBlock { Text = "In welches Fach soll die neue Figur gelegt werden?", Margin = new Thickness(0, 0, 0, 6) };
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
