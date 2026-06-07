using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace KobraCache.Core.Updates;

public sealed record GitHubReleaseAsset(
    string Name,
    Uri DownloadUrl,
    long SizeBytes,
    string? Digest);

public sealed record GitHubReleaseInfo(
    string TagName,
    Version Version,
    Uri HtmlUrl,
    IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed class GitHubReleaseClient
{
    private static readonly Uri DefaultLatestReleaseUri = new("https://api.github.com/repos/SlimQuiggle/KobraCache/releases/latest");
    private readonly HttpClient _httpClient;
    private readonly Uri _latestReleaseUri;

    public GitHubReleaseClient(HttpClient? httpClient = null, Uri? latestReleaseUri = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _latestReleaseUri = latestReleaseUri ?? DefaultLatestReleaseUri;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KobraCache", "1.0"));
        }
    }

    public async Task<GitHubReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(_latestReleaseUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseRelease(document.RootElement);
    }

    public static GitHubReleaseInfo ParseRelease(JsonElement root)
    {
        var tagName = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName) || !TryParseVersionTag(tagName, out var version))
        {
            throw new InvalidOperationException("GitHub release did not include a valid version tag.");
        }

        var htmlUrlText = GetString(root, "html_url") ?? $"https://github.com/SlimQuiggle/KobraCache/releases/tag/{tagName}";
        var assets = new List<GitHubReleaseAsset>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = GetString(asset, "name");
                var downloadUrl = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                assets.Add(new GitHubReleaseAsset(
                    name!,
                    new Uri(downloadUrl!),
                    GetLong(asset, "size") ?? 0,
                    GetString(asset, "digest")));
            }
        }

        return new GitHubReleaseInfo(tagName!, version, new Uri(htmlUrlText), assets);
    }

    public static bool TryParseVersionTag(string tag, out Version version)
    {
        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var prereleaseIndex = trimmed.IndexOfAny(['-', '+']);
        if (prereleaseIndex >= 0)
        {
            trimmed = trimmed[..prereleaseIndex];
        }

        return Version.TryParse(trimmed, out version!);
    }

    public static bool IsNewer(Version latest, Version current)
    {
        return Normalize(latest).CompareTo(Normalize(current)) > 0;
    }

    public static GitHubReleaseAsset? FindWindowsZipAsset(GitHubReleaseInfo release)
    {
        return release.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) ||
            asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version Normalize(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(propertyName, out var child))
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

    private static long? GetLong(JsonElement item, string propertyName)
    {
        var text = GetString(item, propertyName);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
