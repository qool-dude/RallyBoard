namespace RallyBoard.Models;

public class Assignment
{
    public int Id { get; set; }
    public int CourtId { get; set; }
    public int SlotIndex { get; set; }
    public Guid PlayerId { get; set; }
}
