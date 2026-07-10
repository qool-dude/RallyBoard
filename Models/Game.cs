namespace RallyBoard.Models;

public class Game
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public int CourtId { get; set; }
    public DateTime EndedAt { get; set; }
    public string WinnerSide { get; set; } = "";
    public int? TeamAScore { get; set; }
    public int? TeamBScore { get; set; }
    public int? DurationSeconds { get; set; }

    public ICollection<GamePlayer> Players { get; set; } = new List<GamePlayer>();
}
