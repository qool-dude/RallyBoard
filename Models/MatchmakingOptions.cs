namespace RallyBoard.Models;

public class MatchmakingOptions
{
    public const string SectionName = "Matchmaking";

    public RatingWeights Rating { get; set; } = new();
    public SelectionWeights Selection { get; set; } = new();
}

/// <summary>
/// Weights for the 0–100 player rating. Relative weights are normalised.
/// </summary>
public class RatingWeights
{
    /// <summary>How much win % contributes to rating.</summary>
    public double WinRateWeight { get; set; } = 0.50;

    /// <summary>How much games played (experience) contributes.</summary>
    public double GamesPlayedWeight { get; set; } = 0.20;

    /// <summary>How much average game closeness contributes (tighter scores = higher).</summary>
    public double ClosenessWeight { get; set; } = 0.30;

    /// <summary>Games needed to reach full experience score (100).</summary>
    public int GamesPlayedCap { get; set; } = 20;

    /// <summary>Default rating when a player has no games yet.</summary>
    public double DefaultRating { get; set; } = 50;

    /// <summary>How many most-recent sessions get boosted weight in the rating.</summary>
    public int RecentSessionsWindow { get; set; } = 5;

    /// <summary>
    /// Multiplier for games in the recent sessions window (older games stay at 1.0).
    /// Higher = faster rating movement when form improves.
    /// </summary>
    public double RecentSessionMultiplier { get; set; } = 3.0;
}

/// <summary>
/// Shared pick settings plus two algorithm weight profiles.
/// </summary>
public class SelectionWeights
{
    /// <summary>
    /// Probability of using the Ability algorithm (0–1). Remainder uses Balanced.
    /// Default 0.5 = coin flip each pick.
    /// </summary>
    public double AbilityAlgorithmChance { get; set; } = 0.5;

    /// <summary>
    /// Top fraction of the waiting pool (by rating) treated as "strong" for peer matching.
    /// </summary>
    public double TopPlayerPercentile { get; set; } = 0.35;

    /// <summary>Extra score when a game includes 2+ strong players.</summary>
    public double TopClusterBonus { get; set; } = 15;

    /// <summary>How many recent games to consider for mixing penalties.</summary>
    public int RecentGamesLookback { get; set; } = 8;

    /// <summary>
    /// 0 = always pick the best lineup. Higher samples among top candidates.
    /// Same foursome already played this session is excluded unless no fresh options remain.
    /// </summary>
    public double Randomness { get; set; } = 0;

    /// <summary>Current default: waiting / mixing / balance / peer blend.</summary>
    public AlgorithmWeights Balanced { get; set; } = new()
    {
        WaitingWeight = 0.35,
        MixingWeight = 0.20,
        BalanceWeight = 0.25,
        PeerWeight = 0.20,
        HomogeneityWeight = 0
    };

    /// <summary>Emphasises similar-ability foursomes and even team ratings.</summary>
    public AlgorithmWeights Ability { get; set; } = new()
    {
        WaitingWeight = 0.10,
        MixingWeight = 0.10,
        BalanceWeight = 0.35,
        PeerWeight = 0.15,
        HomogeneityWeight = 0.30
    };
}

public class AlgorithmWeights
{
    public double WaitingWeight { get; set; }
    public double MixingWeight { get; set; }
    public double BalanceWeight { get; set; }
    public double PeerWeight { get; set; }

    /// <summary>
    /// Prefer foursomes where the four players have similar ratings (tight skill band).
    /// </summary>
    public double HomogeneityWeight { get; set; }
}

public static class MatchmakingAlgorithms
{
    public const string Balanced = "Balanced";
    public const string Ability = "Ability";
    public const string Manual = "Manual";
}
