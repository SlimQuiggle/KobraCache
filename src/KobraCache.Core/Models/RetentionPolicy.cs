namespace KobraCache.Core.Models;

public sealed record RetentionPolicy
{
    public RetentionPreset Preset { get; init; } = RetentionPreset.Days30;
    public DateOnly? CustomCutoffDate { get; init; }
    public bool IncludeUndatedManualSelections { get; init; }

    public DateTimeOffset GetCutoff(DateTimeOffset now)
    {
        return Preset switch
        {
            RetentionPreset.Days30 => now.AddDays(-30),
            RetentionPreset.Days60 => now.AddDays(-60),
            RetentionPreset.Days90 => now.AddDays(-90),
            RetentionPreset.CustomDate when CustomCutoffDate is { } date =>
                new DateTimeOffset(date.ToDateTime(TimeOnly.MaxValue), now.Offset),
            _ => now.AddDays(-30)
        };
    }
}
