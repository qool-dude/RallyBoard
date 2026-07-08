namespace RallyBoard.Models;

public class Player
{
    private static int _seed;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int ColorIndex { get; init; } = _seed++;

    // Waiting pool metadata
    public DateTime? WaitingSince { get; set; }
    public bool IsPaused { get; set; }
    public DateTime? PausedAt { get; set; }
    public TimeSpan PausedAccumulated { get; set; }

    public string Initials =>
        string.Join("", Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(w => char.ToUpper(w[0])));

    public TimeSpan GetWaitingElapsed()
    {
        if (WaitingSince is null) return TimeSpan.Zero;
        var nowOrPaused = IsPaused && PausedAt is not null ? PausedAt.Value : DateTime.UtcNow;
        var elapsed = (nowOrPaused - WaitingSince.Value) - PausedAccumulated;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }
}
