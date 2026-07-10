using Microsoft.EntityFrameworkCore;
using RallyBoard.Data;
using RallyBoard.Models;

namespace RallyBoard.Services;

public class SessionService
{
    private readonly IDbContextFactory<RallyBoardDbContext> _dbFactory;
    private Guid? _currentSessionId;

    public event Action? OnChange;
    public event Action? SessionStarted;

    public SessionService(IDbContextFactory<RallyBoardDbContext> dbFactory)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        EnsureSchema();
        _currentSessionId = GetOrCreateCurrentSessionId();
    }

    public Guid CurrentSessionId => _currentSessionId ??= GetOrCreateCurrentSessionId();

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
            .Where(s => s.EndedAt == null)
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

        return CreateSession(db, DefaultSessionName(today)).Id;
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

        var newSession = CreateSession(db, name);
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
        IReadOnlyList<(Player Player, int SlotIndex)> players)
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
            .OrderByDescending(s => s.Date)
            .ThenByDescending(s => s.StartedAt)
            .Select(s => new SessionSummaryRow(
                s.Id,
                s.Date,
                s.Name,
                s.Games.Count,
                s.Attendances.Count,
                s.EndedAt == null))
            .ToList();
    }

    public SessionStats GetSessionStats(Guid? sessionId)
    {
        using var db = _dbFactory.CreateDbContext();
        var attendances = sessionId is null
            ? db.SessionAttendances
            : db.SessionAttendances.Where(a => a.SessionId == sessionId);
        var paid = attendances.Count(a => a.HasPaid);
        var total = attendances.Select(a => a.PlayerId).Distinct().Count();
        var games = sessionId is null
            ? db.Games.Count()
            : db.Games.Count(g => g.SessionId == sessionId);

        return new SessionStats(games, total, paid, Math.Max(0, attendances.Count() - paid));
    }

    public List<PlayerRankingRow> GetRankings(Guid? sessionId)
    {
        using var db = _dbFactory.CreateDbContext();

        var gamesQuery = db.Games.AsQueryable();
        if (sessionId is not null)
            gamesQuery = gamesQuery.Where(g => g.SessionId == sessionId);

        var games = gamesQuery
            .Include(g => g.Players)
            .ThenInclude(gp => gp.Player)
            .AsNoTracking()
            .ToList();

        var attendanceQuery = db.SessionAttendances.AsQueryable();
        if (sessionId is not null)
            attendanceQuery = attendanceQuery.Where(a => a.SessionId == sessionId);

        var attendances = attendanceQuery
            .Include(a => a.Player)
            .AsNoTracking()
            .ToList();

        var paidByPlayer = attendances
            .GroupBy(a => a.PlayerId)
            .ToDictionary(g => g.Key, g => g.Any(a => a.HasPaid));

        var stats = new Dictionary<Guid, (string Name, int Games, int Wins, int Losses, int PointsFor, int PointsAgainst)>();

        foreach (var attendance in attendances.GroupBy(a => a.PlayerId).Select(g => g.First()))
        {
            stats[attendance.PlayerId] = (attendance.Player.Name, 0, 0, 0, 0, 0);
        }

        foreach (var game in games)
        {
            foreach (var gp in game.Players)
            {
                if (!stats.TryGetValue(gp.PlayerId, out var row))
                    row = (gp.Player.Name, 0, 0, 0, 0, 0);

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
                    row.PointsAgainst + pointsAgainst);
            }
        }

        return stats
            .Select(kv =>
            {
                var (name, gamesPlayed, wins, losses, pointsFor, pointsAgainst) = kv.Value;
                var winRate = gamesPlayed > 0 ? Math.Round(100.0 * wins / gamesPlayed, 1) : 0;
                var hasPaid = paidByPlayer.TryGetValue(kv.Key, out var paid) && paid;
                return new PlayerRankingRow(kv.Key, name, gamesPlayed, wins, losses, winRate, pointsFor, pointsAgainst, hasPaid);
            })
            .OrderByDescending(r => r.Wins)
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

    public List<Player> GetAllPlayers()
    {
        using var db = _dbFactory.CreateDbContext();
        return db.Players.AsNoTracking().OrderBy(p => p.Name).ToList();
    }

    public HashSet<Guid> GetAttendeeIds(Guid sessionId)
    {
        using var db = _dbFactory.CreateDbContext();
        return db.SessionAttendances
            .Where(a => a.SessionId == sessionId)
            .Select(a => a.PlayerId)
            .ToHashSet();
    }

    private static Session CreateSession(RallyBoardDbContext db, string name)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var session = new Session
        {
            Date = today,
            Name = name,
            StartedAt = DateTime.UtcNow
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
