namespace RallyBoard.Models;

public class SessionAttendance
{
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public Player Player { get; set; } = null!;
    public DateTime CheckedInAt { get; set; }
    public bool HasPaid { get; set; }
}
