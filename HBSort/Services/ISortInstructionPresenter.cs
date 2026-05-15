namespace HBSort.Services;

/// <summary>
/// v0.1.24-beta.1 Phase 1.5: zentrale Schnittstelle zum Anzeigen des
/// Post-Save-Sortier-Modals (SortInstruction). Aufrufer aus verschachtelten
/// Dialogen (DismantleWizard, MinifigSummary, kuenftige Dialoge) muessen sich
/// nicht mehr durch die Owner.DataContext-Hierarchie hangeln um auf
/// MainViewModel.ScanViewModel.BinInstruction zuzugreifen.
///
/// Wurzel-Fix fuer Befund "kein Popup aus Lagertab-Pfad" (Praxis-Test
/// 2026-05-15): der bisherige Owner.DataContext-Cast schlug fehl wenn der
/// DismantleWizard aus MinifigSummaryDialog geoeffnet wurde (Owner-Kette
/// MainWindow -> MinifigSummary -> DismantleWizard, Cast traf nur den
/// MinifigSummary-VM und nicht den MainViewModel).
///
/// Engineering-Prinzip 1.1 (keine Pflaster): Owner-Walk waere Pflaster,
/// dieser Service ist Wurzel-Fix - Aufrufer brauchen die Window-Hierarchie
/// gar nicht mehr zu kennen.
/// </summary>
public interface ISortInstructionPresenter
{
    /// <summary>
    /// Zeigt das Post-Save-Modal mit der gegebenen SortInstruction.
    /// No-op wenn MainViewModel oder ScanViewModel nicht verfuegbar
    /// (Test-Pfade, Shutdown).
    /// </summary>
    void Show(HBSort.ViewModels.SortInstruction instruction);
}
