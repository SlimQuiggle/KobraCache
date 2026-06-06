using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KobraCache.Core.Models;
using KobraCache.Core.Transports;

namespace KobraCache.Core.Cloud;

public sealed class AnycubicCloudClient : IPrinterTransport
{
    private static readonly Uri DefaultBaseUri = new("https://cloud-universe.anycubic.com/p/p/workbench/api/");
    private const string AppId = "f9b3528877c94d5c9c5af32245db46ef";
    private const string AppSecret = "0cf75926606049a3937f56b0373b99fb";
    private const string SlicerVersion = "V3.0.0";
    private const string DeviceType = "pcf";
    private readonly HttpClient _httpClient;

    public AnycubicCloudClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { BaseAddress = DefaultBaseUri };
        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = DefaultBaseUri;
        }
    }

    public async Task<string> AuthenticateSlicerTokenAsync(string slicerAccessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slicerAccessToken))
        {
            throw new InvalidOperationException("Slicer cloud import requires an access token from Slicer Next.");
        }

        var body = new
        {
            device_type = DeviceType,
            access_token = slicerAccessToken
        };

        using var request = CreateJsonRequest(HttpMethod.Post, "v3/public/loginWithAccessToken", null, body);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var token = GetString(document.RootElement, "data", "token")
            ?? GetString(document.RootElement, "token");

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Anycubic login succeeded but did not return a session token.");
        }

        return token!;
    }

    public async Task<IReadOnlyList<PrinterIdentity>> ListPrintersAsync(string slicerAccessToken, CancellationToken cancellationToken = default)
    {
        var sessionToken = await AuthenticateSlicerTokenAsync(slicerAccessToken, cancellationToken).ConfigureAwait(false);
        return await ListPrintersWithSessionTokenAsync(sessionToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PrinterIdentity>> ListPrintersWithSessionTokenAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "work/printer/getPrinters", sessionToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return EnumerateObjects(document.RootElement)
            .Select(item => ToPrinter(item, sessionToken))
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
            return PrinterRuntimeStatus.CredentialsNeeded;
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
            throw new InvalidOperationException("Cloud file listing requires a Slicer cloud token import.");
        }

        var body = new
        {
            page = 1,
            limit = 200
        };

        using var request = CreateJsonRequest(HttpMethod.Post, "work/index/files", printer.CloudAccessToken, body);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

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
            throw new InvalidOperationException("Cloud deletion requires a Slicer cloud token import.");
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
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string? sessionToken)
    {
        var request = new HttpRequestMessage(method, path);
        AddSignedHeaders(request, sessionToken);
        return request;
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, string? sessionToken, object body)
    {
        var request = CreateRequest(method, path, sessionToken);
        var json = JsonSerializer.Serialize(body);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static void AddSignedHeaders(HttpRequestMessage request, string? sessionToken)
    {
        var nonce = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var signatureInput = $"{AppId}{timestamp}{SlicerVersion}{AppSecret}{nonce}{AppId}";
        var signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signatureInput))).ToLowerInvariant();

        request.Headers.TryAddWithoutValidation("Xx-Device-Type", DeviceType);
        request.Headers.TryAddWithoutValidation("Xx-Is-Cn", "1");
        request.Headers.TryAddWithoutValidation("Xx-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("Xx-Signature", signature);
        request.Headers.TryAddWithoutValidation("Xx-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("Xx-Version", SlicerVersion);
        request.Headers.TryAddWithoutValidation("XX-LANGUAGE", "US");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KobraCache", "0.2.0"));

        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            request.Headers.TryAddWithoutValidation("XX-Token", sessionToken);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 500)
        {
            body = body[..500] + "...";
        }

        throw new HttpRequestException($"Anycubic cloud request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    private static PrinterIdentity? ToPrinter(JsonElement item, string sessionToken)
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
            CloudAccessToken = sessionToken,
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
            ?? GetString(item, "old_filename")
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
            CreatedAt = GetDate(item, "time") ?? GetDate(item, "create_time") ?? GetDate(item, "created_at"),
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
            foreach (var propertyName in new[] { "data", "list", "records", "rows", "files", "file_info", "printers" })
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
               GetString(item, "old_filename") is not null ||
               GetString(item, "name") is not null ||
               GetString(item, "printer_id") is not null;
    }

    private static JsonElement? GetProperty(JsonElement item, string propertyName)
    {
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(propertyName, out var value)
            ? value
            : null;
    }

    private static string? GetString(JsonElement item, params string[] path)
    {
        var current = item;
        foreach (var propertyName in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propertyName, out current))
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
            return numeric > 0;
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
