using System.Globalization;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using KobraCache.Core.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace KobraCache.Core.Transports;

public interface ILanMqttGateway
{
    Task<string> SendCommandAsync(
        PrinterIdentity printer,
        string action,
        object payload,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class LanMqttPrinterClient : IPrinterTransport
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);
    private readonly ILanMqttGateway _gateway;

    public LanMqttPrinterClient(ILanMqttGateway? gateway = null)
    {
        _gateway = gateway ?? new MqttNetLanGateway();
    }

    public Task<PrinterRuntimeStatus> GetStatusAsync(PrinterIdentity printer, CancellationToken cancellationToken = default)
    {
        var payload = CreatePayload("info", "query", null);
        return SendAsync(
            printer,
            "info",
            payload,
            document => ParseStatus(document.RootElement),
            cancellationToken);
    }

    public Task<IReadOnlyList<PrinterCacheFile>> ListFilesAsync(
        PrinterIdentity printer,
        StorageTarget target,
        CancellationToken cancellationToken = default)
    {
        var action = target switch
        {
            StorageTarget.LocalCache => "listLocal",
            StorageTarget.Usb => "listUdisk",
            _ => throw new InvalidOperationException("LAN MQTT supports local cache and USB storage only.")
        };

        var payload = CreatePayload("file", action, new Dictionary<string, object?> { ["path"] = "/" });
        return SendAsync(
            printer,
            action,
            payload,
            document => ParseFiles(printer.Key, target, document.RootElement),
            cancellationToken);
    }

    public Task DeleteFileAsync(PrinterIdentity printer, PrinterCacheFile file, CancellationToken cancellationToken = default)
    {
        var action = file.StorageTarget switch
        {
            StorageTarget.LocalCache => "deleteLocal",
            StorageTarget.Usb => "deleteUdisk",
            _ => throw new InvalidOperationException("LAN MQTT supports local cache and USB deletion only.")
        };

        var payload = CreatePayload("file", action, new Dictionary<string, object?>
        {
            ["path"] = string.IsNullOrWhiteSpace(file.Path) ? "/" : file.Path,
            ["filename"] = file.FileName
        });

        return SendAsync(printer, action, payload, _ => true, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        PrinterIdentity printer,
        string action,
        object payload,
        Func<JsonDocument, T> parser,
        CancellationToken cancellationToken)
    {
        if (!printer.HasLanCredentials)
        {
            throw new InvalidOperationException("LAN MQTT requires imported Slicer LAN credentials.");
        }

        var responseText = await _gateway.SendCommandAsync(printer, action, payload, DefaultTimeout, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(responseText);
        return parser(document);
    }

    private static object CreatePayload(string type, string action, object? data)
    {
        return new
        {
            type,
            action,
            msgid = Guid.NewGuid().ToString(),
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data
        };
    }

    private static string? GetPayloadMsgId(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("msgid", out var value) ? value.GetString() : null;
    }

    private static string? GetPayloadType(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("type", out var value) ? value.GetString() : null;
    }

    private static bool IsExpectedResponse(string text, string? msgId, string action, string topic)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var responseMsgId = GetString(root, "msgid");
            var responseAction = GetString(root, "action") ?? GetString(root, "type");
            var isFileList = IsListAction(action) && ContainsFileListPayload(root);
            return (!string.IsNullOrWhiteSpace(msgId) && responseMsgId == msgId) ||
                   string.Equals(responseAction, action, StringComparison.OrdinalIgnoreCase) ||
                   isFileList ||
                   topic.EndsWith($"/{action}/report", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (string Host, int Port, bool UseTls) ResolveBroker(PrinterIdentity printer)
    {
        if (Uri.TryCreate(printer.MqttBroker, UriKind.Absolute, out var uri))
        {
            var port = uri.Port > 0 ? uri.Port : uri.Scheme.Equals("mqtts", StringComparison.OrdinalIgnoreCase) ? 9883 : 2883;
            return (uri.Host, port, uri.Scheme.Equals("mqtts", StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(printer.IpAddress))
        {
            return (printer.IpAddress, 9883, true);
        }

        throw new InvalidOperationException("Printer does not have an MQTT broker or IP address.");
    }

    private static PrinterRuntimeStatus ParseStatus(JsonElement root)
    {
        var data = GetProperty(root, "data") ?? root;
        var state = FirstNonBlank(GetString(data, "state"), GetString(root, "state"), GetString(data, "status"));

        if (state.Contains("free", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("idle", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterRuntimeStatus.Idle;
        }

        if (state.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("print", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("preheat", StringComparison.OrdinalIgnoreCase) ||
            state.Contains("level", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterRuntimeStatus.Busy;
        }

        if (state.Contains("offline", StringComparison.OrdinalIgnoreCase))
        {
            return PrinterRuntimeStatus.Offline;
        }

        return PrinterRuntimeStatus.Unknown;
    }

    private static IReadOnlyList<PrinterCacheFile> ParseFiles(string printerKey, StorageTarget target, JsonElement root)
    {
        return EnumerateFileItems(root)
            .Select(item => ToFile(printerKey, target, item))
            .Where(file => file is not null)
            .Cast<PrinterCacheFile>()
            .ToArray();
    }

    private static PrinterCacheFile? ToFile(string printerKey, StorageTarget target, JsonElement item)
    {
        var name = item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : GetString(item, "filename")
              ?? GetString(item, "fileName")
              ?? GetString(item, "old_filename")
              ?? GetString(item, "file_name")
              ?? GetString(item, "gcode_name")
              ?? GetString(item, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (item.ValueKind == JsonValueKind.Object && IsDirectoryRecord(item))
        {
            return null;
        }

        return new PrinterCacheFile
        {
            PrinterKey = printerKey,
            StorageTarget = target,
            Path = item.ValueKind == JsonValueKind.Object
                ? GetString(item, "path") ?? GetString(item, "filepath") ?? GetString(item, "filePath") ?? "/"
                : "/",
            FileName = name,
            RemoteId = item.ValueKind == JsonValueKind.Object
                ? GetString(item, "taskid") ?? GetString(item, "file_id") ?? GetString(item, "fileID") ?? GetString(item, "id")
                : null,
            SizeBytes = item.ValueKind == JsonValueKind.Object
                ? GetLong(item, "filesize") ?? GetLong(item, "file_size") ?? GetLong(item, "size")
                : null,
            CreatedAt = item.ValueKind == JsonValueKind.Object
                ? GetDate(item, "time") ?? GetDate(item, "create_time") ?? GetDate(item, "created_at")
                : null,
            ModifiedAt = item.ValueKind == JsonValueKind.Object
                ? GetDate(item, "timestamp") ?? GetDate(item, "update_time") ?? GetDate(item, "updated_at") ?? GetDate(item, "modified_at") ?? GetDate(item, "mtime")
                : null,
            IsCurrentJob = item.ValueKind == JsonValueKind.Object && (GetBool(item, "is_current") ?? GetBool(item, "is_printing") ?? GetBool(item, "printing") ?? false)
        };
    }

    private static IEnumerable<JsonElement> EnumerateFileItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var nested in EnumerateFileItems(item))
                {
                    yield return nested;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.String && LooksLikeFileName(root.GetString()))
        {
            yield return root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeFileRecord(root))
            {
                yield return root;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    foreach (var nested in EnumerateFileItems(property.Value))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    private static bool LooksLikeFileRecord(JsonElement item)
    {
        if (GetString(item, "filename") is not null ||
            GetString(item, "fileName") is not null ||
            GetString(item, "old_filename") is not null ||
            GetString(item, "file_name") is not null ||
            GetString(item, "gcode_name") is not null)
        {
            return true;
        }

        return GetString(item, "name") is not null &&
               (HasProperty(item, "size") ||
                HasProperty(item, "filesize") ||
                HasProperty(item, "file_size") ||
                HasProperty(item, "path") ||
                HasProperty(item, "filepath") ||
                HasProperty(item, "filePath") ||
                HasProperty(item, "is_dir") ||
                HasProperty(item, "isDir") ||
                HasProperty(item, "update_time") ||
                HasProperty(item, "timestamp"));
    }

    private static bool IsDirectoryRecord(JsonElement item)
    {
        var type = GetString(item, "type") ?? GetString(item, "filetype") ?? GetString(item, "file_type");
        return GetBool(item, "is_dir") == true ||
               GetBool(item, "isDir") == true ||
               string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsFileListPayload(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    return true;
                }

                if (ContainsFileListPayload(property.Value))
                {
                    return true;
                }

                continue;
            }

            if (IsFileListContainerName(property.Name) && property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                return true;
            }

            if (ContainsFileListPayload(property.Value))
            {
                return true;
            }
        }

        return LooksLikeFileRecord(root);
    }

    private static bool ContainsFileListPayload(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return ContainsFileListPayload(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsFileListContainerName(string name)
    {
        return name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("files", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("list", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("records", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("rows", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("file_info", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("fileList", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("file_list", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("items", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("children", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsListAction(string action)
    {
        return action.Equals("listLocal", StringComparison.OrdinalIgnoreCase) ||
               action.Equals("listUdisk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFileName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !value.EndsWith("/", StringComparison.Ordinal) &&
               (value.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".gco", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".gc", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasProperty(JsonElement item, string name)
    {
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out _);
    }

    private static JsonElement? GetProperty(JsonElement item, string name)
    {
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var child) ? child : null;
    }

    private static string? GetString(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var child))
        {
            return null;
        }

        return child.ValueKind switch
        {
            JsonValueKind.String => child.GetString(),
            JsonValueKind.Number => child.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static long? GetLong(JsonElement item, string name)
    {
        var text = GetString(item, name);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool? GetBool(JsonElement item, string name)
    {
        var text = GetString(item, name);
        if (text == "1")
        {
            return true;
        }

        if (text == "0")
        {
            return false;
        }

        return bool.TryParse(text, out var value) ? value : null;
    }

    private static DateTimeOffset? GetDate(JsonElement item, string name)
    {
        var text = GetString(item, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            return null;
        }

        return unix > 9_999_999_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
            : DateTimeOffset.FromUnixTimeSeconds(unix);
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

    private sealed class MqttNetLanGateway : ILanMqttGateway
    {
        public async Task<string> SendCommandAsync(
            PrinterIdentity printer,
            string action,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var msgId = GetPayloadMsgId(payload);
            var commandType = GetPayloadType(payload) ?? action;
            var responseTopic = "anycubic/anycubicCloud/v1/printer/public/#";
            var commandTopics = BuildCommandTopics(printer, commandType, IsReadOnlyAction(action));
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var factory = new MqttFactory();
            using var client = factory.CreateMqttClient();
            await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

            client.ApplicationMessageReceivedAsync += args =>
            {
                var text = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
                if (!IsExpectedResponse(text, msgId, action, args.ApplicationMessage.Topic))
                {
                    return Task.CompletedTask;
                }

                if (!IsListAction(action) || ContainsFileListPayload(text))
                {
                    completion.TrySetResult(text);
                }

                return Task.CompletedTask;
            };

            var options = BuildOptions(printer);
            await client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            await client.SubscribeAsync(responseTopic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken).ConfigureAwait(false);

            var json = JsonSerializer.Serialize(payload);
            foreach (var commandTopic in commandTopics)
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(commandTopic)
                    .WithPayload(json)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            }

            var responseText = await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return responseText;
        }

        private static IReadOnlyList<string> BuildCommandTopics(
            PrinterIdentity printer,
            string commandType,
            bool includeFallbacks)
        {
            var primarySender = IsLegacyServerTopicModel(printer.ModeId) ? "server" : "slicer";
            var fallbackSender = primarySender == "slicer" ? "server" : "slicer";
            var primary = BuildCommandTopic(primarySender, printer, commandType);
            if (!includeFallbacks)
            {
                return [primary];
            }

            var fallback = BuildCommandTopic(fallbackSender, printer, commandType);
            return primary.Equals(fallback, StringComparison.OrdinalIgnoreCase)
                ? [primary]
                : [primary, fallback];
        }

        private static string BuildCommandTopic(string sender, PrinterIdentity printer, string commandType)
        {
            return $"anycubic/anycubicCloud/v1/{sender}/printer/{printer.ModeId}/{printer.DeviceId}/{commandType}";
        }

        private static bool IsLegacyServerTopicModel(string? modeId)
        {
            return modeId is "20021" or "20022" or "20023";
        }

        private static bool IsReadOnlyAction(string action)
        {
            return action.Equals("info", StringComparison.OrdinalIgnoreCase) ||
                   IsListAction(action);
        }

        private static MqttClientOptions BuildOptions(PrinterIdentity printer)
        {
            var (host, port, useTls) = ResolveBroker(printer);
            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId($"KobraCache-{Guid.NewGuid():N}")
                .WithTcpServer(host, port)
                .WithCredentials(printer.MqttUsername, printer.MqttPassword)
                .WithCleanSession();

            if (useTls)
            {
                optionsBuilder.WithTlsOptions(options =>
                {
                    // Anycubic LAN brokers commonly use local printer certificates that do not chain to a public CA.
                    options.UseTls();
                    options.WithSslProtocols(SslProtocols.Tls12);
                    options.WithAllowUntrustedCertificates(true);
                    options.WithIgnoreCertificateChainErrors(true);
                    options.WithIgnoreCertificateRevocationErrors(true);
                    options.WithCertificateValidationHandler(_ => true);
                });
            }

            return optionsBuilder.Build();
        }
    }
}
