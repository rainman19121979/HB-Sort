using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HBSort.Core.Services;
using HBSort.ViewModels;
using Serilog;

namespace HBSort.Views;

/// <summary>
/// UX X.20 Teil 1: Read-Only-Dialog fuer einen FloatingPart aus der
/// Lagerliste. Ersetzt die alte ShowInfoAsync-Plain-Text-Variante.
///
/// Das Bild wird asynchron beim Laden ueber den IPartImageProvider geholt
/// (BL-First, lokales Cache-File). So muss der Aufrufer das Bild nicht
/// vorab kennen - er uebergibt nur die InventoryRowItem-Daten.
/// </summary>
public partial class FloatingPartDetailDialog : Window
{
    private readonly InventoryRowItem _row;
    private readonly IPartImageProvider _imageProvider;

    public FloatingPartDetailDialog(InventoryRowItem row, IPartImageProvider imageProvider)
    {
        InitializeComponent();
        _row = row;
        _imageProvider = imageProvider;

        // Header / Felder synchron befuellen.
        NameText.Text = string.IsNullOrWhiteSpace(row.Description) ? "(ohne Name)" : row.Description;
        BlPartIdRun.Text = " " + row.ItemId;
        StorageBinText.Text = "📦 "
            + (string.IsNullOrWhiteSpace(row.StorageBinLabel) ? "(kein Fach)" : row.StorageBinLabel);
        ColorText.Text = $"{row.ColorName} (BL:{row.ColorId})";
        ColorSwatch.Background = row.ColorSwatch ?? Brushes.LightGray;
        QuantityText.Text = row.Quantity.ToString();

        // Bild im Hintergrund laden - der Dialog ist sofort sichtbar, das Bild
        // poppt rein wenn der Loader fertig ist. Bei Fehlern bleibt der Slot leer.
        Loaded += async (_, _) => await LoadImageAsync();
    }

    private async System.Threading.Tasks.Task LoadImageAsync()
    {
        try
        {
            var path = await _imageProvider.GetImageFileByBlAsync("P", _row.ItemId, _row.ColorId);
            if (string.IsNullOrEmpty(path)) return;

            // Bild-Loader analog zum NullToImageSourceConverter (Audit W-7):
            // OnLoad + Freeze, damit der File-Handle direkt wieder frei ist.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new System.Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            PartImage.Source = bmp;
        }
        catch (System.Exception ex)
        {
            Log.Debug(ex, "FloatingPartDetailDialog: Bild-Lookup fehlgeschlagen ({Part}/{Color})",
                _row.ItemId, _row.ColorId);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
