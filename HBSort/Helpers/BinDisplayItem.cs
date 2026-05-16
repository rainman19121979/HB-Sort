using HBSort.Core.Models;

namespace HBSort.Helpers;

/// <summary>
/// v0.1.24-beta.1 Phase 2b (Konzept Befund B2 / OPEN-7): Adapter fuer die
/// Bin-Comboboxen. Wrapt das echte <see cref="StorageBin"/> plus einen
/// vorgerechneten <see cref="DisplayText"/> (z.B. "Box 005 (2 wartend, 3 fertig)").
/// <para>
/// Combobox-XAML nutzt <c>DisplayMemberPath="DisplayText"</c>; VM-Konsumenten
/// greifen ueber <see cref="Bin"/>.<see cref="StorageBin.Id"/> bzw.
/// <see cref="StorageBin.Label"/> auf die Originaldaten zu.
/// </para>
/// <para>
/// <see cref="Id"/>-Convenience-Property: Reference-Equality-Lookups beim
/// Re-Suggest (z.B. <c>AvailableBins.FirstOrDefault(b =&gt; b.Id == suggested.Id)</c>)
/// bleiben dadurch einzeilig.
/// </para>
/// </summary>
public record BinDisplayItem(StorageBin Bin, string DisplayText)
{
    /// <summary>Convenience: gleiches Id-Field wie das gewrappte Bin.</summary>
    public int Id => Bin.Id;
}
