namespace RallyBoard.Models;

public class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Password required to enter admin mode.</summary>
    public string Password { get; set; } = "stanway123";

    /// <summary>Minutes of inactivity before admin mode expires.</summary>
    public int InactivityMinutes { get; set; } = 30;
}
