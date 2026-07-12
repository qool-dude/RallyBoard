namespace RallyBoard.Models;

/// <summary>In-memory result from matchmaking when a lineup is picked.</summary>
public class MatchmakingPickResult
{
    public Player[] Slots { get; init; } = Array.Empty<Player>();
    public MatchmakingDecision Decision { get; init; } = new();
}

public class MatchmakingDecision
{
    public DateTime PickedAt { get; set; } = DateTime.UtcNow;
    public int WaitingPoolSize { get; set; }
    public int CandidatesConsidered { get; set; }
    public int RankAmongCandidates { get; set; }
    public bool UsedRandomness { get; set; }

    public double TotalScore { get; set; }
    public double WaitingScore { get; set; }
    public double MixingScore { get; set; }
    public double BalanceScore { get; set; }
    public double PeerScore { get; set; }

    public double WaitingWeight { get; set; }
    public double MixingWeight { get; set; }
    public double BalanceWeight { get; set; }
    public double PeerWeight { get; set; }

    public double HomogeneityScore { get; set; }
    public double HomogeneityWeight { get; set; }

    public string Algorithm { get; set; } = "";
    public string DominantFactor { get; set; } = "";
    public string Summary { get; set; } = "";

    public List<MatchmakingPlayerSnapshot> Pool { get; set; } = new();
    public List<MatchmakingPlayerSnapshot> Chosen { get; set; } = new();
    public List<MatchmakingAlternative> Alternatives { get; set; } = new();
}

public class MatchmakingPlayerSnapshot
{
    public Guid PlayerId { get; set; }
    public string Name { get; set; } = "";
    public double Rating { get; set; }
    public double WaitingSeconds { get; set; }
    public bool IsTopPlayer { get; set; }
    public string TeamSide { get; set; } = "";
}

public class MatchmakingAlternative
{
    public int Rank { get; set; }
    public double TotalScore { get; set; }
    public double WaitingScore { get; set; }
    public double MixingScore { get; set; }
    public double BalanceScore { get; set; }
    public double PeerScore { get; set; }
    public double HomogeneityScore { get; set; }

    public List<string> PlayerNames { get; set; } = new();
}

/// <summary>Persisted matchmaking explanation linked to a completed (or pending) game.</summary>
public class MatchmakingExplanation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public Guid? GameId { get; set; }
    public Game? Game { get; set; }
    public int CourtId { get; set; }
    public DateTime PickedAt { get; set; }

    public int WaitingPoolSize { get; set; }
    public int CandidatesConsidered { get; set; }
    public int RankAmongCandidates { get; set; }
    public bool UsedRandomness { get; set; }

    public double TotalScore { get; set; }
    public double WaitingScore { get; set; }
    public double MixingScore { get; set; }
    public double BalanceScore { get; set; }
    public double PeerScore { get; set; }

    public double WaitingWeight { get; set; }
    public double MixingWeight { get; set; }
    public double BalanceWeight { get; set; }
    public double PeerWeight { get; set; }

    public double HomogeneityScore { get; set; }
    public double HomogeneityWeight { get; set; }

    public string Algorithm { get; set; } = "";
    public string DominantFactor { get; set; } = "";
    public string Summary { get; set; } = "";

    /// <summary>JSON: pool snapshots, chosen players, alternatives.</summary>
    public string DetailsJson { get; set; } = "{}";
}

public record MatchmakingExplanationRow(
    Guid Id,
    Guid? GameId,
    int CourtId,
    DateTime PickedAt,
    DateTime? GameEndedAt,
    string TeamANames,
    string TeamBNames,
    int? TeamAScore,
    int? TeamBScore,
    string WinnerSide,
    double TotalScore,
    double WaitingScore,
    double MixingScore,
    double BalanceScore,
    double PeerScore,
    double HomogeneityScore,
    double WaitingWeight,
    double MixingWeight,
    double BalanceWeight,
    double PeerWeight,
    double HomogeneityWeight,
    string Algorithm,
    string DominantFactor,
    string Summary,
    int WaitingPoolSize,
    int CandidatesConsidered,
    int RankAmongCandidates,
    bool UsedRandomness,
    List<MatchmakingPlayerSnapshot> Pool,
    List<MatchmakingPlayerSnapshot> Chosen,
    List<MatchmakingAlternative> Alternatives);
