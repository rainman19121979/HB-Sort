using HBSort.ViewModels;

namespace HBSort.Services;

/// <summary>
/// v0.1.24-beta.1 Phase 1.5: Default-Implementation von
/// <see cref="ISortInstructionPresenter"/>. Haelt eine Referenz auf
/// MainViewModel und leitet an dessen ScanViewModel weiter.
///
/// Bewusst keine Dispatcher-Logik hier - das uebernimmt
/// <see cref="BinInstructionViewModel.ShowSortInstruction"/> intern
/// (DispatcherPriority.Render-Wrapper).
/// </summary>
public sealed class SortInstructionPresenter : ISortInstructionPresenter
{
    private readonly MainViewModel _mainVm;

    public SortInstructionPresenter(MainViewModel mainVm)
    {
        _mainVm = mainVm;
    }

    public void Show(SortInstruction instruction)
    {
        if (instruction == null) return;
        var scan = _mainVm?.ScanViewModel;
        if (scan == null) return;
        scan.ShowSortInstruction(instruction);
    }
}
