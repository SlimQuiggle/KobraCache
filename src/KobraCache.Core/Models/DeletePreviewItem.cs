namespace KobraCache.Core.Models;

public sealed record DeletePreviewItem(
    PrinterCacheFile File,
    bool IsEligible,
    string Reason);
