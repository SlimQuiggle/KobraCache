namespace KobraCache.Core.Models;

public sealed record PrinterCacheFile
{
    public required string PrinterKey { get; init; }
    public required StorageTarget StorageTarget { get; init; }
    public string Path { get; init; } = "/";
    public required string FileName { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public bool IsCurrentJob { get; init; }
    public string? RemoteId { get; init; }

    public DateTimeOffset? BestDate => ModifiedAt ?? CreatedAt;
}
