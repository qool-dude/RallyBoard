using Microsoft.EntityFrameworkCore;
using RallyBoard.Data;
using RallyBoard.Models;

namespace RallyBoard.Services;

public class MatchmakingService
{
    private readonly IDbContextFactory<RallyBoardDbContext> _dbFactory;
    private readonly MatchmakingSettingsService _settings;
    private readonly Random _random = new();

    public MatchmakingService(
        IDbContextFactory<RallyBoardDbContext> dbFactory,
        MatchmakingSettingsService settings)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    public MatchmakingOptions Options => _settings.Current;

    public Dictionary<Guid, double> GetRatings(bool isTest)
    {
        return GetGlobalRatings(isTest).ToDictionary(s => s.PlayerId, s => s.Rating);
    }

    /// <summary>
    /// All-time ratings within Test or Live, with recency boost for games in the last N sessions.
    /// </summary>
    public List<PlayerMatchStats> GetGlobalRatings(bool isTest)
    {
        using var db = _dbFactory.CreateDbContext();

        var sessionOrder = db.Sessions
            .Where(s => s.IsTest == isTest)
            .OrderByDescending(s => s.Date)
            .ThenByDescending(s => s.StartedAt)
            .Select(s => s.Id)
            .ToList();

        var recencyRank = sessionOrder
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        var games = db.Games
            .Where(g => g.Session.IsTest == isTest)
            .Include(g => g.Players)
            .ThenInclude(gp => gp.Player)
            .AsNoTracking()
            .ToList();

        var stats = new Dictionary<Guid, (string Name, int RawGames, int RawWins, int RawLosses, int PointsFor, int PointsAgainst, double WGames, double WWins, double WCloseness)>();

        foreach (var game in games)
        {
            var rank = recencyRank.TryGetValue(game.SessionId, out var r) ? r : int.MaxValue;
            var weight = PlayerRatingCalculator.SessionRecencyWeight(rank, Options.Rating);
            var closeness = PlayerRatingCalculator.GameCloseness(game.TeamAScore, game.TeamBScore);

            foreach (var gp in game.Players)
            {
                if (!stats.TryGetValue(gp.PlayerId, out var row))
                    row = (gp.Player.Name, 0, 0, 0, 0, 0, 0, 0, 0);

                var won = game.WinnerSide == gp.TeamSide;
                var lost = game.WinnerSide is "TeamA" or "TeamB" && game.WinnerSide != gp.TeamSide;
                var pointsFor = gp.TeamSide == "TeamA" ? game.TeamAScore ?? 0 : game.TeamBScore ?? 0;
                var pointsAgainst = gp.TeamSide == "TeamA" ? game.TeamBScore ?? 0 : game.TeamAScore ?? 0;

                stats[gp.PlayerId] = (
                    row.Name,
                    row.RawGames + 1,
                    row.RawWins + (won ? 1 : 0),
                    row.RawLosses + (lost ? 1 : 0),
                    row.PointsFor + pointsFor,
                    row.PointsAgainst + pointsAgainst,
                    row.WGames + weight,
                    row.WWins + (won ? weight : 0),
                    row.WCloseness + closeness * weight);
            }
        }

        return stats.Select(kv =>
        {
            var (name, rawGames, rawWins, rawLosses, pf, pa, wGames, wWins, wClose) = kv.Value;
            var winRate = rawGames > 0 ? Math.Round(100.0 * rawWins / rawGames, 1) : 0;
            var closeness = wGames > 0 ? Math.Round(wClose / wGames, 1) : 50;
            var rating = PlayerRatingCalculator.ComputeRating(wGames, wWins, wClose, rawGames, Options.Rating);
            return new PlayerMatchStats(kv.Key, name, rawGames, rawWins, rawLosses, winRate, closeness, rating, pf, pa);
        }).ToList();
    }

    /// <summary>
    /// Fills a four-player lineup using either the Balanced or Ability algorithm.
    /// Existing fixed slots are preserved and only empty positions are selected.
    /// </summary>
    public MatchmakingPickResult? PickLineup(
        IReadOnlyList<Player> waiting,
        Guid sessionId,
        bool isTest,
        IReadOnlyList<Player?>? fixedSlots = null)
    {
        var preservedSlots = fixedSlots is { Count: 4 }
            ? fixedSlots.ToArray()
            : new Player?[4];
        var fixedPlayers = preservedSlots.OfType<Player>().DistinctBy(p => p.Id).ToArray();
        var slotsToFill = 4 - fixedPlayers.Length;

        if (slotsToFill <= 0 || waiting.Count < slotsToFill)
            return null;

        using var historyDb = _dbFactory.CreateDbContext();
        var playedPlayerIds = historyDb.GamePlayers
            .Where(gp => gp.Game.SessionId == sessionId)
            .Select(gp => gp.PlayerId)
            .Distinct()
            .ToHashSet();
        var firstGamePlayerIds = waiting
            .Where(p => !playedPlayerIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToHashSet();

        var ratings = GetRatings(isTest);
        var recentPairs = GetRecentPairCounts(sessionId, Options.Selection.RecentGamesLookback);
        var usedFoursomes = GetSessionFoursomeKeys(sessionId);
        var sel = Options.Selection;

        var useAbility = _random.NextDouble() < Math.Clamp(sel.AbilityAlgorithmChance, 0, 1);
        var algorithm = useAbility ? MatchmakingAlgorithms.Ability : MatchmakingAlgorithms.Balanced;
        var weights = useAbility ? sel.Ability : sel.Balanced;

        var wWait = Math.Max(0, weights.WaitingWeight);
        var wMix = Math.Max(0, weights.MixingWeight);
        var wBal = Math.Max(0, weights.BalanceWeight);
        var wPeer = Math.Max(0, weights.PeerWeight);
        var wHom = Math.Max(0, weights.HomogeneityWeight);
        var weightSum = wWait + wMix + wBal + wPeer + wHom;
        if (weightSum <= 0)
        {
            wWait = wMix = wBal = wPeer = 1;
            wHom = 0;
            weightSum = 4;
        }

        var defaultRating = Options.Rating.DefaultRating;
        double RatingOf(Player p) =>
            ratings.TryGetValue(p.Id, out var r) ? r : defaultRating;

        var fullPool = waiting.Concat(fixedPlayers).DistinctBy(p => p.Id).ToList();
        var poolRatings = fullPool.Select(RatingOf).OrderByDescending(r => r).ToList();
        var strongPoolSize = Math.Max(1, (int)Math.Ceiling(fullPool.Count * Math.Clamp(sel.TopPlayerPercentile, 0.05, 1)));
        var topThreshold = poolRatings[Math.Min(strongPoolSize - 1, poolRatings.Count - 1)];
        var topIds = fullPool.Where(p => RatingOf(p) >= topThreshold).Select(p => p.Id).ToHashSet();

        var maxWait = waiting.Max(p => p.GetWaitingElapsed().TotalSeconds);
        if (maxWait <= 0) maxWait = 1;

        var balanceBias = useAbility ? 0.85 : 0.6;
        var candidates = new List<(Player[] Slots, double Score, double Wait, double Mix, double Bal, double Peer, double Hom, bool IsRepeat)>();

        foreach (var selected in Combinations(waiting, slotsToFill))
        {
            var quartet = fixedPlayers.Concat(selected).ToArray();
            var waitScore = selected.Average(p => p.GetWaitingElapsed().TotalSeconds / maxWait) * 100;
            var peerScore = PeerQualityScore(quartet, RatingOf, topIds, sel.TopClusterBonus);
            var homScore = HomogeneityScore(quartet, RatingOf);
            var bestSplit = fixedPlayers.Length == 0
                ? BestTeamSplit(quartet, ratings, recentPairs, balanceBias)
                : BestTeamAssignment(selected, preservedSlots, ratings, recentPairs, balanceBias);
            var score =
                (waitScore * wWait
                 + bestSplit.MixScore * wMix
                 + bestSplit.BalanceScore * wBal
                 + peerScore * wPeer
                 + homScore * wHom) / weightSum;

            var isRepeat = usedFoursomes.Contains(FoursomeKey(quartet));
            candidates.Add((bestSplit.Slots, score, waitScore, bestSplit.MixScore, bestSplit.BalanceScore, peerScore, homScore, isRepeat));
        }

        if (candidates.Count == 0)
            return null;

        // Anyone who has not yet played this session gets priority for the
        // remaining empty positions.
        var requiredFirstGamePlayers = Math.Min(slotsToFill, firstGamePlayerIds.Count);
        var priorityCandidates = requiredFirstGamePlayers == 0
            ? candidates
            : candidates
                .Where(c => c.Slots.Count(p => firstGamePlayerIds.Contains(p.Id)) == requiredFirstGamePlayers)
                .ToList();

        // Prefer never-before-seen foursomes this session; only reuse if nothing else left
        var fresh = priorityCandidates.Where(c => !c.IsRepeat).ToList();
        var poolToUse = fresh.Count > 0 ? fresh : priorityCandidates;
        var forcedRepeat = fresh.Count == 0 && usedFoursomes.Count > 0;

        poolToUse.Sort((a, b) => b.Score.CompareTo(a.Score));

        var randomness = Math.Clamp(sel.Randomness, 0, 1);
        var usedRandomness = randomness > 0;
        int chosenIndex;
        if (!usedRandomness)
        {
            chosenIndex = 0;
        }
        else
        {
            var topCount = Math.Max(1, (int)Math.Ceiling(poolToUse.Count * Math.Max(randomness, 0.05)));
            topCount = Math.Min(topCount, poolToUse.Count);
            chosenIndex = _random.Next(topCount);
        }

        var chosen = poolToUse[chosenIndex];
        var overallRank = priorityCandidates
            .OrderByDescending(c => c.Score)
            .Select((c, i) => (c, Rank: i + 1))
            .First(x => FoursomeKey(x.c.Slots) == FoursomeKey(chosen.Slots))
            .Rank;

        var pool = waiting
            .OrderByDescending(p => p.GetWaitingElapsed())
            .Select(p => new MatchmakingPlayerSnapshot
            {
                PlayerId = p.Id,
                Name = p.Name,
                Rating = Math.Round(RatingOf(p), 1),
                WaitingSeconds = Math.Round(p.GetWaitingElapsed().TotalSeconds),
                IsTopPlayer = topIds.Contains(p.Id)
            })
            .ToList();

        var chosenSnapshots = chosen.Slots.Select((p, i) => new MatchmakingPlayerSnapshot
        {
            PlayerId = p.Id,
            Name = p.Name,
            Rating = Math.Round(RatingOf(p), 1),
            WaitingSeconds = Math.Round(p.GetWaitingElapsed().TotalSeconds),
            IsTopPlayer = topIds.Contains(p.Id),
            TeamSide = i < 2 ? "TeamA" : "TeamB"
        }).ToList();

        var alternatives = poolToUse
            .Take(Math.Min(4, poolToUse.Count))
            .Select((c, i) => new MatchmakingAlternative
            {
                Rank = i + 1,
                TotalScore = Math.Round(c.Score, 1),
                WaitingScore = Math.Round(c.Wait, 1),
                MixingScore = Math.Round(c.Mix, 1),
                BalanceScore = Math.Round(c.Bal, 1),
                PeerScore = Math.Round(c.Peer, 1),
                HomogeneityScore = Math.Round(c.Hom, 1),
                PlayerNames = c.Slots.Select(p => p.Name).ToList()
            })
            .ToList();

        var contributions = new Dictionary<string, double>
        {
            ["Waiting"] = chosen.Wait * wWait,
            ["Mixing"] = chosen.Mix * wMix,
            ["Balance"] = chosen.Bal * wBal,
            ["Peer"] = chosen.Peer * wPeer,
            ["Ability"] = chosen.Hom * wHom
        };
        var dominant = contributions.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).FirstOrDefault().Key
            ?? "Waiting";

        var decision = new MatchmakingDecision
        {
            WaitingPoolSize = waiting.Count,
            CandidatesConsidered = poolToUse.Count,
            RankAmongCandidates = chosenIndex + 1,
            UsedRandomness = usedRandomness && chosenIndex > 0,
            TotalScore = Math.Round(chosen.Score, 1),
            WaitingScore = Math.Round(chosen.Wait, 1),
            MixingScore = Math.Round(chosen.Mix, 1),
            BalanceScore = Math.Round(chosen.Bal, 1),
            PeerScore = Math.Round(chosen.Peer, 1),
            HomogeneityScore = Math.Round(chosen.Hom, 1),
            WaitingWeight = wWait,
            MixingWeight = wMix,
            BalanceWeight = wBal,
            PeerWeight = wPeer,
            HomogeneityWeight = wHom,
            Algorithm = algorithm,
            Pool = pool,
            Chosen = chosenSnapshots,
            Alternatives = alternatives,
            DominantFactor = dominant,
            Summary = BuildSummary(
                chosen, chosenIndex, poolToUse.Count, priorityCandidates.Count, overallRank,
                algorithm, dominant, usedRandomness, forcedRepeat, chosen.IsRepeat,
                chosenSnapshots, wWait, wMix, wBal, wPeer, wHom, weightSum)
        };

        return new MatchmakingPickResult
        {
            Slots = chosen.Slots,
            Decision = decision
        };
    }

    private static string BuildSummary(
        (Player[] Slots, double Score, double Wait, double Mix, double Bal, double Peer, double Hom, bool IsRepeat) chosen,
        int chosenIndex,
        int availableCount,
        int totalCount,
        int overallRank,
        string algorithm,
        string dominant,
        bool usedRandomness,
        bool forcedRepeat,
        bool isRepeat,
        List<MatchmakingPlayerSnapshot> snapshots,
        double wWait, double wMix, double wBal, double wPeer, double wHom, double weightSum)
    {
        var teamA = string.Join(" & ", snapshots.Where(s => s.TeamSide == "TeamA").Select(s => $"{s.Name} ({s.Rating})"));
        var teamB = string.Join(" & ", snapshots.Where(s => s.TeamSide == "TeamB").Select(s => $"{s.Name} ({s.Rating})"));
        var rankText = chosenIndex == 0
            ? "best-scoring available lineup"
            : $"#{chosenIndex + 1} of {availableCount} available (randomness among top candidates)";
        var contrib =
            $"Waiting {chosen.Wait:0.#}×{(wWait / weightSum):0.##}, " +
            $"Mixing {chosen.Mix:0.#}×{(wMix / weightSum):0.##}, " +
            $"Balance {chosen.Bal:0.#}×{(wBal / weightSum):0.##}, " +
            $"Peer {chosen.Peer:0.#}×{(wPeer / weightSum):0.##}, " +
            $"Homogeneity {chosen.Hom:0.#}×{(wHom / weightSum):0.##}";

        var repeatNote = forcedRepeat
            ? " Forced to reuse a foursome already played this session (no fresh combinations left)."
            : isRepeat
                ? " Note: this foursome already played earlier this session."
                : totalCount > availableCount
                    ? $" Excluded {totalCount - availableCount} foursome(s) already played this session."
                    : "";

        return $"Algorithm: {algorithm}. Picked {rankText} (overall rank #{overallRank}/{totalCount}). {teamA} vs {teamB}. " +
               $"Dominant factor: {dominant}. Factor contributions: {contrib}. " +
               $"Total score {chosen.Score:0.#} from {availableCount} available of {totalCount} possible foursomes" +
               (usedRandomness && chosenIndex > 0 ? " (not the absolute top due to randomness)." : ".") +
               repeatNote;
    }

    /// <summary>How tightly clustered the four players' ratings are (100 = identical).</summary>
    private static double HomogeneityScore(Player[] quartet, Func<Player, double> ratingOf)
    {
        var rs = quartet.Select(ratingOf).ToArray();
        var spread = rs.Max() - rs.Min();
        return Math.Clamp(100.0 * (1.0 - spread / 100.0), 0, 100);
    }

    /// <summary>
    /// Rewards games that put strong players with other strong players.
    /// </summary>
    private static double PeerQualityScore(
        Player[] quartet,
        Func<Player, double> ratingOf,
        HashSet<Guid> topIds,
        double topClusterBonus)
    {
        var rs = quartet.Select(ratingOf).OrderByDescending(r => r).ToArray();
        var avg = rs.Average();
        var top2Avg = (rs[0] + rs[1]) / 2.0;
        var score = 0.45 * avg + 0.55 * top2Avg;

        var strongInGame = quartet.Count(p => topIds.Contains(p.Id));
        if (strongInGame >= 2)
            score = Math.Min(100, score + topClusterBonus);
        else if (strongInGame == 1)
            score = Math.Max(0, score - topClusterBonus * 0.5);

        return Math.Clamp(score, 0, 100);
    }

    private (Player[] Slots, double MixScore, double BalanceScore) BestTeamSplit(
        Player[] quartet,
        Dictionary<Guid, double> ratings,
        Dictionary<(Guid, Guid), int> recentPairs,
        double balanceBias = 0.6)
    {
        // Three unique 2v2 partitions of four players
        int[][] splits =
        [
            [0, 1, 2, 3],
            [0, 2, 1, 3],
            [0, 3, 1, 2]
        ];

        var defaultRating = Options.Rating.DefaultRating;
        balanceBias = Math.Clamp(balanceBias, 0, 1);
        var mixBias = 1.0 - balanceBias;
        double bestScore = double.MinValue;
        Player[] bestSlots = [quartet[0], quartet[1], quartet[2], quartet[3]];
        double bestMix = 0, bestBal = 0;

        foreach (var split in splits)
        {
            var a0 = quartet[split[0]];
            var a1 = quartet[split[1]];
            var b0 = quartet[split[2]];
            var b1 = quartet[split[3]];

            var rA = RatingOf(a0) + RatingOf(a1);
            var rB = RatingOf(b0) + RatingOf(b1);
            var diff = Math.Abs(rA - rB);
            var balanceScore = Math.Clamp(100.0 * (1.0 - diff / 200.0), 0, 100);

            var pairHits =
                PairCount(a0.Id, a1.Id) + PairCount(b0.Id, b1.Id) +
                PairCount(a0.Id, b0.Id) + PairCount(a0.Id, b1.Id) +
                PairCount(a1.Id, b0.Id) + PairCount(a1.Id, b1.Id);

            var mixScore = Math.Clamp(100.0 * (1.0 - pairHits / (6.0 * Math.Max(1, Options.Selection.RecentGamesLookback))), 0, 100);

            var splitScore = balanceScore * balanceBias + mixScore * mixBias;
            if (splitScore > bestScore)
            {
                bestScore = splitScore;
                bestSlots = [a0, a1, b0, b1];
                bestMix = mixScore;
                bestBal = balanceScore;
            }
        }

        return (bestSlots, bestMix, bestBal);

        double RatingOf(Player p) =>
            ratings.TryGetValue(p.Id, out var r) ? r : defaultRating;

        int PairCount(Guid a, Guid b)
        {
            var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
            return recentPairs.TryGetValue(key, out var c) ? c : 0;
        }
    }

    private (Player[] Slots, double MixScore, double BalanceScore) BestTeamAssignment(
        Player[] selected,
        Player?[] fixedSlots,
        Dictionary<Guid, double> ratings,
        Dictionary<(Guid, Guid), int> recentPairs,
        double balanceBias)
    {
        var emptyIndices = fixedSlots
            .Select((player, index) => (player, index))
            .Where(x => x.player is null)
            .Select(x => x.index)
            .ToArray();
        var defaultRating = Options.Rating.DefaultRating;
        balanceBias = Math.Clamp(balanceBias, 0, 1);
        var mixBias = 1.0 - balanceBias;
        var bestScore = double.MinValue;
        Player[]? bestSlots = null;
        double bestMix = 0, bestBalance = 0;

        foreach (var permutation in Permutations(selected))
        {
            var slots = fixedSlots.ToArray();
            for (var i = 0; i < emptyIndices.Length; i++)
                slots[emptyIndices[i]] = permutation[i];

            var complete = slots.Select(p => p!).ToArray();
            var rA = RatingOf(complete[0]) + RatingOf(complete[1]);
            var rB = RatingOf(complete[2]) + RatingOf(complete[3]);
            var diff = Math.Abs(rA - rB);
            var balanceScore = Math.Clamp(100.0 * (1.0 - diff / 200.0), 0, 100);

            var pairHits =
                PairCount(complete[0].Id, complete[1].Id) +
                PairCount(complete[2].Id, complete[3].Id) +
                PairCount(complete[0].Id, complete[2].Id) +
                PairCount(complete[0].Id, complete[3].Id) +
                PairCount(complete[1].Id, complete[2].Id) +
                PairCount(complete[1].Id, complete[3].Id);
            var mixScore = Math.Clamp(
                100.0 * (1.0 - pairHits / (6.0 * Math.Max(1, Options.Selection.RecentGamesLookback))),
                0,
                100);

            var assignmentScore = balanceScore * balanceBias + mixScore * mixBias;
            if (assignmentScore <= bestScore) continue;

            bestScore = assignmentScore;
            bestSlots = complete;
            bestMix = mixScore;
            bestBalance = balanceScore;
        }

        return (bestSlots ?? fixedSlots.Select(p => p!).ToArray(), bestMix, bestBalance);

        double RatingOf(Player p) =>
            ratings.TryGetValue(p.Id, out var rating) ? rating : defaultRating;

        int PairCount(Guid a, Guid b)
        {
            var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
            return recentPairs.TryGetValue(key, out var count) ? count : 0;
        }
    }

    private HashSet<string> GetSessionFoursomeKeys(Guid sessionId)
    {
        using var db = _dbFactory.CreateDbContext();
        var games = db.Games
            .Where(g => g.SessionId == sessionId)
            .Include(g => g.Players)
            .AsNoTracking()
            .ToList();

        return games
            .Select(g => FoursomeKey(g.Players.Select(p => p.PlayerId)))
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet();
    }

    private static string FoursomeKey(IEnumerable<Player> players) =>
        FoursomeKey(players.Select(p => p.Id));

    private static string FoursomeKey(IEnumerable<Guid> playerIds)
    {
        var ids = playerIds.OrderBy(id => id).ToList();
        return ids.Count == 4 ? string.Join("|", ids) : "";
    }

    private Dictionary<(Guid, Guid), int> GetRecentPairCounts(Guid sessionId, int lookback)
    {
        using var db = _dbFactory.CreateDbContext();
        var games = db.Games
            .Where(g => g.SessionId == sessionId)
            .Include(g => g.Players)
            .OrderByDescending(g => g.EndedAt)
            .Take(Math.Max(1, lookback))
            .AsNoTracking()
            .ToList();

        var counts = new Dictionary<(Guid, Guid), int>();
        foreach (var game in games)
        {
            var teamA = game.Players.Where(p => p.TeamSide == "TeamA").Select(p => p.PlayerId).ToList();
            var teamB = game.Players.Where(p => p.TeamSide == "TeamB").Select(p => p.PlayerId).ToList();
            AddPairs(teamA);
            AddPairs(teamB);
            foreach (var a in teamA)
            foreach (var b in teamB)
                Increment(a, b);
        }

        return counts;

        void AddPairs(List<Guid> team)
        {
            for (var i = 0; i < team.Count; i++)
            for (var j = i + 1; j < team.Count; j++)
                Increment(team[i], team[j]);
        }

        void Increment(Guid a, Guid b)
        {
            var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
        }
    }

    private static IEnumerable<Player[]> Permutations(IReadOnlyList<Player> players)
    {
        if (players.Count == 0)
        {
            yield return Array.Empty<Player>();
            yield break;
        }

        for (var i = 0; i < players.Count; i++)
        {
            var remaining = players.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(remaining))
                yield return new[] { players[i] }.Concat(tail).ToArray();
        }
    }

    private static IEnumerable<Player[]> Combinations(IReadOnlyList<Player> players, int k)
    {
        var n = players.Count;
        var indices = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return indices.Select(i => players[i]).ToArray();

            var i = k - 1;
            while (i >= 0 && indices[i] == n - k + i)
                i--;
            if (i < 0)
                yield break;
            indices[i]++;
            for (var j = i + 1; j < k; j++)
                indices[j] = indices[j - 1] + 1;
        }
    }
}
