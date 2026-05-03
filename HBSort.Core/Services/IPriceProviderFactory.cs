namespace HBSort.Core.Services;

/// <summary>
/// Phase 8: Provider-Factory. Liest AppSettings.Prices.Provider und liefert
/// den passenden Singleton-Provider zurueck. Aenderung in Settings -> Provider
/// wechselt live (alle Provider sind als Singleton registriert).
/// </summary>
public interface IPriceProviderFactory
{
    /// <summary>Liefert den aktuell konfigurierten Provider.</summary>
    IPriceProvider GetActiveProvider();
}

public class PriceProviderFactory : IPriceProviderFactory
{
    private readonly ISettingsService _settings;
    private readonly DummyPriceProvider _dummy;
    private readonly BricklinkApiPriceProvider _bl;

    public PriceProviderFactory(
        ISettingsService settings,
        DummyPriceProvider dummy,
        BricklinkApiPriceProvider bl)
    {
        _settings = settings;
        _dummy = dummy;
        _bl = bl;
    }

    public IPriceProvider GetActiveProvider()
    {
        var name = _settings.Current.Prices.Provider ?? "None";
        return name switch
        {
            "BricklinkApi" => _bl,
            _              => _dummy
        };
    }
}
