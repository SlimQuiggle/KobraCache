using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using KobraCache.Core.Updates;

namespace KobraCache.Desktop;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    GitHubReleaseInfo? Release,
    GitHubReleaseAsset? Asset);

public sealed record PreparedUpdate(
    GitHubReleaseInfo Release,
    string ExtractDirectory,
    string ScriptPath,
    string TargetDirectory,
    string ExecutablePath);

public sealed class AppUpdateService
{
    private const string ExecutableName = "KobraCache.Desktop.exe";
    private readonly GitHubReleaseClient _releaseClient;
    private readonly HttpClient _httpClient;

    public AppUpdateService(GitHubReleaseClient? releaseClient = null, HttpClient? httpClient = null)
    {
        _releaseClient = releaseClient ?? new GitHubReleaseClient();
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _releaseClient.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        var current = AppVersion.Current;
        if (!GitHubReleaseClient.IsNewer(latest.Version, current))
        {
            return new UpdateCheckResult(false, latest, null);
        }

        var asset = GitHubReleaseClient.FindWindowsZipAsset(latest)
            ?? throw new InvalidOperationException($"Release {latest.TagName} does not include a Windows x64 zip asset.");

        return new UpdateCheckResult(true, latest, asset);
    }

    public async Task<PreparedUpdate> DownloadAndPrepareAsync(
        GitHubReleaseInfo release,
        GitHubReleaseAsset asset,
        CancellationToken cancellationToken = default)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            throw new InvalidOperationException("Cannot find the running KobraCache executable path.");
        }

        var targetDirectory = Path.GetDirectoryName(processPath)
            ?? throw new InvalidOperationException("Cannot find the KobraCache install directory.");
        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KobraCache",
            "Updates",
            release.TagName);
        var downloadDirectory = Path.Combine(updateRoot, "download");
        var extractDirectory = Path.Combine(updateRoot, "extract");
        var zipPath = Path.Combine(downloadDirectory, asset.Name);
        var scriptPath = Path.Combine(updateRoot, "install-update.ps1");

        RecreateDirectory(updateRoot);
        Directory.CreateDirectory(downloadDirectory);
        Directory.CreateDirectory(extractDirectory);

        await DownloadFileAsync(asset.DownloadUrl, zipPath, cancellationToken).ConfigureAwait(false);
        await VerifyDigestAsync(asset, zipPath, cancellationToken).ConfigureAwait(false);
        ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true);

        var extractedExe = Path.Combine(extractDirectory, ExecutableName);
        if (!File.Exists(extractedExe))
        {
            throw new InvalidOperationException($"The update package did not include {ExecutableName}.");
        }

        WriteUpdateScript(scriptPath);
        return new PreparedUpdate(release, extractDirectory, scriptPath, targetDirectory, processPath);
    }

    public void StartInstallOnExit(PreparedUpdate update)
    {
        var processId = Environment.ProcessId.ToString();
        var logPath = Path.Combine(AppLogger.LogDirectory, "updater.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(update.ScriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(processId);
        startInfo.ArgumentList.Add("-SourceDir");
        startInfo.ArgumentList.Add(update.ExtractDirectory);
        startInfo.ArgumentList.Add("-TargetDir");
        startInfo.ArgumentList.Add(update.TargetDirectory);
        startInfo.ArgumentList.Add("-ExePath");
        startInfo.ArgumentList.Add(update.ExecutablePath);
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);

        Process.Start(startInfo);
    }

    private async Task DownloadFileAsync(Uri downloadUrl, string outputPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = File.Create(outputPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyDigestAsync(
        GitHubReleaseAsset asset,
        string zipPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.Digest) ||
            !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expected = asset.Digest["sha256:".Length..].Trim();
        var actual = await GitHubReleaseClient.ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The downloaded update package did not match GitHub's SHA256 digest.");
        }
    }

    private static void RecreateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
    }

    private static void WriteUpdateScript(string scriptPath)
    {
        var script = """
        param(
            [Parameter(Mandatory = $true)][int]$ProcessId,
            [Parameter(Mandatory = $true)][string]$SourceDir,
            [Parameter(Mandatory = $true)][string]$TargetDir,
            [Parameter(Mandatory = $true)][string]$ExePath,
            [Parameter(Mandatory = $true)][string]$LogPath
        )

        $ErrorActionPreference = 'Stop'

        function Write-UpdaterLog {
            param([string]$Message)
            $logDir = Split-Path -Parent $LogPath
            if ($logDir) {
                New-Item -ItemType Directory -Path $logDir -Force | Out-Null
            }
            Add-Content -LiteralPath $LogPath -Value "$(Get-Date -Format o) $Message"
        }

        try {
            Write-UpdaterLog "Waiting for KobraCache process $ProcessId to exit."
            Wait-Process -Id $ProcessId -Timeout 90 -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 750

            Write-UpdaterLog "Copying update from $SourceDir to $TargetDir."
            Get-ChildItem -LiteralPath $SourceDir -Force | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination $TargetDir -Recurse -Force
            }

            Write-UpdaterLog "Relaunching $ExePath."
            Start-Process -FilePath $ExePath -WorkingDirectory $TargetDir
            Write-UpdaterLog "Update completed."
        }
        catch {
            Write-UpdaterLog "Update failed: $($_.Exception.Message)"
            throw
        }
        """;

        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, script);
    }
}
