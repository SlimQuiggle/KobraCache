using System.Net;

namespace KobraCache.Core.Models;

public sealed record PrinterIdentity
{
    public required string Key { get; init; }
    public string? DisplayName { get; init; }
    public string? ModelName { get; init; }
    public string? IpAddress { get; init; }
    public PrinterSource Source { get; init; }
    public PrinterConnectionMode ConnectionMode { get; init; }
    public string? ModeId { get; init; }
    public string? DeviceId { get; init; }
    public string? MqttBroker { get; init; }
    public string? MqttUsername { get; init; }
    public string? MqttPassword { get; init; }
    public string? CloudPrinterId { get; init; }
    public string? CloudKey { get; init; }
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.Now;

    public string NameOrAddress => FirstNonBlank(DisplayName, ModelName, IpAddress, Key);

    public bool HasLanCredentials =>
        !string.IsNullOrWhiteSpace(DeviceId) &&
        !string.IsNullOrWhiteSpace(ModeId) &&
        !string.IsNullOrWhiteSpace(MqttUsername) &&
        !string.IsNullOrWhiteSpace(MqttPassword);

    public bool HasValidIp =>
        !string.IsNullOrWhiteSpace(IpAddress) &&
        IPAddress.TryParse(IpAddress, out _);

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "Unknown printer";
    }
}
