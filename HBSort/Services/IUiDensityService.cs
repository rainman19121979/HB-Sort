namespace HBSort.Services;

/// <summary>UI-Darstellungsdichte.</summary>
public enum UiDensity
{
    Compact,
    Normal,
    Comfortable
}

/// <summary>
/// Wechselt zur Laufzeit die UI-Density (FontSize/Padding/Image-Sizes).
/// Implementierung tauscht das Density-ResourceDictionary in
/// Application.Current.Resources aus.
/// </summary>
public interface IUiDensityService
{
    UiDensity Current { get; }

    /// <summary>Wendet die gewaehlte Density an + persistiert sie in den Settings.</summary>
    Task ApplyAsync(UiDensity density);
}
