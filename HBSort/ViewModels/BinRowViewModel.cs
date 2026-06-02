using CommunityToolkit.Mvvm.ComponentModel;
using HBSort.Core.Models;

namespace HBSort.ViewModels;

/// <summary>
/// Eine Zeile in der Lagerfach-Liste. Buendelt das Bin mit den
/// Belegungsangaben (Anzahl Figuren, Anzahl FloatingParts).
/// </summary>
public partial class BinRowViewModel : ObservableObject
{
    public int Id { get; }
    public StorageBin Bin { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private int _minifigCount;
    [ObservableProperty] private int _floatingPartCount;
    [ObservableProperty] private DateTime _createdAt;

    /// <summary>
    /// Bulk-Auswahl-Markierung (v0.1.25-beta.1). Reiner UI-State, KEINE
    /// Persistenz - die Checkbox-Spalte im Lagerfach-DataGrid bindet hierauf.
    /// Beim ReloadAsync wird die Zeilen-Liste neu aufgebaut, dadurch geht die
    /// Auswahl ohnehin verloren (kein extra Reset noetig).
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>"Frei" wenn weder Figuren noch Teile drin sind.</summary>
    public bool IsFree => MinifigCount == 0 && FloatingPartCount == 0;

    /// <summary>Anzeige-Text fuer die Status-Spalte.</summary>
    public string StatusText => IsFree ? "Frei" : "Belegt";

    public BinRowViewModel(StorageBin bin, int minifigCount, int floatingPartCount)
    {
        Bin = bin;
        Id = bin.Id;
        _label = bin.Label;
        _minifigCount = minifigCount;
        _floatingPartCount = floatingPartCount;
        _createdAt = bin.CreatedAt;
    }
}
