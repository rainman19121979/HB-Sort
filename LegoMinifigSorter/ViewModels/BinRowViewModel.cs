using CommunityToolkit.Mvvm.ComponentModel;
using LegoMinifigSorter.Core.Models;

namespace LegoMinifigSorter.ViewModels;

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
