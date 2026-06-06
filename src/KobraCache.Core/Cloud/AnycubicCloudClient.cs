using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KobraCache.Core.Models;
using KobraCache.Core.Transports;

namespace KobraCache.Core.Cloud;

public sealed class AnycubicCloudClient : IPrinterTransport
{
    private static readonly Uri DefaultBaseUri = new("https://cloud-universe.anycubic.com/p/p/workbench/api/");
    private readonly HttpClient _httpClient;

    public AnycubicCloudClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { BaseAddress = DefaultBaseUri };
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = DefaultBaseUri;
        }
    }

    public async Task<IReadOnlyList<PrinterIdentity>> ListPrintersAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "work/printer/getPrinters", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return EnumerateObjects(document.RootElement)
            .Select(item => ToPrinter(item, accessToken))
            .Where(printer => printer is not null)
            .Cast<PrinterIdentity>()
            .GroupBy(printer => printer.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public async Task<PrinterRuntimeStatus> GetStatusAsync(PrinterIdentity printer, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printer.CloudAccessToken))
        {
            return PrinterRuntimeStatus.Unknown;
        }

        using var request = CreateRequest(HttpMethod.Get, "work/printer/printersStatus", printer.CloudAccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return PrinterRuntimeStatus.Unknown;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var matching = EnumerateObjects(document.RootElement)
            .FirstOrDefault(item => MatchesPrinter(item, printer));

        return matching.ValueKind == JsonValueKind.Undefined
            ? PrinterRuntimeStatus.Unknown
            : ParseStatus(matching);
    }

    public async Task<IReadOnlyList<PrinterCacheFile>> ListFilesAsync(
        PrinterIdentity printer,
        StorageTarget target,
        CancellationToken cancellationToken = default)
    {
        if (target != StorageTarget.Cloud)
        {
            return Array.Empty<PrinterCacheFile>();
        }

        if (string.IsNullOrWhiteSpace(printer.CloudAccessToken))
        {
            throw new InvalidOperationException("Cloud file listing requires a Slicer cloud token.");
        }

        var body = new
        {
            page = 1,
            page_size = 200,
            pageSize = 200,
            type = "gcode"
        };

        using var request = CreateJsonRequest(HttpMethod.Post, "work/index/files", printer.CloudAccessToken, body);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return EnumerateObjects(document.RootElement)
            .Select(item => ToFile(printer.Key, StorageTarget.Cloud, item))
            .Where(file => file is not null)
            .Cast<PrinterCacheFile>()
            .ToArray();
    }

    public async Task DeleteFileAsync(PrinterIdentity printer, PrinterCacheFile file, CancellationToken cancellationToken = default)
    {
        if (file.StorageTarget != StorageTarget.Cloud)
        {
            throw new InvalidOperationException("This cloud client can only delete cloud files.");
        }

        if (string.IsNullOrWhiteSpace(printer.CloudAccessToken))
        {
            throw new InvalidOperationException("Cloud deletion requires a Slicer cloud token.");
        }

        var fileId = file.RemoteId ?? file.FileName;
        var body = new
        {
            file_id = fileId,
            file_ids = new[] { fileId },
            fileIds = new[] { fileId },
            ids = new[] { fileId }
        };

        using var request = CreateJsonRequest(HttpMethod.Post, "work/index/delFiles", printer.CloudAccessToken, body);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        AddAuthHeaders(request, accessToken);
        return request;
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, string accessToken, object body)
    {
        var request = CreateRequest(method, path, accessToken);
        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static void AddAuthHeaders(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("XX-Token", accessToken);
        request.Headers.TryAddWithoutValidation("XX-LANGUAGE", CultureInfo.CurrentUICulture.Name.Replace('-', '_'));
    }

    private static PrinterIdentity? ToPrinter(JsonElement item, string accessToken)
    {
        var id = GetString(item, "id") ?? GetString(item, "printer_id") ?? GetString(item, "device_id");
        var key = GetString(item, "key") ?? GetString(item, "nonce");
        var name = GetString(item, "name") ?? GetString(item, "printer_name");
        var model = GetString(item, "model") ?? GetString(item, "machine_name");
        var machineData = GetProperty(item, "machine_data");
        var ip = GetString(item, "ip") ?? (machineData is { } data ? GetString(data, "ip") : null);

        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new PrinterIdentity
        {
            Key = $"cloud:{FirstNonBlank(id, key, name)}",
            CloudPrinterId = id,
            CloudKey = key,
            CloudAccessToken = accessToken,
            DisplayName = name,
            ModelName = model,
            IpAddress = ip,
            Source = PrinterSource.SlicerCloud,
            ConnectionMode = PrinterConnectionMode.Cloud
        };
    }

    private static PrinterCacheFile? ToFile(string printerKey, StorageTarget target, JsonElement item)
    {
        var name = GetString(item, "filename")
            ?? GetString(item, "file_name")
            ?? GetString(item, "gcode_name")
            ?? GetString(item, "name");

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new PrinterCacheFile
        {
            PrinterKey = printerKey,
            StorageTarget = target,
            Path = GetString(item, "path") ?? GetString(item, "filepath") ?? "/",
            FileName = name,
            RemoteId = GetString(item, "file_id") ?? GetString(item, "fileID") ?? GetString(item, "id"),
            SizeBytes = GetLong(item, "filesize") ?? GetLong(item, "file_size") ?? GetLong(item, "size"),
            CreatedAt = GetDate(item, "create_time") ?? GetDate(item, "created_at"),
            ModifiedAt = GetDate(item, "update_time") ?? GetDate(item, "updated_at") ?? GetDate(item, "last_update_time"),
            IsCurrentJob = GetBool(item, "is_printing") ?? false
        };
    }

    private static PrinterRuntimeStatus ParseStatus(JsonElement item)
    {
        var text = FirstNonBlank(
            GetString(item, "state"),
            GetString(item, "status"),
            GetString(item, "print_status"),
            GetString(item, "ready_status"));

        if (text.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("2", StringComparison.OrdinalIgnoreCase) && GetString(item, "device_status") == "2")
        {
            return PrinterRuntimeStatus.Offline;
        }

        if (text.Contains("busy", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("print", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("2", StringComparison.OrdinalIgnoreCase) ||
            GetBool(item, "is_printing") == true)
        {
            return PrinterRuntimeStatus.Busy;
        }

        if (text.Contains("free", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("idle", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            GetBool(item, "is_printing") == false)
        {
            return PrinterRuntimeStatus.Idle;
        }

        return PrinterRuntimeStatus.Unknown;
    }

    private static bool MatchesPrinter(JsonElement item, PrinterIdentity printer)
    {
        var id = GetString(item, "id") ?? GetString(item, "printer_id") ?? GetString(item, "device_id");
        var key = GetString(item, "key") ?? GetString(item, "nonce");
        return (!string.IsNullOrWhiteSpace(printer.CloudPrinterId) && id == printer.CloudPrinterId) ||
               (!string.IsNullOrWhiteSpace(printer.CloudKey) && key == printer.CloudKey);
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "data", "list", "records", "files", "file_info", "printers" })
            {
                if (root.TryGetProperty(propertyName, out var child))
                {
                    foreach (var item in EnumerateObjects(child))
                    {
                        yield return item;
                    }
                }
            }

            if (LooksLikeRecord(root))
            {
                yield return root;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                foreach (var nested in EnumerateObjects(item))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool LooksLikeRecord(JsonElement item)
    {
        return GetString(item, "id") is not null ||
               GetString(item, "filename") is not null ||
               GetString(item, "name") is not null ||
               GetString(item, "printer_id") is not null;
    }

    private static JsonElement? GetProperty(JsonElement item, string propertyName)
    {
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(propertyName, out var value)
            ? value
            : null;
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static long? GetLong(JsonElement item, string propertyName)
    {
        var text = GetString(item, propertyName);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool? GetBool(JsonElement item, string propertyName)
    {
        var text = GetString(item, propertyName);
        if (bool.TryParse(text, out var boolValue))
        {
            return boolValue;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric > 1;
        }

        return null;
    }

    private static DateTimeOffset? GetDate(JsonElement item, string propertyName)
    {
        var text = GetString(item, propertyName);
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
}
