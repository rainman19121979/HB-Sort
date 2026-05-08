using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HBSort.Core.Services;
using HBSort.Services;
using Serilog;

namespace HBSort.ViewModels;

/// <summary>
/// ViewModel fuer den Bulk-Create-Dialog. Live-Vorschau bei jeder Eingabe-Aenderung,
/// Validation und Conflict-Handling.
/// </summary>
public partial class BinBulkCreateViewModel : ObservableObject
{
    private readonly IStorageBinService _binService;
    private readonly INotificationService _notifications;
    private readonly IDialogService _dialogs;

    // UX X.27 (v0.1.14): kein Leerzeichen mehr am Ende des Default-Prefix.
    // Vorher: "Box " + "01" = "Box 01". Jetzt: "Box" + "01" = "Box01".
    // User kann selbst ein Leerzeichen tippen wenn er das Format will.
    [ObservableProperty] private string _prefix = "Box";
    [ObservableProperty] private string _suffix = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNumbers))]
    [NotifyPropertyChangedFor(nameof(IsLetters))]
    private BinSequenceType _sequenceType = BinSequenceType.Numbers;

    [ObservableProperty] private string _startStr = "1";
    [ObservableProperty] private string _endStr = "20";
    [ObservableProperty] private int _padding = 3;

    [ObservableProperty] private string _validationMessage = string.Empty;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _canCreate;

    /// <summary>Vorschau der ersten 3 generierten Labels (oder alle wenn weniger).</summary>
    public ObservableCollection<string> PreviewItems { get; } = new();

    public bool IsNumbers => SequenceType == BinSequenceType.Numbers;
    public bool IsLetters => SequenceType == BinSequenceType.Letters;

    /// <summary>Ergebnis fuer den Caller: alle generierten Labels (nach Anlegen).</summary>
    public List<string>? GeneratedLabels { get; private set; }

    public BinBulkCreateViewModel(
        IStorageBinService binService,
        INotificationService notifications,
        IDialogService dialogs)
    {
        _binService = binService;
        _notifications = notifications;
        _dialogs = dialogs;
        UpdatePreview();
    }

    // PropertyChanged-Handler bauen den Cache aus jeder geaenderten Property neu auf.
    partial void OnPrefixChanged(string value) => UpdatePreview();
    partial void OnSuffixChanged(string value) => UpdatePreview();
    partial void OnSequenceTypeChanged(BinSequenceType value)
    {
        // Sinnvolle Defaults beim Wechsel
        if (value == BinSequenceType.Numbers)
        {
            if (!int.TryParse(StartStr, out _)) StartStr = "1";
            if (!int.TryParse(EndStr, out _)) EndStr = "20";
        }
        else
        {
            if (!BinNameGenerator.IsValidAlpha(StartStr)) StartStr = "A";
            if (!BinNameGenerator.IsValidAlpha(EndStr)) EndStr = "F";
        }
        UpdatePreview();
    }
    partial void OnStartStrChanged(string value) => UpdatePreview();
    partial void OnEndStrChanged(string value) => UpdatePreview();
    partial void OnPaddingChanged(int value) => UpdatePreview();

    private void UpdatePreview()
    {
        PreviewItems.Clear();
        ValidationMessage = string.Empty;
        SummaryText = string.Empty;
        CanCreate = false;

        try
        {
            var labels = BinNameGenerator.Generate(
                Prefix ?? string.Empty,
                Suffix ?? string.Empty,
                SequenceType,
                StartStr ?? string.Empty,
                EndStr ?? string.Empty,
                Math.Max(0, Padding));

            // Erste 3 Labels in der Vorschau, Summary unten
            foreach (var l in labels.Take(3)) PreviewItems.Add(l);
            if (labels.Count > 3)
            {
                SummaryText = $"... {labels.Count - 3} weitere bis '{labels[^1]}'\n" +
                              $"Insgesamt {labels.Count} Lagerfaecher.";
            }
            else
            {
                SummaryText = $"Insgesamt {labels.Count} Lagerfaecher.";
            }
            CanCreate = labels.Count > 0;
        }
        catch (ArgumentException ex)
        {
            ValidationMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task CreateAsync()
    {
        var labels = BinNameGenerator.Generate(
            Prefix ?? string.Empty,
            Suffix ?? string.Empty,
            SequenceType,
            StartStr ?? string.Empty,
            EndStr ?? string.Empty,
            Math.Max(0, Padding));

        if (labels.Count == 0) return;

        // Bestaetigungs-Dialog bei sehr grossen Bulks - hier OK/Abbrechen,
        // weil das Anlegen selbst nicht destruktiv ist (nur viel Aufwand).
        if (labels.Count > 1000)
        {
            if (!await _dialogs.ShowConfirmAsync(
                "Grosse Bulk-Anlage",
                $"Du legst {labels.Count} Lagerfaecher an. Sicher?"))
                return;
        }

        try
        {
            var result = await _binService.CreateBulkAsync(labels);

            if (result.Conflicts.Count > 0)
            {
                var conflictPreview = string.Join(", ", result.Conflicts.Take(5));
                var msg = $"{result.Conflicts.Count} Faecher existieren bereits: {conflictPreview}" +
                          (result.Conflicts.Count > 5 ? $" (und {result.Conflicts.Count - 5} weitere)" : "") +
                          $"\n\nBereits {result.Created.Count} neue Faecher angelegt.";
                await _dialogs.ShowInfoAsync("Anlage mit Konflikten", msg);
            }

            if (result.Created.Count > 0)
            {
                _notifications.ShowSuccess($"{result.Created.Count} Lagerfaecher angelegt.");
                GeneratedLabels = result.Created.Select(c => c.Label).ToList();
            }
            else
            {
                _notifications.ShowWarning("Keine neuen Lagerfaecher angelegt (alle existierten schon).");
                GeneratedLabels = new List<string>();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Bulk-Create fehlgeschlagen");
            await _dialogs.ShowErrorAsync("Bulk-Anlage", $"Fehler: {ex.Message}");
        }
    }
}
