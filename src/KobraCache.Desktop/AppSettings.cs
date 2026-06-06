using KobraCache.Core.Models;

namespace KobraCache.Desktop;

public sealed record AppSettings
{
    public List<string> ManualIpAddresses { get; init; } = [];
    public RetentionPreset RetentionPreset { get; init; } = RetentionPreset.Days30;
    public DateOnly? CustomCutoffDate { get; init; }
    public bool IncludeLocalCache { get; init; } = true;
    public bool IncludeUsb { get; init; } = true;
    public bool IncludeCloud { get; init; } = true;
}
