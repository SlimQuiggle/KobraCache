namespace KobraCache.Core.Models;

public sealed record DeletePreview(
    PrinterIdentity Printer,
    PrinterRuntimeStatus PrinterStatus,
    RetentionPolicy Policy,
    DateTimeOffset Cutoff,
    IReadOnlyList<DeletePreviewItem> Items)
{
    public int EligibleCount => Items.Count(item => item.IsEligible);
    public long EligibleBytes => Items
        .Where(item => item.IsEligible)
        .Sum(item => item.File.SizeBytes ?? 0);
}
