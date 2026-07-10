namespace RallyBoard.Models;

public class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public ICollection<SessionAttendance> Attendances { get; set; } = new List<SessionAttendance>();
    public ICollection<Game> Games { get; set; } = new List<Game>();
}
