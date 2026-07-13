namespace RallyBoard.Models;

public class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Password required to enter admin mode.</summary>
    public string Password { get; set; } = "stanway123";
}
