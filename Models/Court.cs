namespace RallyBoard.Models;

public class Court
{
    public int Id { get; init; }
    public string Name { get; set; } = "";

    /// <summary>Slots 0-1 are Team A (left of net), 2-3 are Team B (right of net).</summary>
    public Player?[] Slots { get; } = new Player?[4];

    public DateTime? StartedAt { get; set; }

    public bool IsFull => Slots.All(s => s is not null);
    public int FilledCount => Slots.Count(s => s is not null);
    public bool IsRunning => StartedAt is not null;
}
