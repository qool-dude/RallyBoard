namespace RallyBoard.Models;

public class Court
{
    public int Id { get; init; }
    public string Name { get; set; } = "";

    /// <summary>Slots 0-1 are Team A (left of net), 2-3 are Team B (right of net).</summary>
    public Player?[] Slots { get; } = new Player?[4];

    public DateTime? StartedAt { get; set; }
    public DateTime? PausedAt { get; set; }
    public TimeSpan AccumulatedTime { get; set; }

    // Game end state
    public string? Winner { get; set; } // "TeamA", "TeamB", or "Tie"
    public int TeamAScore { get; set; }
    public int TeamBScore { get; set; }

    public bool IsFull => Slots.All(s => s is not null);
    public int FilledCount => Slots.Count(s => s is not null);
    public bool IsRunning => StartedAt is not null && PausedAt is null;
    public bool IsPaused => StartedAt is not null && PausedAt is not null;
    public bool GameEnded => Winner is not null;

    /// <summary>
    /// Gets the elapsed time on the court timer.
    /// </summary>
    public TimeSpan GetElapsedTime()
    {
        if (StartedAt is null) return TimeSpan.Zero;

        var endTime = PausedAt ?? DateTime.UtcNow;
        var elapsed = (endTime - StartedAt.Value) + AccumulatedTime;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>
    /// Resets the court to initial state (clears players, timer, and game state).
    /// </summary>
    public void Reset()
    {
        Array.Clear(Slots, 0, Slots.Length);
        StartedAt = null;
        PausedAt = null;
        AccumulatedTime = TimeSpan.Zero;
        Winner = null;
        TeamAScore = 0;
        TeamBScore = 0;
    }
}
