using System.Net;
using KobraCache.Core.Models;

namespace KobraCache.Core.Services;

public sealed class ManualPrinterService
{
    public PrinterIdentity AddManualIp(string ipAddress)
    {
        var normalized = ipAddress.Trim();
        if (!IPAddress.TryParse(normalized, out _))
        {
            throw new ArgumentException("Enter a valid printer IP address.", nameof(ipAddress));
        }

        return new PrinterIdentity
        {
            Key = $"manual:{normalized}",
            DisplayName = normalized,
            IpAddress = normalized,
            Source = PrinterSource.ManualIp,
            ConnectionMode = PrinterConnectionMode.ProbeOnly
        };
    }
}
