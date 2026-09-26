namespace Dusage;

/// <summary>A usage source plus the widget's bookkeeping for it.</summary>
sealed class Provider(IUsageSource source)
{
    public IUsageSource Source { get; } = source;
    public string Key => Source.Key;

    public ProviderState State { get; set; } = new();
    public bool SignedIn { get; set; }

    public DateTimeOffset NextFetch { get; set; }
    public DateTimeOffset LastAttempt { get; set; }
    public DateTimeOffset HoldUntil { get; set; }
    public bool Busy { get; set; }

    public List<ExtraLimit> Extras => State.Last?.Extra is { } extras ? extras.Where(e => e.Key is not null).ToList() : [];

    public string ExtraKey(ExtraLimit extra) => $"{Key}:{extra.Key}";
}
