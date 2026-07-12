using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RallyBoard.Data;
using RallyBoard.Models;

namespace RallyBoard.Services;

public class SessionService
{
    private readonly IDbContextFactory<RallyBoardDbContext> _dbFactory;
    private readonly MatchmakingOptions _matchmaking;
    private readonly MatchmakingService _matchmakingService;
    private Guid? _currentSessionId;

    public event Action? OnChange;
    public event Action? SessionStarted;
    public event Action? ModeChanged;

    /// <summary>
    /// When true, courts/dashboard use test sessions &amp; players only.
    /// Defaults to Test because existing data is marked IsTest.
    /// </summary>
    public bool IsTestMode { get; private set; } = true;

    public SessionService(
        IDbContextFactory<RallyBoardDbContext> dbFactory,
        IOptions<MatchmakingOptions> matchmaking,
        MatchmakingService matchmakingService)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _matchmaking = matchmaking?.Value ?? new MatchmakingOptions();
        _matchmakingService = matchmakingService ?? throw new ArgumentNullException(nameof(matchmakingService));
        EnsureSchema();
        _currentSessionId = GetOrCreateCurrentSessionId();
    }

    public Guid CurrentSessionId => _currentSessionId ??= GetOrCreateCurrentSessionId();

    public void SetTestMode(bool isTest)
    {
        if (IsTestMode == isTest) return;
        IsTestMode = isTest;
        _currentSessionId = GetOrCreateCurrentSessionId();
        ModeChanged?.Invoke();
        OnChange?.Invoke();
    }

    public void EnsureSchema()
    {
        using var db = _dbFactory.CreateDbContext();
        DatabaseInitializer.EnsureSchema(db);
    }

    public Session GetCurrentSession()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Sessions.Find(CurrentSessionId)
            ?? throw new InvalidOperationException("Current session not found.");
    }

    public Guid GetOrCreateCurrentSessionId()
    {
        using var db = _dbFactory.CreateDbContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var active = db.Sessions
            .Where(s => s.EndedAt == null && s.IsTest == IsTestMode)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();

        if (active is not null)
        {
            if (active.Date < today)
            {
                active.EndedAt = DateTime.UtcNow;
                db.SaveChanges();
            }
            else
            {
                return active.Id;
            }
        }

        return CreateSession(db, DefaultSessionName(today), IsTestMode).Id;
    }

    public void EndCurrentSession(string nextSessionName)
    {
        using var db = _dbFactory.CreateDbContext();
        var session = db.Sessions.Find(CurrentSessionId);
        if (session is not null && session.EndedAt is null)
        {
            session.EndedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        var name = string.IsNullOrWhiteSpace(nextSessionName)
            ? DefaultSessionName(DateOnly.FromDateTime(DateTime.UtcNow))
            : nextSessionName.Trim();

        var newSession = CreateSession(db, name, IsTestMode);
        _currentSessionId = newSession.Id;

        SessionStarted?.Invoke();
        OnChange?.Invoke();
    }

    public void UpdateSessionName(Guid sessionId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        using var db = _dbFactory.CreateDbContext();
        var session = db.Sessions.Find(sessionId);
        if (session is null) return;

        session.Name = name.Trim();
        db.SaveChanges();
        OnChange?.Invoke();
    }

    public void RecordAttendance(Guid playerId)
    {
        var sessionId = CurrentSessionId;
        using var db = _dbFactory.CreateDbContext();

        if (db.SessionAttendances.Any(a => a.SessionId == sessionId && a.PlayerId == playerId))
            return;

        db.SessionAttendances.Add(new SessionAttendance
        {
            SessionId = sessionId,
            PlayerId = playerId,
            CheckedInAt = DateTime.UtcNow
        });
        db.SaveChanges();
        OnChange?.Invoke();
    }

    public void RecordAttendance(IEnumerable<Guid> playerIds)
    {
        foreach (var id in playerIds)
            RecordAttendance(id);
    }

    public void RecordGame(
        int courtId,
        string winnerSide,
        int? teamAScore,
        int? teamBScore,
        TimeSpan duration,
        IReadOnlyList<(Player Player, int SlotIndex)> players,
        MatchmakingDecision? matchmaking = null)
    {
        if (players.Count == 0)
            return;

        var sessionId = CurrentSessionId;
        using var db = _dbFactory.CreateDbContext();

        foreach (var (player, _) in players)
            EnsureAttendance(db, sessionId, player.Id);

        var game = new Game
        {
            SessionId = sessionId,
            CourtId = courtId,
            EndedAt = DateTime.UtcNow,
            WinnerSide = winnerSide,
            TeamAScore = teamAScore,
            TeamBScore = teamBScore,
            DurationSeconds = (int)Math.Round(duration.TotalSeconds)
        };

        foreach (var (player, slotIndex) in players)
        {
            game.Players.Add(new GamePlayer
            {
                PlayerId = player.Id,
                TeamSide = slotIndex < 2 ? "TeamA" : "TeamB",
                SlotIndex = slotIndex
            });
        }

        db.Games.Add(game);

        if (matchmaking is not null)
        {
            var details = System.Text.Json.JsonSerializer.Serialize(new
            {
                matchmaking.Pool,
                matchmaking.Chosen,
                matchmaking.Alternatives
            });

            db.MatchmakingExplanations.Add(new MatchmakingExplanation
            {
                SessionId = sessionId,
                Game = game,
                CourtId = courtId,
                PickedAt = matchmaking.PickedAt,
                WaitingPoolSize = matchmaking.WaitingPoolSize,
                CandidatesConsidered = matchmaking.CandidatesConsidered,
                RankAmongCandidates = matchmaking.RankAmongCandidates,
                UsedRandomness = matchmaking.UsedRandomness,
                TotalScore = matchmaking.TotalScore,
                WaitingScore = matchmaking.WaitingScore,
                MixingScore = matchmaking.MixingScore,
                BalanceScore = matchmaking.BalanceScore,
                PeerScore = matchmaking.PeerScore,
                HomogeneityScore = matchmaking.HomogeneityScore,
                WaitingWeight = matchmaking.WaitingWeight,
                MixingWeight = matchmaking.MixingWeight,
                BalanceWeight = matchmaking.BalanceWeight,
                PeerWeight = matchmaking.PeerWeight,
                HomogeneityWeight = matchmaking.HomogeneityWeight,
                Algorithm = matchmaking.Algorithm,
                DominantFactor = matchmaking.DominantFactor,
                Summary = matchmaking.Summary,
                DetailsJson = details
            });
        }

        db.SaveChanges();
        OnChange?.Invoke();
    }

    public void SetHasPaid(Guid sessionId, Guid playerId, bool hasPaid)
    {
        using var db = _dbFactory.CreateDbContext();
        var attendance = db.SessionAttendances
            .FirstOrDefault(a => a.SessionId == sessionId && a.PlayerId == playerId);

        if (attendance is null)
        {
            attendance = new SessionAttendance
            {
                SessionId = sessionId,
                PlayerId = playerId,
                CheckedInAt = DateTime.UtcNow,
                HasPaid = hasPaid
            };
            db.SessionAttendances.Add(attendance);
        }
        else
        {
            attendance.HasPaid = hasPaid;
        }

        db.SaveChanges();
        OnChange?.Invoke();
    }

    public void ToggleAttendance(Guid sessionId, Guid playerId, bool present)
    {
        using var db = _dbFactory.CreateDbContext();
        var attendance = db.SessionAttendances
            .FirstOrDefault(a => a.SessionId == sessionId && a.PlayerId == playerId);

        if (present)
        {
            if (attendance is null)
            {
                db.SessionAttendances.Add(new SessionAttendance
                {
                    SessionId = sessionId,
                    PlayerId = playerId,
                    CheckedInAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }
        }
        else if (attendance is not null)
        {
            db.SessionAttendances.Remove(attendance);
            db.SaveChanges();
        }

        OnChange?.Invoke();
    }

    public List<SessionSummaryRow> GetSessions()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Sessions
            .Where(s => s.IsTest == IsTestMode)
            .OrderByDescending(s => s.Date)
            .ThenByDescending(s => s.StartedAt)
            .Select(s => new SessionSummaryRow(
                s.Id,
                s.Date,
                s.Name,
                s.Games.Count,
                s.Attendances.Count,
                s.EndedAt == null,
                s.IsTest))
            .ToList();
    }

    public SessionStats GetSessionStats(Guid? sessionId)
    {
        using var db = _dbFactory.CreateDbContext();
        var attendances = sessionId is null
            ? db.SessionAttendances.Where(a => a.Session.IsTest == IsTestMode)
            : db.SessionAttendances.Where(a => a.SessionId == sessionId);
        var paid = attendances.Count(a => a.HasPaid);
        var total = attendances.Select(a => a.PlayerId).Distinct().Count();
        var games = sessionId is null
            ? db.Games.Count(g => g.Session.IsTest == IsTestMode)
            : db.Games.Count(g => g.SessionId == sessionId);

        return new SessionStats(games, total, paid, Math.Max(0, attendances.Count() - paid));
    }

    public List<PlayerRankingRow> GetRankings(Guid? sessionId)
    {
        using var db = _dbFactory.CreateDbContext();

        var gamesQuery = db.Games.AsQueryable();
        if (sessionId is not null)
            gamesQuery = gamesQuery.Where(g => g.SessionId == sessionId);
        else
            gamesQuery = gamesQuery.Where(g => g.Session.IsTest == IsTestMode);

        var games = gamesQuery
            .Include(g => g.Players)
            .ThenInclude(gp => gp.Player)
            .AsNoTracking()
            .ToList();

        var attendanceQuery = db.SessionAttendances.AsQueryable();
        if (sessionId is not null)
            attendanceQuery = attendanceQuery.Where(a => a.SessionId == sessionId);
        else
            attendanceQuery = attendanceQuery.Where(a => a.Session.IsTest == IsTestMode);

        var attendances = attendanceQuery
            .Include(a => a.Player)
            .AsNoTracking()
            .ToList();

        var paidByPlayer = attendances
            .GroupBy(a => a.PlayerId)
            .ToDictionary(g => g.Key, g => g.Any(a => a.HasPaid));

        // Rating is always all-sessions (within mode) with recency weighting
        var globalRatings = _matchmakingService.GetGlobalRatings(IsTestMode)
            .ToDictionary(r => r.PlayerId);

        var stats = new Dictionary<Guid, (string Name, int Games, int Wins, int Losses, int PointsFor, int PointsAgainst, double ClosenessSum)>();

        foreach (var attendance in attendances.GroupBy(a => a.PlayerId).Select(g => g.First()))
        {
            stats[attendance.PlayerId] = (attendance.Player.Name, 0, 0, 0, 0, 0, 0);
        }

        foreach (var game in games)
        {
            var closeness = PlayerRatingCalculator.GameCloseness(game.TeamAScore, game.TeamBScore);
            foreach (var gp in game.Players)
            {
                if (!stats.TryGetValue(gp.PlayerId, out var row))
                    row = (gp.Player.Name, 0, 0, 0, 0, 0, 0);

                var won = game.WinnerSide == gp.TeamSide;
                var lost = game.WinnerSide is "TeamA" or "TeamB" && game.WinnerSide != gp.TeamSide;

                var pointsFor = gp.TeamSide == "TeamA" ? game.TeamAScore ?? 0 : game.TeamBScore ?? 0;
                var pointsAgainst = gp.TeamSide == "TeamA" ? game.TeamBScore ?? 0 : game.TeamAScore ?? 0;

                stats[gp.PlayerId] = (
                    row.Name,
                    row.Games + 1,
                    row.Wins + (won ? 1 : 0),
                    row.Losses + (lost ? 1 : 0),
                    row.PointsFor + pointsFor,
                    row.PointsAgainst + pointsAgainst,
                    row.ClosenessSum + closeness);
            }
        }

        // Include anyone with a global rating even if not in this session's attendance/games list
        // (only when viewing all sessions — for a single session keep attendees + players who played)
        if (sessionId is null)
        {
            foreach (var g in globalRatings.Values)
            {
                if (!stats.ContainsKey(g.PlayerId))
                    stats[g.PlayerId] = (g.Name, g.Games, g.Wins, g.Losses, g.PointsFor, g.PointsAgainst, g.Closeness * Math.Max(1, g.Games));
            }
        }

        return stats
            .Select(kv =>
            {
                var (name, gamesPlayed, wins, losses, pointsFor, pointsAgainst, closenessSum) = kv.Value;
                var winRate = gamesPlayed > 0 ? Math.Round(100.0 * wins / gamesPlayed, 1) : 0;
                var closeness = gamesPlayed > 0 ? Math.Round(closenessSum / gamesPlayed, 1) : 50;
                var rating = globalRatings.TryGetValue(kv.Key, out var global)
                    ? global.Rating
                    : _matchmaking.Rating.DefaultRating;
                var hasPaid = paidByPlayer.TryGetValue(kv.Key, out var paid) && paid;
                return new PlayerRankingRow(kv.Key, name, gamesPlayed, wins, losses, winRate, closeness, rating, pointsFor, pointsAgainst, hasPaid);
            })
            .OrderByDescending(r => r.Rating)
            .ThenByDescending(r => r.Wins)
            .ThenByDescending(r => r.WinRate)
            .ThenByDescending(r => r.Games)
            .ThenBy(r => r.Name)
            .ToList();
    }

    public List<GameSummaryRow> GetRecentGames(Guid? sessionId, int limit = 20)
    {
        using var db = _dbFactory.CreateDbContext();

        var query = db.Games.AsQueryable();
        if (sessionId is not null)
            query = query.Where(g => g.SessionId == sessionId);
        else
            query = query.Where(g => g.Session.IsTest == IsTestMode);

        return query
            .Include(g => g.Players)
            .ThenInclude(gp => gp.Player)
            .OrderByDescending(g => g.EndedAt)
            .Take(limit)
            .AsNoTracking()
            .ToList()
            .Select(g =>
            {
                var teamA = g.Players.Where(p => p.TeamSide == "TeamA").OrderBy(p => p.SlotIndex).Select(p => p.Player.Name);
                var teamB = g.Players.Where(p => p.TeamSide == "TeamB").OrderBy(p => p.SlotIndex).Select(p => p.Player.Name);
                return new GameSummaryRow(
                    g.Id,
                    g.EndedAt,
                    g.CourtId,
                    g.WinnerSide,
                    g.TeamAScore,
                    g.TeamBScore,
                    string.Join(" & ", teamA),
                    string.Join(" & ", teamB));
            })
            .ToList();
    }

    public List<MatchmakingExplanationRow> GetMatchmakingExplanations(Guid? sessionId, int limit = 40)
    {
        using var db = _dbFactory.CreateDbContext();

        var query = db.MatchmakingExplanations
            .Include(m => m.Game!)
            .ThenInclude(g => g.Players)
            .ThenInclude(gp => gp.Player)
            .AsQueryable();

        if (sessionId is not null)
            query = query.Where(m => m.SessionId == sessionId);
        else
            query = query.Where(m => m.Session.IsTest == IsTestMode);

        return query
            .OrderByDescending(m => m.PickedAt)
            .Take(limit)
            .AsNoTracking()
            .ToList()
            .Select(m =>
            {
                var details = ParseDetails(m.DetailsJson);
                string teamA, teamB;
                if (m.Game is not null)
                {
                    teamA = string.Join(" & ", m.Game.Players.Where(p => p.TeamSide == "TeamA").OrderBy(p => p.SlotIndex).Select(p => p.Player.Name));
                    teamB = string.Join(" & ", m.Game.Players.Where(p => p.TeamSide == "TeamB").OrderBy(p => p.SlotIndex).Select(p => p.Player.Name));
                }
                else
                {
                    teamA = string.Join(" & ", details.Chosen.Where(c => c.TeamSide == "TeamA").Select(c => c.Name));
                    teamB = string.Join(" & ", details.Chosen.Where(c => c.TeamSide == "TeamB").Select(c => c.Name));
                }

                return new MatchmakingExplanationRow(
                    m.Id,
                    m.GameId,
                    m.CourtId,
                    m.PickedAt,
                    m.Game?.EndedAt,
                    teamA,
                    teamB,
                    m.Game?.TeamAScore,
                    m.Game?.TeamBScore,
                    m.Game?.WinnerSide ?? "",
                    m.TotalScore,
                    m.WaitingScore,
                    m.MixingScore,
                    m.BalanceScore,
                    m.PeerScore,
                    m.HomogeneityScore,
                    m.WaitingWeight,
                    m.MixingWeight,
                    m.BalanceWeight,
                    m.PeerWeight,
                    m.HomogeneityWeight,
                    m.Algorithm,
                    m.DominantFactor,
                    m.Summary,
                    m.WaitingPoolSize,
                    m.CandidatesConsidered,
                    m.RankAmongCandidates,
                    m.UsedRandomness,
                    details.Pool,
                    details.Chosen,
                    details.Alternatives);
            })
            .ToList();
    }

    private static (List<MatchmakingPlayerSnapshot> Pool, List<MatchmakingPlayerSnapshot> Chosen, List<MatchmakingAlternative> Alternatives) ParseDetails(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var pool = root.TryGetProperty("Pool", out var p)
                ? System.Text.Json.JsonSerializer.Deserialize<List<MatchmakingPlayerSnapshot>>(p.GetRawText()) ?? new()
                : new();
            var chosen = root.TryGetProperty("Chosen", out var c)
                ? System.Text.Json.JsonSerializer.Deserialize<List<MatchmakingPlayerSnapshot>>(c.GetRawText()) ?? new()
                : new();
            var alts = root.TryGetProperty("Alternatives", out var a)
                ? System.Text.Json.JsonSerializer.Deserialize<List<MatchmakingAlternative>>(a.GetRawText()) ?? new()
                : new();
            return (pool, chosen, alts);
        }
        catch
        {
            return (new(), new(), new());
        }
    }

    public List<Player> GetAllPlayers()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Players
            .AsNoTracking()
            .Where(p => p.IsTest == IsTestMode)
            .OrderBy(p => p.Name)
            .ToList();
    }

    public HashSet<Guid> GetAttendeeIds(Guid sessionId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.SessionAttendances
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.PlayerId)
            .ToHashSet();
    }

    private Session CreateSession(RallyBoardDbContext db, string name, bool isTest)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var session = new Session
        {
            Date = today,
            Name = name,
            StartedAt = DateTime.UtcNow,
            IsTest = isTest
        };
        db.Sessions.Add(session);
        db.SaveChanges();
        return session;
    }

    private static string DefaultSessionName(DateOnly date) =>
        date.ToString("ddd d MMM yyyy");

    private static void EnsureAttendance(RallyBoardDbContext db, Guid sessionId, Guid playerId)
    {
        if (db.SessionAttendances.Any(a => a.SessionId == sessionId && a.PlayerId == playerId))
            return;

        db.SessionAttendances.Add(new SessionAttendance
        {
            SessionId = sessionId,
            PlayerId = playerId,
            CheckedInAt = DateTime.UtcNow
        });
    }
}
