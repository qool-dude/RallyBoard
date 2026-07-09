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
            private Timer? _tickTimer;
            private Player? _draggedPlayer;
            private int _tickCount;

            // raised once per second to allow UI to refresh elapsed times
            public event Action? Tick;

        // raised when state changes (players moved, courts modified, etc.)
        public event Action? OnChange;

        public List<Court> Courts { get; } = new();
        public List<Player> Waiting { get; } = new();

        public CourtAllocationService(IDbContextFactory<RallyBoardDbContext> dbFactory)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));

            for (int i = 1; i <= 2; i++)
                Courts.Add(new Court { Id = i, Name = $"Court {i}" });

            // Load persisted players/assignments if present
            try
            {
                using var db = _dbFactory.CreateDbContext();
                db.Database.EnsureCreated();

                var dbPlayers = db.Players.AsNoTracking().ToList();
                if (dbPlayers.Any())
                {
                    var assignments = db.Assignments.AsNoTracking().ToList();
                    var playersById = dbPlayers.ToDictionary(p => p.Id);

                    // place assigned players onto courts
                    foreach (var assign in assignments)
                    {
                        var court = Courts.FirstOrDefault(c => c.Id == assign.CourtId);
                        if (court is not null && assign.SlotIndex >= 0 && assign.SlotIndex < court.Slots.Length)
                        {
                            if (playersById.TryGetValue(assign.PlayerId, out var player))
                                court.Slots[assign.SlotIndex] = player;
                        }
                    }

                    // players not assigned go to waiting
                    var assignedIds = assignments.Select(a => a.PlayerId).ToHashSet();
                    foreach (var p in dbPlayers)
                        if (!assignedIds.Contains(p.Id)) Waiting.Add(p);
                }
                else
                {
                    // seed defaults
                    foreach (var name in new[] { "Ayesha", "Bilal", "Sana", "Usman", "Hina", "Zain" })
                    {
                        var p = new Player { Name = name };
                        MarkPlayerWaiting(p);
                        Waiting.Add(p);
                    }

                    PersistState();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CourtAllocationService DB init error: {ex}");
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

                // Simplest approach: wipe and repopulate Players and Assignments to match in-memory state
                // WARNING: this will remove any external changes made directly in the DB.

                var existingAssignments = db.Assignments.ToList();
                db.Assignments.RemoveRange(existingAssignments);

                var existingPlayers = db.Players.ToList();
                db.Players.RemoveRange(existingPlayers);

                db.SaveChanges();

                // collect all players from courts and waiting (skip nulls)
                var playersOnCourts = Courts.SelectMany(c => c.Slots.Where(s => s is not null)).Cast<Player>();
                var allPlayers = Waiting.Concat(playersOnCourts)
                    .GroupBy(p => p.Id)
                    .Select(g => g.First())
                    .ToList();

                // insert players
                foreach (var p in allPlayers)
                {
                    db.Players.Add(new Player
                    {
                        Id = p.Id,
                        Name = p.Name,
                        ColorIndex = p.ColorIndex,
                        WaitingSince = p.WaitingSince,
                        IsPaused = p.IsPaused,
                        PausedAt = p.PausedAt,
                        PausedAccumulated = p.PausedAccumulated
                    });
                }

                db.SaveChanges();

                // insert assignments for players on courts
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
            // Collect all players (from courts and waiting)
            var allPlayers = Waiting.Concat(Courts.SelectMany(c => c.Slots.Where(s => s is not null))).ToList();

            // Clear all courts
            foreach (var court in Courts)
            {
                Array.Clear(court.Slots, 0, court.Slots.Length);
            }

            Waiting.Clear();

            // Shuffle and re-allocate players randomly
            var random = new Random();
            allPlayers = allPlayers.OrderBy(_ => random.Next()).ToList();

            // Allocate to courts first, then remaining to waiting
            int playerIndex = 0;
            foreach (var court in Courts)
            {
                for (int i = 0; i < court.Slots.Length && playerIndex < allPlayers.Count; i++)
                {
                    court.Slots[i] = allPlayers[playerIndex++];
                }
            }

            // Remaining players go to waiting
            while (playerIndex < allPlayers.Count)
            {
                Waiting.Add(allPlayers[playerIndex++]);
            }

            PersistState();
            OnChange?.Invoke();
        }

        public void PickGame(Court c)
        {
            // Collect all players (from courts and waiting)
            var allPlayers = Waiting.ToList();

            // Shuffle and re-allocate players randomly
            var random = new Random();
            allPlayers = allPlayers.OrderBy(_ => random.Next()).ToList();

            // Allocate to court and remove from waiting
            for (int i = 0; i < c.Slots.Length; i++)
            {
                c.Slots[i] = allPlayers[i];
                Waiting.Remove(allPlayers[i]);
            }

            // Remaining players go to waiting
            //while (playerIndex < allPlayers.Count)
            //{
            //    Waiting.Add(allPlayers[playerIndex++]);
            //}

            PersistState();
            OnChange?.Invoke();
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

        public void DropOnPlayer(Player target)
        {
            if (_draggedPlayer is null || target is null || _draggedPlayer.Id == target.Id)
                return;

            var dragged = _draggedPlayer;
            _draggedPlayer = null;
            SwapPlayers(dragged, target);
        }

        public void DropOnWaiting()
        {
            if (_draggedPlayer is null)
                return;

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
                    Waiting.Add(player2);
                }
            }
            else if (location1 == "court" && location2 == "waiting")
            {
                if (pos1 is (Court court1, int slot1))
                {
                    court1.Slots[slot1] = player2;
                    Waiting.Remove(player2);
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

            // Calculate color based on total existing players (more reliable than a global seed)
            int totalPlayers = Waiting.Count + Courts.SelectMany(c => c.Slots).Count(s => s is not null);
            int colorIndex = totalPlayers % 6;

            var player = new Player { Name = name, ColorIndex = colorIndex };
            Console.WriteLine($"AddPlayer: Created {player.Name} with ColorIndex={player.ColorIndex} (totalPlayers={totalPlayers})");
            MarkPlayerWaiting(player);
            Waiting.Add(player);

            PersistState();
            OnChange?.Invoke();
        }

        public void ClearCourt(Court court)
        {
            if (court is null) return;

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

        public void EndGame(Court court, string winner, int? teamAScore, int? teamBScore)
        {
            if (court is null || string.IsNullOrWhiteSpace(winner))
                return;

            if (winner is not ("TeamA" or "TeamB" or "Tie"))
                return;

            if (court.IsRunning)
                court.PausedAt = DateTime.UtcNow;

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
                // Pause: record the pause time
                court.PausedAt = DateTime.UtcNow;
            }
            else if (court.IsPaused)
            {
                // Resume: add the pause duration to accumulated time and clear pause marker
                var pauseDuration = court.PausedAt.Value - court.StartedAt!.Value;
                court.AccumulatedTime += pauseDuration;
                court.StartedAt = DateTime.UtcNow;
                court.PausedAt = null;
            }
            else
            {
                // Start: initial start
                court.StartedAt = DateTime.UtcNow;
                court.PausedAt = null;
                court.AccumulatedTime = TimeSpan.Zero;
            }

            for (int i = 0; i < court.Slots.Length; i++)
            {
                if (court.Slots[i]?.WaitingSince != null)
                {
                    Player p = (Player)court.Slots[i];
                    p.TotalWaiting += DateTime.UtcNow - p.WaitingSince.Value;
                    p.WaitingSince = null;
                }
            }


            PersistState();
            OnChange?.Invoke();
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

        public void Dispose()
        {
            _tickTimer?.Dispose();
        }
    }
}
