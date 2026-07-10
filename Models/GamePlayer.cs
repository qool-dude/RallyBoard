namespace RallyBoard.Models;

public class GamePlayer
{
    public int Id { get; set; }
    public Guid GameId { get; set; }
    public Game Game { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;
    public string TeamSide { get; set; } = "";
    public int SlotIndex { get; set; }
}
