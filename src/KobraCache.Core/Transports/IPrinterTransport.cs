using KobraCache.Core.Models;

namespace KobraCache.Core.Transports;

public interface IPrinterTransport
{
    Task<PrinterRuntimeStatus> GetStatusAsync(PrinterIdentity printer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PrinterCacheFile>> ListFilesAsync(PrinterIdentity printer, StorageTarget target, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(PrinterIdentity printer, PrinterCacheFile file, CancellationToken cancellationToken = default);
}
