using System.ComponentModel;
using System.Runtime.CompilerServices;
using KobraCache.Core.Models;

namespace KobraCache.Desktop;

public sealed class PrinterRow : INotifyPropertyChanged
{
    private PrinterIdentity _printer;
    private PrinterRuntimeStatus _status = PrinterRuntimeStatus.Unknown;

    public PrinterRow(PrinterIdentity printer)
    {
        _printer = printer;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PrinterIdentity Printer => _printer;

    public string Name => _printer.NameOrAddress;

    public string IpAddress => string.IsNullOrWhiteSpace(_printer.IpAddress) ? "" : _printer.IpAddress;

    public string SourceText
    {
        get
        {
            if (HasLanCredentials && HasCloudToken)
            {
                return "Slicer LAN + Cloud";
            }

            return _printer.Source switch
            {
                PrinterSource.ManualIp => "Manual IP",
                PrinterSource.SlicerLan => "Slicer LAN",
                PrinterSource.SlicerCloud => "Slicer Cloud",
                _ => _printer.Source.ToString()
            };
        }
    }

    public string CapabilityText
    {
        get
        {
            if (HasLanCredentials && HasCloudToken)
            {
                return "LAN + Cloud";
            }

            if (HasLanCredentials)
            {
                return "LAN MQTT";
            }

            if (HasCloudToken)
            {
                return "Cloud";
            }

            return "Probe only";
        }
    }

    public bool HasLanCredentials => _printer.HasLanCredentials;

    public bool HasCloudToken =>
        !string.IsNullOrWhiteSpace(_printer.CloudAccessToken) &&
        (!string.IsNullOrWhiteSpace(_printer.CloudPrinterId) || !string.IsNullOrWhiteSpace(_printer.CloudKey));

    public PrinterRuntimeStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => _status.ToString();

    public string DetailText
    {
        get
        {
            var parts = new List<string> { SourceText };
            if (!string.IsNullOrWhiteSpace(_printer.ModelName))
            {
                parts.Add(_printer.ModelName);
            }

            if (HasLanCredentials)
            {
                parts.Add("LAN credentials available");
            }

            if (HasCloudToken)
            {
                parts.Add("Cloud token in memory");
            }

            if (!HasLanCredentials && !HasCloudToken)
            {
                parts.Add("No delete credentials");
            }

            return string.Join(" | ", parts);
        }
    }

    public void Merge(PrinterIdentity incoming)
    {
        _printer = MergePrinters(_printer, incoming);
        OnPropertyChanged(string.Empty);
    }

    private static PrinterIdentity MergePrinters(PrinterIdentity existing, PrinterIdentity incoming)
    {
        var hasIncomingDeleteCapability = incoming.HasLanCredentials || HasCloudCapability(incoming);
        var connectionMode = HasCloudCapability(incoming) || HasCloudCapability(existing)
            ? PrinterConnectionMode.Cloud
            : incoming.HasLanCredentials || existing.HasLanCredentials
                ? PrinterConnectionMode.LanMqtt
                : PrinterConnectionMode.ProbeOnly;

        return existing with
        {
            Key = SelectKey(existing, incoming),
            DisplayName = FirstNonBlank(incoming.DisplayName, existing.DisplayName),
            ModelName = FirstNonBlank(incoming.ModelName, existing.ModelName),
            IpAddress = FirstNonBlank(incoming.IpAddress, existing.IpAddress),
            Source = existing.Source == PrinterSource.ManualIp && hasIncomingDeleteCapability ? incoming.Source : existing.Source,
            ConnectionMode = connectionMode,
            ModeId = FirstNonBlank(incoming.ModeId, existing.ModeId),
            DeviceId = FirstNonBlank(incoming.DeviceId, existing.DeviceId),
            MqttBroker = FirstNonBlank(incoming.MqttBroker, existing.MqttBroker),
            MqttUsername = FirstNonBlank(incoming.MqttUsername, existing.MqttUsername),
            MqttPassword = FirstNonBlank(incoming.MqttPassword, existing.MqttPassword),
            CloudPrinterId = FirstNonBlank(incoming.CloudPrinterId, existing.CloudPrinterId),
            CloudKey = FirstNonBlank(incoming.CloudKey, existing.CloudKey),
            CloudAccessToken = FirstNonBlank(incoming.CloudAccessToken, existing.CloudAccessToken),
            ImportedAt = incoming.ImportedAt
        };
    }

    private static string SelectKey(PrinterIdentity existing, PrinterIdentity incoming)
    {
        if (existing.Source == PrinterSource.ManualIp && incoming.Source != PrinterSource.ManualIp)
        {
            return existing.Key;
        }

        return FirstNonBlank(existing.Key, incoming.Key) ?? existing.Key;
    }

    private static bool HasCloudCapability(PrinterIdentity printer)
    {
        return !string.IsNullOrWhiteSpace(printer.CloudAccessToken) &&
               (!string.IsNullOrWhiteSpace(printer.CloudPrinterId) || !string.IsNullOrWhiteSpace(printer.CloudKey));
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
