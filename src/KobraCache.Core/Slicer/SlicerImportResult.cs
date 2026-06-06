using KobraCache.Core.Models;

namespace KobraCache.Core.Slicer;

public sealed record SlicerImportResult(
    string ConfigPath,
    string? CloudAccessToken,
    IReadOnlyList<PrinterIdentity> LanPrinters)
{
    public bool HasCloudToken => !string.IsNullOrWhiteSpace(CloudAccessToken);
}
