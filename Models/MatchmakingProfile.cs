namespace RallyBoard.Models;

public class MatchmakingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public MatchmakingOptions Options { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
