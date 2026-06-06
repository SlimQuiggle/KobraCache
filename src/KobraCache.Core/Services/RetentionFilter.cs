using KobraCache.Core.Models;

namespace KobraCache.Core.Services;

public sealed class RetentionFilter
{
    public DeletePreview CreatePreview(
        PrinterIdentity printer,
        PrinterRuntimeStatus printerStatus,
        IEnumerable<PrinterCacheFile> files,
        RetentionPolicy policy,
        DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? DateTimeOffset.Now;
        var cutoff = policy.GetCutoff(effectiveNow);
        var items = files
            .OrderBy(file => file.StorageTarget)
            .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(file => CreatePreviewItem(file, printerStatus, cutoff))
            .ToArray();

        return new DeletePreview(printer, printerStatus, policy, cutoff, items);
    }

    private static DeletePreviewItem CreatePreviewItem(
        PrinterCacheFile file,
        PrinterRuntimeStatus printerStatus,
        DateTimeOffset cutoff)
    {
        if (printerStatus != PrinterRuntimeStatus.Idle)
        {
            return new DeletePreviewItem(file, false, "Printer is not confirmed idle.");
        }

        if (file.IsCurrentJob)
        {
            return new DeletePreviewItem(file, false, "Current or active print file.");
        }

        var fileDate = file.BestDate;
        if (fileDate is null)
        {
            return new DeletePreviewItem(file, false, "No reliable file date.");
        }

        if (fileDate.Value > cutoff)
        {
            return new DeletePreviewItem(file, false, "Newer than selected cutoff.");
        }

        return new DeletePreviewItem(file, true, "Eligible.");
    }
}
