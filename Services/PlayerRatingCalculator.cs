using RallyBoard.Models;

namespace RallyBoard.Services;

public static class PlayerRatingCalculator
{
    public static double GameCloseness(int? teamAScore, int? teamBScore)
    {
        if (teamAScore is null || teamBScore is null)
            return 50;

        var total = teamAScore.Value + teamBScore.Value;
        if (total <= 0)
            return 50;

        var margin = Math.Abs(teamAScore.Value - teamBScore.Value);
        return Math.Clamp(100.0 * (1.0 - (double)margin / total), 0, 100);
    }

    /// <summary>
    /// Session weight for a game: recent sessions (by recency rank 0..window-1) get the multiplier.
    /// </summary>
    public static double SessionRecencyWeight(int sessionRecencyRank, RatingWeights weights)
    {
        var window = Math.Max(1, weights.RecentSessionsWindow);
        if (sessionRecencyRank >= 0 && sessionRecencyRank < window)
            return Math.Max(0.01, weights.RecentSessionMultiplier);
        return 1.0;
    }

    public static double ComputeRating(
        double weightedGames,
        double weightedWins,
        double weightedClosenessSum,
        int rawGamesPlayed,
        RatingWeights weights)
    {
        if (weightedGames <= 0 || rawGamesPlayed <= 0)
            return weights.DefaultRating;

        var winRate = 100.0 * weightedWins / weightedGames;
        var avgCloseness = weightedClosenessSum / weightedGames;

        var wWin = Math.Max(0, weights.WinRateWeight);
        var wGames = Math.Max(0, weights.GamesPlayedWeight);
        var wClose = Math.Max(0, weights.ClosenessWeight);
        var sum = wWin + wGames + wClose;
        if (sum <= 0)
            return weights.DefaultRating;

        var cap = Math.Max(1, weights.GamesPlayedCap);
        var gamesScore = Math.Min(100.0, 100.0 * rawGamesPlayed / cap);
        var closeness = Math.Clamp(avgCloseness, 0, 100);
        var win = Math.Clamp(winRate, 0, 100);

        return Math.Round((win * wWin + gamesScore * wGames + closeness * wClose) / sum, 1);
    }
}
