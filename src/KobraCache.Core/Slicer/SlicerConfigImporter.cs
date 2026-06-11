using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KobraCache.Core.Models;

namespace KobraCache.Core.Slicer;

public sealed partial class SlicerConfigImporter
{
    public static string GetDefaultConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "AnycubicSlicerNext", "AnycubicSlicerNext.conf");
    }

    public async Task<SlicerImportResult> ImportAsync(string? configPath = null, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(configPath) ? GetDefaultConfigPath() : configPath;
        if (!File.Exists(path))
        {
            return new SlicerImportResult(path, null, Array.Empty<PrinterIdentity>());
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var jsonText = ExtractFirstJsonObject(content);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return new SlicerImportResult(path, null, Array.Empty<PrinterIdentity>());
        }

        using var document = JsonDocument.Parse(jsonText, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = document.RootElement;
        var token = TryGetString(root, "anycubic_cloud", "access_token")
            ?? TryGetLatestCloudSessionToken(path, DateTimeOffset.UtcNow);
        var rawLanList = TryGetString(root, "anycubic_remote_printing", "machine_list_of_LAN")
            ?? TryGetString(root, "machine_list_of_LAN");

        var lanPrinters = string.IsNullOrWhiteSpace(rawLanList)
            ? Array.Empty<PrinterIdentity>()
            : ParseLanPrinters(rawLanList).ToArray();

        return new SlicerImportResult(path, token, lanPrinters);
    }

    public static string? ExtractFirstJsonObject(string content)
    {
        var start = content.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < content.Length; i++)
        {
            var ch = content[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return content[start..(i + 1)];
                }
            }
        }

        return null;
    }

    public static IReadOnlyList<JsonElement> DecodeLanMachineList(string rawValue)
    {
        var trimmed = rawValue.Trim();
        using var document = JsonDocument.Parse(
            trimmed.StartsWith("[", StringComparison.Ordinal)
                ? trimmed
                : DecodeAnycubicValue(trimmed));

        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray()
            : Array.Empty<JsonElement>();
    }

    private static IEnumerable<PrinterIdentity> ParseLanPrinters(string rawValue)
    {
        foreach (var item in DecodeLanMachineList(rawValue))
        {
            var ip = TryGetString(item, "ip");
            var deviceId = TryGetString(item, "deviceId") ?? TryGetString(item, "device_id");
            var modeId = TryGetString(item, "modeId") ?? TryGetString(item, "modelId") ?? TryGetString(item, "model_id");
            var name = TryGetString(item, "name") ?? TryGetString(item, "printerName");
            var model = TryGetString(item, "modelName") ?? TryGetString(item, "model");
            var broker = TryGetString(item, "broker");

            yield return new PrinterIdentity
            {
                Key = FirstNonBlank(deviceId, ip, name, Guid.NewGuid().ToString("N")),
                DisplayName = name,
                ModelName = model,
                IpAddress = ip,
                Source = PrinterSource.SlicerLan,
                ConnectionMode = PrinterConnectionMode.LanMqtt,
                ModeId = modeId,
                DeviceId = deviceId,
                MqttBroker = broker,
                MqttUsername = TryGetString(item, "username"),
                MqttPassword = TryGetString(item, "password")
            };
        }
    }

    private static string DecodeAnycubicValue(string encryptedData)
    {
        var firstPass = DecodeStep(encryptedData);
        var firstText = Encoding.ASCII.GetString(firstPass);
        var padded = AddBase64Padding(firstText);
        var secondPass = DecodeStep(padded);
        return Encoding.UTF8.GetString(secondPass);
    }

    private static byte[] DecodeStep(string value)
    {
        var bytes = Convert.FromBase64String(AddBase64Padding(value.Trim()));
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(bytes[i] - 5));
        }

        return bytes;
    }

    private static string AddBase64Padding(string value)
    {
        var trimmed = value.Trim();
        var remainder = trimmed.Length % 4;
        return remainder == 0 ? trimmed : trimmed + new string('=', 4 - remainder);
    }

    private static string? TryGetLatestCloudSessionToken(string configPath, DateTimeOffset now)
    {
        var configDirectory = Path.GetDirectoryName(configPath);
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            return null;
        }

        var logDirectory = Path.Combine(configDirectory, "log");
        if (!Directory.Exists(logDirectory))
        {
            return null;
        }

        foreach (var logPath in Directory.EnumerateFiles(logDirectory, "MainApp_*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            string text;
            try
            {
                text = File.ReadAllText(logPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var matches = CloudMqttTokenRegex().Matches(text);
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var token = matches[i].Groups["token"].Value;
                if (IsUsableAnycubicSessionToken(token, now))
                {
                    return token;
                }
            }
        }

        return null;
    }

    private static bool IsUsableAnycubicSessionToken(string token, DateTimeOffset now)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            var payloadBytes = Convert.FromBase64String(AddBase64Padding(parts[1].Replace('-', '+').Replace('_', '/')));
            using var document = JsonDocument.Parse(payloadBytes);
            var root = document.RootElement;
            if (TryGetString(root, "user_id") is null && TryGetString(root, "userId") is null)
            {
                return false;
            }

            var exp = TryGetString(root, "exp");
            if (!long.TryParse(exp, out var unixExpiration))
            {
                return false;
            }

            return DateTimeOffset.FromUnixTimeSeconds(unixExpiration) > now.AddMinutes(5);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    [GeneratedRegex(@"CloudMqttClient\.cpp:\d+\s+connect url: .*?, username: .*?, token: (?<token>[A-Za-z0-9_\-]+(?:\.[A-Za-z0-9_\-]+){2})", RegexOptions.IgnoreCase)]
    private static partial Regex CloudMqttTokenRegex();
}
