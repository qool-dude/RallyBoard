using RallyBoard.Models;
using RallyBoard.Data;
using Microsoft.EntityFrameworkCore;

namespace RallyBoard.Services;

public class CourtAllocationService
{
    public const int MaxCourts = 5;
    public const int MinCourts = 1;

    public List<Court> Courts { get; } = new();
    public List<Player> Waiting { get; } = new();

    public event Action? OnChange;

    private Guid? _draggingId;
    private readonly RallyBoardDbContext _db;

    public CourtAllocationService(RallyBoardDbContext db)
    {
        _db = db;
        _db.Database.EnsureCreated();

        for (int i = 1; i <= 2; i++)
            Courts.Add(new Court { Id = i, Name = $"Court {i}" });

        // Load persisted players/assignments if present
        var dbPlayers = _db.Players.AsNoTracking().ToList();
        if (dbPlayers.Any())
        {
            var assignments = _db.Assignments.AsNoTracking().ToList();
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

    // ---------- Court count ----------

    public void SetCourtCount(int count)
    {
        count = Math.Clamp(count, MinCourts, MaxCourts);

        while (Courts.Count < count)
            Courts.Add(new Court { Id = Courts.Count + 1, Name = $"Court {Courts.Count + 1}" });

        while (Courts.Count > count)
        {
            var last = Courts[^1];
            foreach (var p in last.Slots.Where(p => p is not null))
            {
                MarkPlayerWaiting(p!);
                Waiting.Add(p!);
            }
            Courts.RemoveAt(Courts.Count - 1);
        }

        Notify();
    }

    // ---------- Player management ----------

    public void AddPlayer(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = new Player { Name = name };
        MarkPlayerWaiting(p);
        Waiting.Add(p);
        Notify();
    }

    public void DeletePlayer(Player player)
    {
        if (player is null) return;
        RemovePlayerFromAll(player.Id);
        Notify();
    }

    // ---------- Drag and drop ----------

    public void StartDrag(Player player) => _draggingId = player.Id;

    public void DropOnSlot(Court targetCourt, int targetIndex)
    {
        if (_draggingId is null) return;
        var dragged = FindPlayer(_draggingId.Value);
        if (dragged is null) { _draggingId = null; return; }
        var displaced = targetCourt.Slots[targetIndex];
        var (sourceCourt, sourceIndex) = FindCourtSlot(dragged);

        // Ensure no duplicates: remove any occurrences first
        RemovePlayerFromAll(dragged.Id);
        if (displaced is not null && displaced.Id != dragged.Id)
            RemovePlayerFromAll(displaced.Id);

        // Place dragged player into target slot
        targetCourt.Slots[targetIndex] = dragged;

        // Put whatever was in the target slot into the dragged player's old spot (swap)
        if (displaced is not null && displaced.Id != dragged.Id)
        {
            if (sourceCourt is not null) sourceCourt.Slots[sourceIndex] = displaced;
            else { MarkPlayerWaiting(displaced); Waiting.Add(displaced); }
        }

        _draggingId = null;
        Notify();
    }

    public void DropOnWaiting()
    {
        if (_draggingId is null) return;
        var dragged = FindPlayer(_draggingId.Value);
        if (dragged is null) { _draggingId = null; return; }

        var (sourceCourt, sourceIndex) = FindCourtSlot(dragged);
        if (sourceCourt is not null)
        {
            sourceCourt.Slots[sourceIndex] = null;
            // ensure no duplicates
            RemovePlayerFromAll(dragged.Id);
            MarkPlayerWaiting(dragged);
            Waiting.Add(dragged);
        }
        // already in waiting -> no-op (reordering not tracked)

        _draggingId = null;
        Notify();
    }

    private void RemovePlayerFromAll(Guid id)
    {
        // Remove from waiting pool
        Waiting.RemoveAll(p => p.Id == id);

        // Remove from any court slots
        foreach (var court in Courts)
            for (int i = 0; i < court.Slots.Length; i++)
                if (court.Slots[i]?.Id == id) court.Slots[i] = null;
    }

    private void PersistState()
    {
        // Persist players and assignments by clearing and re-inserting current state.
        // Collect all players
        var allPlayers = Waiting.Concat(Courts.SelectMany(c => c.Slots).Where(p => p is not null))
            .Cast<Player>()
            .DistinctBy(p => p.Id)
            .ToList();

        // Clear existing DB state
        _db.Assignments.RemoveRange(_db.Assignments);
        _db.Players.RemoveRange(_db.Players);
        _db.SaveChanges();

        // Insert players
        _db.Players.AddRange(allPlayers);
        _db.SaveChanges();

        // Insert assignments
        var assigns = new List<Assignment>();
        foreach (var court in Courts)
        for (int i = 0; i < court.Slots.Length; i++)
        {
            var p = court.Slots[i];
            if (p is not null)
                assigns.Add(new Assignment { CourtId = court.Id, SlotIndex = i, PlayerId = p.Id });
        }

        if (assigns.Any())
        {
            _db.Assignments.AddRange(assigns);
            _db.SaveChanges();
        }
    }

    private Player? FindPlayer(Guid id)
    {
        var fromWaiting = Waiting.FirstOrDefault(p => p.Id == id);
        if (fromWaiting is not null) return fromWaiting;

        foreach (var court in Courts)
            foreach (var slot in court.Slots)
                if (slot?.Id == id) return slot;

        return null;
    }

    private (Court? court, int index) FindCourtSlot(Player player)
    {
        foreach (var court in Courts)
            for (int i = 0; i < court.Slots.Length; i++)
                if (court.Slots[i]?.Id == player.Id) return (court, i);
        return (null, -1);
    }

    /// <summary>
    /// Swap two players' positions. Each player may be in a court slot or in the waiting pool.
    /// After this operation the two players will have exchanged places.
    /// </summary>
    public void SwapPlayers(Player a, Player b)
    {
        if (a is null || b is null) return;
        if (a.Id == b.Id) return;

        var (courtA, idxA) = FindCourtSlot(a);
        var (courtB, idxB) = FindCourtSlot(b);
        // Remove any duplicates/occurrences first
        RemovePlayerFromAll(a.Id);
        RemovePlayerFromAll(b.Id);

        // Place A into B's old position
        if (courtB is not null) courtB.Slots[idxB] = a;
        else { MarkPlayerWaiting(a); Waiting.Add(a); }

        // Place B into A's old position
        if (courtA is not null) courtA.Slots[idxA] = b;
        else { MarkPlayerWaiting(b); Waiting.Add(b); }

        Notify();
    }

    // ---------- Bulk actions ----------

    public void ShuffleAll()
    {
        var everyone = Waiting.Concat(Courts.SelectMany(c => c.Slots).Where(p => p is not null))
            .Cast<Player>()
            .OrderBy(_ => Random.Shared.Next())
            .ToList();

        Waiting.Clear();
        foreach (var court in Courts)
        {
            court.StartedAt = null;
            for (int i = 0; i < 4; i++)
                court.Slots[i] = null;
        }

        int courtIdx = 0, slotIdx = 0;
        foreach (var player in everyone)
        {
            if (courtIdx < Courts.Count)
            {
                Courts[courtIdx].Slots[slotIdx] = player;
                slotIdx++;
                if (slotIdx == 4) { slotIdx = 0; courtIdx++; }
            }
            else
            {
                MarkPlayerWaiting(player);
                Waiting.Add(player);
            }
        }

        Notify();
    }

    public void ClearCourt(Court court)
    {
        court.StartedAt = null;
        for (int i = 0; i < 4; i++)
        {
                if (court.Slots[i] is not null) { MarkPlayerWaiting(court.Slots[i]!); Waiting.Add(court.Slots[i]!); }
            court.Slots[i] = null;
        }
        Notify();
    }

    public void ClearAllCourts()
    {
        foreach (var court in Courts)
        {
            court.StartedAt = null;
            for (int i = 0; i < 4; i++)
            {
                if (court.Slots[i] is not null) { MarkPlayerWaiting(court.Slots[i]!); Waiting.Add(court.Slots[i]!); }
                court.Slots[i] = null;
            }
        }
        Notify();
    }

    public void ToggleTimer(Court court) => court.StartedAt = court.IsRunning ? null : DateTime.UtcNow;

    // ---------- Waiting management helpers ----------

    private void MarkPlayerWaiting(Player p)
    {
        if (p is null) return;
        p.WaitingSince = DateTime.UtcNow;
        p.IsPaused = false;
        p.PausedAt = null;
        p.PausedAccumulated = TimeSpan.Zero;
    }

    public void TogglePause(Player p)
    {
        if (p is null) return;
        // Only allow pause when player is in waiting pool
        if (!Waiting.Any(w => w.Id == p.Id)) return;

        if (p.IsPaused)
        {
            // unpause
            if (p.PausedAt is not null)
                p.PausedAccumulated += DateTime.UtcNow - p.PausedAt.Value;
            p.PausedAt = null;
            p.IsPaused = false;
        }
        else
        {
            // pause
            p.IsPaused = true;
            p.PausedAt = DateTime.UtcNow;
        }

        Notify();
    }

    private void Notify()
    {
        try
        {
            PersistState();
        }
        catch
        {
            // swallow persistence errors to avoid breaking UI; could log in real app
        }
        OnChange?.Invoke();
    }
}
