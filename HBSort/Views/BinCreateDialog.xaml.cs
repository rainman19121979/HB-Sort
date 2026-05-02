using System.Windows;
using HBSort.Core.Models;
using HBSort.Core.Services;

namespace HBSort.Views;

/// <summary>
/// Einfacher Dialog zum Anlegen eines einzelnen Lagerfachs.
/// Bei Erfolg ist DialogResult=true und CreatedBin enthaelt das neue Fach.
/// </summary>
public partial class BinCreateDialog : Window
{
    private readonly IStorageBinService _binService;

    /// <summary>Neu angelegtes Fach (nur gesetzt wenn DialogResult=true).</summary>
    public StorageBin? CreatedBin { get; private set; }

    public BinCreateDialog(IStorageBinService binService)
    {
        InitializeComponent();
        _binService = binService;
        Loaded += (_, _) => LabelBox.Focus();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        var label = LabelBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(label))
        {
            ErrorText.Text = "Label darf nicht leer sein.";
            return;
        }

        try
        {
            var bin = await _binService.CreateSingleAsync(label, NotesBox.Text);
            CreatedBin = bin;
            DialogResult = true;
            Close();
        }
        catch (System.InvalidOperationException ex)
        {
            ErrorText.Text = ex.Message;
        }
        catch (System.Exception ex)
        {
            ErrorText.Text = $"Fehler: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
