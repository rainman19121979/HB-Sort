using HBSort.Core.Services;

namespace HBSort.Helpers;

/// <summary>
/// v0.1.24-beta.1 Phase 2b (Konzept Befund B2 / OPEN-7): erzeugt den
/// Combobox-Suffix-String fuer ein <see cref="StorageBinWithCounts"/>.
///
/// <para>Format (Konzept OPEN-7-Entscheid):</para>
/// <list type="bullet">
///   <item><description>Empty Bin: kein Suffix — "Box 005"</description></item>
///   <item><description>Nur wartend: "Box 005 (2 wartend)"</description></item>
///   <item><description>Nur complete: "Box 005 (3 fertig)"</description></item>
///   <item><description>Wartend + complete (Reifungspfad): "Box 005 (2 wartend, 3 fertig)"</description></item>
///   <item><description>Floating only: "Box 005 (5 Einzelteile)"</description></item>
///   <item><description>Mixed: "Box 005 (2 wartend, 5 Einzelteile)"</description></item>
/// </list>
///
/// Engineering-Prinzip 1.5 (Re-Use): EIN Helper fuer alle 6 Combobox-
/// Aufrufer — keine Copy-Paste-Formatierung in den VMs.
/// </summary>
public static class BinDisplayFormatter
{
    public static string FormatBinDisplayText(StorageBinWithCounts b)
    {
        var parts = new List<string>(3);
        if (b.WaitingCount > 0) parts.Add($"{b.WaitingCount} wartend");
        if (b.CompleteCount > 0) parts.Add($"{b.CompleteCount} fertig");
        if (b.FloatingCount > 0) parts.Add($"{b.FloatingCount} Einzelteile");

        if (parts.Count == 0) return b.Bin.Label;
        return $"{b.Bin.Label} ({string.Join(", ", parts)})";
    }
}
