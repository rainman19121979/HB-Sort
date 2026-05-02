using System.Windows;
using System.Windows.Controls;

namespace HBSort.Views;

/// <summary>
/// Minimaler Input-Dialog (eine Zeile). WPF hat selbst keinen, deswegen
/// dieser kleine Helfer. Nicht produktions-poliert: nur fuer einfache
/// Rename-/Eingabe-Aktionen gedacht.
/// </summary>
internal static class SimpleInputBox
{
    /// <summary>
    /// Zeigt einen modalen Dialog mit einer TextBox. Liefert den eingegebenen
    /// Text bei OK, oder null bei Abbruch.
    /// </summary>
    public static string? Show(Window owner, string title, string prompt, string defaultValue = "")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 170,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        // ModernWPF-Style anwenden, damit das Fenster zum Rest der App passt.
        // (WindowHelper liegt im Controls.Primitives-Namespace.)
        ModernWpf.Controls.Primitives.WindowHelper.SetUseModernWindowStyle(dialog, true);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var promptText = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetRow(promptText, 0);
        grid.Children.Add(promptText);

        var textBox = new TextBox { Text = defaultValue };
        Grid.SetRow(textBox, 1);
        grid.Children.Add(textBox);

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

        string? result = null;
        ok.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.DialogResult = true;
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };

        textBox.Loaded += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        var ok2 = dialog.ShowDialog();
        return ok2 == true ? result : null;
    }
}
