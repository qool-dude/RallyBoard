using Microsoft.EntityFrameworkCore;
using RallyBoard.Data;
using RallyBoard.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace RallyBoard.Services
{
    public class CourtAllocationService : IDisposable
    {
        private readonly IDbContextFactory<RallyBoardDbContext> _dbFactory;
        private readonly SessionService _sessions;
        private readonly MatchmakingService _matchmaking;
            private Timer? _tickTimer;
            private Player? _draggedPlayer;
            private int _tickCount;

            // raised once per second to allow UI to refresh elapsed times
            public event Action? Tick;

        // raised when state changes (players moved, courts modified, etc.)
        public event Action? OnChange;

        /// <summary>Fired when a chip menu opens so other chips can close theirs.</summary>
        public event Action<Guid>? ChipMenuOpening;

        public List<Court> Courts { get; } = new();
        public List<Player> Waiting { get; } = new();

        public void NotifyChipMenuOpening(Guid playerId) => ChipMenuOpening?.Invoke(playerId);
        public CourtAllocationService(
            IDbContextFactory<RallyBoardDbContext> dbFactory,
            SessionService sessions,
            MatchmakingService matchmaking)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _matchmaking = matchmaking ?? throw new ArgumentNullException(nameof(matchmaking));
            _sessions.SessionStarted += ClearSessionBoard;
            _sessions.ModeChanged += OnModeChanged;

            for (int i = 1; i <= 3; i++)
                Courts.Add(new Court { Id = i, Name = $"Court {i}" });

            // Load persisted players/assignments if present
            try
            {
                using var db = _dbFactory.CreateDbContext();
                DatabaseInitializer.EnsureSchema(db);

                var dbPlayers = db.Players.AsNoTracking().ToList();
                if (!dbPlayers.Any())
                {
                    foreach (var name in new[] { "Ayesha", "Bilal", "Sana", "Usman", "Hina", "Zain" })
                        db.Players.Add(new Player { Name = name, IsTest = true });
                    db.SaveChanges();
                }

                // Clear any stale assignments from a previous run
                if (db.Assignments.Any())
                {
                    db.Assignments.RemoveRange(db.Assignments);
                    db.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CourtAllocationService DB init error: {ex}");
            }

            // Re-check-in anyone already marked present for the active session
            try
            {
                foreach (var playerId in _sessions.GetAttendeeIds(_sessions.CurrentSessionId))
                    CheckInPlayer(playerId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CourtAllocationService attendance restore error: {ex}");
            }

            // start centralized tick timer for UI updates (every 1s)
            _tickTimer = new Timer(_ =>
            {
                try
                {
                    _tickCount++;
                    // Log every 10 ticks to avoid spam
                    if (_tickCount % 10 == 0)
                        Console.WriteLine($"Tick fired {_tickCount} times");
                    Tick?.Invoke();
                }
                catch (Exception ex)
                {
                    // prevent timer termination on unhandled exceptions; surface to logs
                    Console.WriteLine($"Tick error: {ex}");
                }
            }, null, 0, 1000);

            // diagnostic logging to confirm single instance and timer start
            try
            {
                using var db = _dbFactory.CreateDbContext();
                Console.WriteLine($"CourtAllocationService ctor: Hash={GetHashCode()}, PlayersLoaded={db.Players.Count()}");
                Console.WriteLine("CourtAllocationService tick timer started");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CourtAllocationService ctor logging error: {ex}");
            }
        }

        public void MarkPlayerWaiting(Player p)
        {
            if (p is null) return;
            if (p.WaitingSince is not null) return; // already waiting)
            p.WaitingSince = DateTime.UtcNow;
            p.IsPaused = false;
            p.PausedAt = null;
            p.PausedAccumulated = TimeSpan.Zero;
        }

        public void PersistState()
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();

                var playersOnCourts = Courts.SelectMany(c => c.Slots.Where(s => s is not null)).Cast<Player>();
                var allPlayers = Waiting.Concat(playersOnCourts)
                    .GroupBy(p => p.Id)
                    .Select(g => g.First())
                    .ToList();

                var existingById = db.Players.ToDictionary(p => p.Id);
                foreach (var p in allPlayers)
                {
                    if (existingById.TryGetValue(p.Id, out var existing))
                    {
                        existing.Name = p.Name;
                        existing.WaitingSince = p.WaitingSince;
                        existing.IsPaused = p.IsPaused;
                        existing.PausedAt = p.PausedAt;
                        existing.PausedAccumulated = p.PausedAccumulated;
                    }
                    else
                    {
                        db.Players.Add(new Player
                        {
                            Id = p.Id,
                            Name = p.Name,
                            ColorIndex = p.ColorIndex,
                            IsTest = p.IsTest,
                            WaitingSince = p.WaitingSince,
                            IsPaused = p.IsPaused,
                            PausedAt = p.PausedAt,
                            PausedAccumulated = p.PausedAccumulated
                        });
                    }
                }

                db.Assignments.RemoveRange(db.Assignments);
                db.SaveChanges();

                foreach (var court in Courts)
                {
                    for (int i = 0; i < court.Slots.Length; i++)
                    {
                        var player = court.Slots[i];
                        if (player is null) continue;

                        db.Assignments.Add(new Assignment
                        {
                            CourtId = court.Id,
                            SlotIndex = i,
                            PlayerId = player.Id
                        });
                    }
                }

                db.SaveChanges();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PersistState error: {ex}");
            }
        }

        public void ClearAllCourts()
        {
            // Move all players from courts back to waiting
            foreach (var court in Courts)
            {
                for (int i = 0; i < court.Slots.Length; i++)
                {
                    var player = court.Slots[i];
                    if (player is not null)
                    {
                        MarkPlayerWaiting(player);
                        Waiting.Add(player);
                        court.Slots[i] = null;
                    }
                }
            }

            PersistState();
            OnChange?.Invoke();
        }

        public void ShuffleAll()
        {
            var allPlayers = Waiting
                .Concat(Courts.SelectMany(c => c.Slots.Where(s => s is not null)).Cast<Player>())
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .ToList();

            foreach (var court in Courts)
                court.Reset();

            Waiting.Clear();
            foreach (var player in allPlayers)
            {
                // Court players had WaitingSince cleared when their game started
                MarkPlayerWaiting(player);
                Waiting.Add(player);
            }

            foreach (var court in Courts)
            {
                var pick = _matchmaking.PickLineup(GetAvailableWaitingPlayers(), _sessions.CurrentSessionId, _sessions.IsTestMode);
                if (pick is null)
                    break;

                for (int i = 0; i < court.Slots.Length; i++)
                {
                    court.Slots[i] = pick.Slots[i];
                    Waiting.Remove(pick.Slots[i]);
                }

                court.PendingMatchmaking = pick.Decision;
            }

            PersistState();
            OnChange?.Invoke();
        }

        /// <summary>
        /// Proposes a lineup without mutating the court or waiting pool.
        /// </summary>
        public MatchmakingPickResult? ProposePickGame(Court? court = null)
        {
            var available = GetAvailableWaitingPlayers();
            var fixedSlots = court?.Slots.ToArray() ?? new Player?[4];
            var slotsToFill = fixedSlots.Count(p => p is null);
            if (slotsToFill == 0 || available.Count < slotsToFill)
                return null;

            return _matchmaking.PickLineup(
                available,
                _sessions.CurrentSessionId,
                _sessions.IsTestMode,
                fixedSlots);
        }

        /// <summary>
        /// Applies a previously proposed lineup to the court and removes players from waiting.
        /// </summary>
        public void ApplyPickGame(Court c, MatchmakingPickResult pick, bool startTimer = true)
        {
            if (c is null || pick?.Slots is null || pick.Slots.Length < 4)
                return;

            c.ResetTimer();
            c.PendingMatchmaking = pick.Decision;

            for (int i = 0; i < c.Slots.Length; i++)
            {
                var player = pick.Slots[i];
                RemovePlayerFromAllLocations(player);
                c.Slots[i] = player;
            }

            if (startTimer)
                StartTimer(c);

            PersistState();
            OnChange?.Invoke();
        }

        public void PickGame(Court c)
        {
            var pick = ProposePickGame(c);
            if (pick is null)
                return;

            ApplyPickGame(c, pick, startTimer: false);
        }

        public void SetCourtCount(int count)
        {
            // Ensure count is at least 1
            if (count < 1) count = 1;

            int currentCount = Courts.Count;

            if (count > currentCount)
            {
                // Add new courts
                for (int i = currentCount + 1; i <= count; i++)
                {
                    Courts.Add(new Court { Id = i, Name = $"Court {i}" });
                }
            }
            else if (count < currentCount)
            {
                // Remove courts and move players back to waiting
                for (int i = currentCount; i > count; i--)
                {
                    var court = Courts.FirstOrDefault(c => c.Id == i);
                    if (court is not null)
                    {
                        // Move all players from removed court to waiting and start their timers
                        foreach (var player in court.Slots.Where(p => p is not null))
                        {
                            MarkPlayerWaiting(player);
                            Waiting.Add(player);
                        }

                        Courts.Remove(court);
                    }
                }
            }

            PersistState();
            OnChange?.Invoke();
        }

        public void StartDrag(Player player)
        {
            _draggedPlayer = player;
            Console.WriteLine($"Drag started: {player?.Name}");
        }

        public void DropOnSlot(Court court, int slotIndex)
        {
            if (_draggedPlayer is null || slotIndex < 0 || slotIndex >= court.Slots.Length)
                return;

            if (court.LineupLocked || IsPlayerOnLockedCourt(_draggedPlayer))
            {
                _draggedPlayer = null;
                return;
            }

            var targetPlayer = court.Slots[slotIndex];

            if (targetPlayer is not null && targetPlayer.Id != _draggedPlayer.Id)
            {
                var dragged = _draggedPlayer;
                _draggedPlayer = null;
                SwapPlayers(dragged, targetPlayer);
                return;
            }

            RemovePlayerFromAllLocations(_draggedPlayer);
            court.Slots[slotIndex] = _draggedPlayer;
            _draggedPlayer = null;

            PersistState();
            OnChange?.Invoke();
        }

        /// <summary>Places a waiting (or elsewhere) player into an empty or occupied court slot.</summary>
        public void AssignPlayerToSlot(Court court, int slotIndex, Player player)
        {
            if (court is null || player is null || slotIndex < 0 || slotIndex >= court.Slots.Length)
                return;

            if (court.LineupLocked || IsPlayerOnLockedCourt(player))
                return;

            var targetPlayer = court.Slots[slotIndex];
            if (targetPlayer is not null && targetPlayer.Id != player.Id)
            {
                SwapPlayers(player, targetPlayer);
                return;
            }

            RemovePlayerFromAllLocations(player);
            court.Slots[slotIndex] = player;

            PersistState();
            OnChange?.Invoke();
        }

        public void DropOnPlayer(Player target)
        {
            if (_draggedPlayer is null || target is null || _draggedPlayer.Id == target.Id)
                return;

            if (IsPlayerOnLockedCourt(_draggedPlayer) || IsPlayerOnLockedCourt(target))
            {
                _draggedPlayer = null;
                return;
            }

            var dragged = _draggedPlayer;
            _draggedPlayer = null;
            SwapPlayers(dragged, target);
        }

        public void DropOnWaiting()
        {
            if (_draggedPlayer is null)
                return;

            if (IsPlayerOnLockedCourt(_draggedPlayer))
            {
                _draggedPlayer = null;
                return;
            }

            // Remove from courts
            RemovePlayerFromAllLocations(_draggedPlayer);

            // Add to waiting and start/reset waiting timer
            if (!Waiting.Contains(_draggedPlayer))
            {
                MarkPlayerWaiting(_draggedPlayer);
                Waiting.Add(_draggedPlayer);
            }

            _draggedPlayer = null;

            PersistState();
            OnChange?.Invoke();
        }

        public void DeletePlayer(Player player)
        {
            if (IsPlayerOnLockedCourt(player))
                return;

            // Remove player from courts and waiting
            RemovePlayerFromAllLocations(player);

            // Note: we don't delete from DB automatically; just remove from in-memory state
            PersistState();
            OnChange?.Invoke();
        }

        public void TogglePause(Player player)
        {
            if (player is null) return;

            player.IsPaused = !player.IsPaused;

            if (player.IsPaused)
            {
                player.PausedAt = DateTime.UtcNow;
            }
            else
            {
                // Resume: accumulate paused time
                if (player.PausedAt is not null)
                {
                    var pauseDuration = DateTime.UtcNow - player.PausedAt.Value;
                    player.PausedAccumulated += pauseDuration;
                    player.PausedAt = null;
                }
            }

            PersistState();
            OnChange?.Invoke();
        }

        public void SwapPlayers(Player player1, Player player2)
        {
            if (player1 is null || player2 is null) return;
            if (IsPlayerOnLockedCourt(player1) || IsPlayerOnLockedCourt(player2))
                return;

            // Find locations
            var (location1, pos1) = FindPlayerLocation(player1);
            var (location2, pos2) = FindPlayerLocation(player2);

            // Perform swap based on location types
            if (location1 == "waiting" && location2 == "waiting")
            {
                var idx1 = Waiting.IndexOf(player1);
                var idx2 = Waiting.IndexOf(player2);
                if (idx1 >= 0 && idx2 >= 0)
                {
                    Waiting[idx1] = player2;
                    Waiting[idx2] = player1;
                }
            }
            else if (location1 == "waiting" && location2 == "court")
            {
                if (pos2 is (Court court2, int slot2))
                {
                    court2.Slots[slot2] = player1;
                    Waiting.Remove(player1);
                    // Court players often have WaitingSince cleared — restart their wait timer
                    player2.WaitingSince = null;
                    MarkPlayerWaiting(player2);
                    Waiting.Add(player2);
                }
            }
            else if (location1 == "court" && location2 == "waiting")
            {
                if (pos1 is (Court court1, int slot1))
                {
                    court1.Slots[slot1] = player2;
                    Waiting.Remove(player2);
                    player1.WaitingSince = null;
                    MarkPlayerWaiting(player1);
                    Waiting.Add(player1);
                }
            }
            else if (location1 == "court" && location2 == "court")
            {
                if (pos1 is (Court court1, int slot1) && pos2 is (Court court2, int slot2))
                {
                    court1.Slots[slot1] = player2;
                    court2.Slots[slot2] = player1;
                }
            }

            PersistState();
            OnChange?.Invoke();
        }

        public void AddPlayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            using var db = _dbFactory.CreateDbContext();
            var trimmed = name.Trim();
            var isTest = _sessions.IsTestMode;
            var existing = db.Players
                .Where(p => p.IsTest == isTest)
                .AsEnumerable()
                .FirstOrDefault(p => p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                CheckInPlayer(existing.Id);
                return;
            }

            int colorIndex = db.Players.Count(p => p.IsTest == isTest) % 6;
            var player = new Player { Name = trimmed, ColorIndex = colorIndex, IsTest = isTest };
            db.Players.Add(player);
            db.SaveChanges();

            CheckInPlayer(player.Id);
        }

        public bool RenamePlayer(Guid playerId, string name)
        {
            var trimmed = name.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return false;

            using var db = _dbFactory.CreateDbContext();
            var player = db.Players.FirstOrDefault(p =>
                p.Id == playerId && p.IsTest == _sessions.IsTestMode);
            if (player is null) return false;

            var duplicate = db.Players
                .Where(p => p.Id != playerId && p.IsTest == player.IsTest)
                .AsEnumerable()
                .Any(p => p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (duplicate) return false;

            player.Name = trimmed;
            db.SaveChanges();

            foreach (var activePlayer in Waiting
                .Concat(Courts.SelectMany(c => c.Slots).OfType<Player>())
                .Where(p => p.Id == playerId))
            {
                activePlayer.Name = trimmed;
            }

            OnChange?.Invoke();
            return true;
        }

        public void CheckInPlayer(Guid playerId)
        {
            if (GetActivePlayerIds().Contains(playerId))
                return;

            using var db = _dbFactory.CreateDbContext();
            var player = db.Players.AsNoTracking().FirstOrDefault(p => p.Id == playerId);
            if (player is null) return;
            player.WaitingSince = DateTime.UtcNow;
            MarkPlayerWaiting(player);
            Waiting.Add(player);
            _sessions.RecordAttendance(player.Id);
            PersistState();
            OnChange?.Invoke();
        }

        public List<Player> GetRosterPlayers() => _sessions.GetAllPlayers();

        /// <summary>Waiting players eligible for automatic matchmaking (not paused).</summary>
        public List<Player> GetAvailableWaitingPlayers() =>
            Waiting.Where(p => !p.IsPaused).ToList();

        public HashSet<Guid> GetActivePlayerIds()
        {
            var ids = Waiting.Select(p => p.Id).ToHashSet();
            foreach (var court in Courts)
                foreach (var slot in court.Slots)
                    if (slot is not null) ids.Add(slot.Id);
            return ids;
        }

        public void ClearSessionBoard()
        {
            foreach (var court in Courts)
                court.Reset();

            Waiting.Clear();
            PersistState();
            OnChange?.Invoke();
        }

        public void ClearCourt(Court court)
        {
            if (court is null) return;
            if (court.LineupLocked) return;

            for (int i = 0; i < court.Slots.Length; i++)
            {
                var player = court.Slots[i];
                if (player is not null)
                {
                    MarkPlayerWaiting(player);
                    Waiting.Add(player);
                    court.Slots[i] = null;
                }
            }

            PersistState();
            OnChange?.Invoke();
        }

        /// <summary>
        /// Swaps everything assigned to two physical courts, including players,
        /// timer state, score state, and the pending matchmaking explanation.
        /// </summary>
        public void MoveCourt(Court source, Court destination)
        {
            if (source is null || destination is null || source.Id == destination.Id)
                return;

            for (var i = 0; i < source.Slots.Length; i++)
                (source.Slots[i], destination.Slots[i]) = (destination.Slots[i], source.Slots[i]);

            (source.StartedAt, destination.StartedAt) = (destination.StartedAt, source.StartedAt);
            (source.PausedAt, destination.PausedAt) = (destination.PausedAt, source.PausedAt);
            (source.AccumulatedTime, destination.AccumulatedTime) =
                (destination.AccumulatedTime, source.AccumulatedTime);
            (source.Winner, destination.Winner) = (destination.Winner, source.Winner);
            (source.TeamAScore, destination.TeamAScore) =
                (destination.TeamAScore, source.TeamAScore);
            (source.TeamBScore, destination.TeamBScore) =
                (destination.TeamBScore, source.TeamBScore);
            (source.PendingMatchmaking, destination.PendingMatchmaking) =
                (destination.PendingMatchmaking, source.PendingMatchmaking);

            PersistState();
            OnChange?.Invoke();
        }

        public void EndGame(Court court, string winner, int? teamAScore, int? teamBScore)
        {
            if (court is null || string.IsNullOrWhiteSpace(winner))
                return;

            if (winner is not ("TeamA" or "TeamB" or "Tie"))
                return;

            if (court.IsRunning)
                court.PausedAt = DateTime.UtcNow;

            var duration = court.GetElapsedTime();
            var gamePlayers = court.Slots
                .Select((player, index) => (Player: player, SlotIndex: index))
                .Where(x => x.Player is not null)
                .Select(x => (x.Player!, x.SlotIndex))
                .ToList();

            if (gamePlayers.Count > 0)
            {
                _sessions.RecordGame(
                    court.Id,
                    winner,
                    teamAScore,
                    teamBScore,
                    duration,
                    gamePlayers,
                    court.PendingMatchmaking);
            }

            for (int i = 0; i < court.Slots.Length; i++)
            {
                var player = court.Slots[i];
                if (player is not null)
                {
                    MarkPlayerWaiting(player);
                    Waiting.Add(player);
                }
            }

            court.Reset();

            PersistState();
            OnChange?.Invoke();
        }

        public void ToggleTimer(Court court)
        {
            if (court is null) return;

            if (court.IsRunning)
            {
                court.PausedAt = DateTime.UtcNow;
            }
            else if (court.IsPaused)
            {
                var pauseDuration = court.PausedAt!.Value - court.StartedAt!.Value;
                court.AccumulatedTime += pauseDuration;
                court.StartedAt = DateTime.UtcNow;
                court.PausedAt = null;
            }
            else
            {
                StartTimer(court);
            }

            PersistState();
            OnChange?.Invoke();
        }

        /// <summary>
        /// Starts (or resumes) the timer on every court that has at least 2 players.
        /// Already-running courts are left alone.
        /// </summary>
        public void StartAllTimers()
        {
            var changed = false;
            foreach (var court in Courts)
            {
                if (court.FilledCount < 2 || court.IsRunning)
                    continue;

                if (court.IsPaused)
                {
                    var pauseDuration = court.PausedAt!.Value - court.StartedAt!.Value;
                    court.AccumulatedTime += pauseDuration;
                    court.StartedAt = DateTime.UtcNow;
                    court.PausedAt = null;
                }
                else
                {
                    StartTimer(court);
                }
                changed = true;
            }

            if (!changed) return;

            PersistState();
            OnChange?.Invoke();
        }

        private void StartTimer(Court court)
        {
            court.StartedAt = DateTime.UtcNow;
            court.PausedAt = null;
            court.AccumulatedTime = TimeSpan.Zero;

            for (int i = 0; i < court.Slots.Length; i++)
            {
                if (court.Slots[i]?.WaitingSince != null)
                {
                    var p = court.Slots[i]!;
                    p.TotalWaiting += DateTime.UtcNow - p.WaitingSince.Value;
                    p.WaitingSince = null;
                }
            }
        }

        private bool IsPlayerOnLockedCourt(Player player)
        {
            if (player is null) return false;
            foreach (var court in Courts)
            {
                if (!court.LineupLocked) continue;
                if (court.Slots.Any(s => s?.Id == player.Id))
                    return true;
            }
            return false;
        }

        private (string location, object? position) FindPlayerLocation(Player player)
        {
            // Check in waiting
            if (Waiting.Contains(player))
                return ("waiting", null);

            // Check in courts
            foreach (var court in Courts)
            {
                for (int i = 0; i < court.Slots.Length; i++)
                {
                    if (court.Slots[i]?.Id == player.Id)
                        return ("court", (court, i));
                }
            }

            return ("unknown", null);
        }

        private void RemovePlayerFromAllLocations(Player player)
        {
            // Remove from waiting
            Waiting.RemoveAll(p => p.Id == player.Id);

            // Remove from courts
            foreach (var court in Courts)
            {
                for (int i = 0; i < court.Slots.Length; i++)
                {
                    if (court.Slots[i]?.Id == player.Id)
                        court.Slots[i] = null;
                }
            }
        }

        private void OnModeChanged()
        {
            ClearSessionBoard();
            try
            {
                foreach (var playerId in _sessions.GetAttendeeIds(_sessions.CurrentSessionId))
                    CheckInPlayer(playerId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CourtAllocationService mode switch restore error: {ex}");
            }
        }

        public void Dispose()
        {
            _sessions.SessionStarted -= ClearSessionBoard;
            _sessions.ModeChanged -= OnModeChanged;
            _tickTimer?.Dispose();
        }
    }
}
