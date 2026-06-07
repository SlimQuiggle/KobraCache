namespace KobraCache.Desktop;

public sealed record AppSettings
{
    public List<string> ManualIpAddresses { get; init; } = [];
    public int StorageTargetDefaultsVersion { get; init; }
    public bool IncludeLocalCache { get; init; } = true;
    public bool IncludeUsb { get; init; }
    public bool IncludeCloud { get; init; }
}
