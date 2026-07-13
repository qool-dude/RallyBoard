using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RallyBoard.Data;
using RallyBoard.Models;

namespace RallyBoard.Services;

/// <summary>
/// Live matchmaking options: seeded from appsettings, overlaid from DB, mutable at runtime.
/// </summary>
public class MatchmakingSettingsService
{
    public const string SettingsKey = "Matchmaking";
    public const string ProfilesKey = "Matchmaking.Profiles";
    public const string ActiveProfileKey = "Matchmaking.ActiveProfileId";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IDbContextFactory<RallyBoardDbContext> _dbFactory;
    private readonly MatchmakingOptions _live;
    private readonly MatchmakingOptions _defaults;
    private readonly List<MatchmakingProfile> _profiles = new();
    private Guid? _activeProfileId;
    private readonly object _gate = new();

    public event Action? OnChange;

    public MatchmakingSettingsService(
        IDbContextFactory<RallyBoardDbContext> dbFactory,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _defaults = new MatchmakingOptions();
        configuration.GetSection(MatchmakingOptions.SectionName).Bind(_defaults);
        _live = Clone(_defaults);

        try
        {
            using var db = _dbFactory.CreateDbContext();
            DatabaseInitializer.EnsureSchema(db);
            LoadFromDb(db);
            LoadProfilesFromDb(db);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MatchmakingSettingsService load error: {ex.Message}");
        }
    }

    /// <summary>Shared live instance used by matchmaking / ratings.</summary>
    public MatchmakingOptions Current => _live;

    public Guid? ActiveProfileId
    {
        get { lock (_gate) return _activeProfileId; }
    }

    public string? ActiveProfileName
    {
        get
        {
            lock (_gate)
            {
                if (_activeProfileId is null) return null;
                return _profiles.FirstOrDefault(p => p.Id == _activeProfileId)?.Name;
            }
        }
    }

    public MatchmakingOptions GetSnapshot()
    {
        lock (_gate) return Clone(_live);
    }

    public MatchmakingOptions GetDefaults() => Clone(_defaults);

    public List<MatchmakingProfile> ListProfiles()
    {
        lock (_gate)
        {
            return _profiles
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneProfile)
                .ToList();
        }
    }

    public void Apply(MatchmakingOptions next, Guid? asProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        Normalize(next);

        lock (_gate)
        {
            Copy(next, _live);
            _activeProfileId = asProfileId;
            PersistUnlocked();
            PersistActiveProfileUnlocked();
        }

        OnChange?.Invoke();
    }

    public void ResetToDefaults() => Apply(Clone(_defaults), asProfileId: null);

    public MatchmakingProfile SaveProfile(string name, MatchmakingOptions options, Guid? existingId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Profile name is required.", nameof(name));

        ArgumentNullException.ThrowIfNull(options);
        var trimmed = name.Trim();
        Normalize(options);

        MatchmakingProfile saved;
        lock (_gate)
        {
            MatchmakingProfile? profile = null;
            if (existingId is Guid id)
                profile = _profiles.FirstOrDefault(p => p.Id == id);

            profile ??= _profiles.FirstOrDefault(p =>
                p.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));

            if (profile is null)
            {
                profile = new MatchmakingProfile
                {
                    Id = Guid.NewGuid(),
                    Name = trimmed,
                    Options = Clone(options),
                    UpdatedAt = DateTime.UtcNow
                };
                _profiles.Add(profile);
            }
            else
            {
                profile.Name = trimmed;
                profile.Options = Clone(options);
                profile.UpdatedAt = DateTime.UtcNow;
            }

            PersistProfilesUnlocked();
            saved = CloneProfile(profile);
        }

        OnChange?.Invoke();
        return saved;
    }

    public void ApplyProfile(Guid profileId)
    {
        MatchmakingOptions options;
        lock (_gate)
        {
            var profile = _profiles.FirstOrDefault(p => p.Id == profileId)
                ?? throw new InvalidOperationException("Profile not found.");
            options = Clone(profile.Options);
        }

        Apply(options, asProfileId: profileId);
    }

    public void DeleteProfile(Guid profileId)
    {
        lock (_gate)
        {
            var removed = _profiles.RemoveAll(p => p.Id == profileId);
            if (removed == 0) return;

            if (_activeProfileId == profileId)
                _activeProfileId = null;

            PersistProfilesUnlocked();
            PersistActiveProfileUnlocked();
        }

        OnChange?.Invoke();
    }

    private void LoadFromDb(RallyBoardDbContext db)
    {
        var row = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == SettingsKey);
        if (row is null || string.IsNullOrWhiteSpace(row.JsonValue))
            return;

        var stored = JsonSerializer.Deserialize<MatchmakingOptions>(row.JsonValue, JsonOptions);
        if (stored is null) return;

        Normalize(stored);
        Copy(stored, _live);
    }

    private void LoadProfilesFromDb(RallyBoardDbContext db)
    {
        var profilesRow = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == ProfilesKey);
        if (profilesRow is not null && !string.IsNullOrWhiteSpace(profilesRow.JsonValue))
        {
            var list = JsonSerializer.Deserialize<List<MatchmakingProfile>>(profilesRow.JsonValue, JsonOptions);
            if (list is not null)
            {
                _profiles.Clear();
                foreach (var p in list)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) continue;
                    p.Options ??= new MatchmakingOptions();
                    Normalize(p.Options);
                    if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();
                    _profiles.Add(p);
                }
            }
        }

        var activeRow = db.AppSettings.AsNoTracking().FirstOrDefault(s => s.Key == ActiveProfileKey);
        if (activeRow is not null
            && Guid.TryParse(activeRow.JsonValue.Trim('"'), out var activeId)
            && _profiles.Any(p => p.Id == activeId))
        {
            _activeProfileId = activeId;
        }
    }

    private void PersistUnlocked()
    {
        using var db = _dbFactory.CreateDbContext();
        DatabaseInitializer.EnsureSchema(db);
        UpsertSetting(db, SettingsKey, JsonSerializer.Serialize(_live, JsonOptions));
        db.SaveChanges();
    }

    private void PersistProfilesUnlocked()
    {
        using var db = _dbFactory.CreateDbContext();
        DatabaseInitializer.EnsureSchema(db);
        UpsertSetting(db, ProfilesKey, JsonSerializer.Serialize(_profiles, JsonOptions));
        db.SaveChanges();
    }

    private void PersistActiveProfileUnlocked()
    {
        using var db = _dbFactory.CreateDbContext();
        DatabaseInitializer.EnsureSchema(db);
        var value = _activeProfileId is Guid id ? id.ToString() : "";
        UpsertSetting(db, ActiveProfileKey, value);
        db.SaveChanges();
    }

    private static void UpsertSetting(RallyBoardDbContext db, string key, string json)
    {
        var row = db.AppSettings.FirstOrDefault(s => s.Key == key);
        if (row is null)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = key,
                JsonValue = json,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            row.JsonValue = json;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }

    private static MatchmakingProfile CloneProfile(MatchmakingProfile source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Options = Clone(source.Options),
        UpdatedAt = source.UpdatedAt
    };

    private static void Normalize(MatchmakingOptions o)
    {
        o.Selection ??= new SelectionWeights();
        o.Selection.Balanced ??= new AlgorithmWeights();
        o.Selection.Ability ??= new AlgorithmWeights();
        o.Rating ??= new RatingWeights();

        o.Selection.AbilityAlgorithmChance = Clamp(o.Selection.AbilityAlgorithmChance, 0, 1);
        o.Selection.TopPlayerPercentile = Clamp(o.Selection.TopPlayerPercentile, 0.05, 1);
        o.Selection.TopClusterBonus = Clamp(o.Selection.TopClusterBonus, 0, 100);
        o.Selection.RecentGamesLookback = Math.Clamp(o.Selection.RecentGamesLookback, 1, 40);
        o.Selection.Randomness = Clamp(o.Selection.Randomness, 0, 1);

        NormalizeWeights(o.Selection.Balanced);
        NormalizeWeights(o.Selection.Ability);

        o.Rating.WinRateWeight = Math.Max(0, o.Rating.WinRateWeight);
        o.Rating.GamesPlayedWeight = Math.Max(0, o.Rating.GamesPlayedWeight);
        o.Rating.ClosenessWeight = Math.Max(0, o.Rating.ClosenessWeight);
        o.Rating.GamesPlayedCap = Math.Clamp(o.Rating.GamesPlayedCap, 1, 100);
        o.Rating.DefaultRating = Clamp(o.Rating.DefaultRating, 0, 100);
        o.Rating.RecentSessionsWindow = Math.Clamp(o.Rating.RecentSessionsWindow, 1, 50);
        o.Rating.RecentSessionMultiplier = Clamp(o.Rating.RecentSessionMultiplier, 1, 20);
    }

    private static void NormalizeWeights(AlgorithmWeights w)
    {
        w.WaitingWeight = Math.Max(0, w.WaitingWeight);
        w.MixingWeight = Math.Max(0, w.MixingWeight);
        w.BalanceWeight = Math.Max(0, w.BalanceWeight);
        w.PeerWeight = Math.Max(0, w.PeerWeight);
        w.HomogeneityWeight = Math.Max(0, w.HomogeneityWeight);
    }

    private static double Clamp(double v, double min, double max) =>
        Math.Clamp(v, min, max);

    private static MatchmakingOptions Clone(MatchmakingOptions source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<MatchmakingOptions>(json, JsonOptions) ?? new MatchmakingOptions();
    }

    private static void Copy(MatchmakingOptions from, MatchmakingOptions to)
    {
        to.Rating ??= new RatingWeights();
        to.Selection ??= new SelectionWeights();
        to.Selection.Balanced ??= new AlgorithmWeights();
        to.Selection.Ability ??= new AlgorithmWeights();

        to.Rating.WinRateWeight = from.Rating.WinRateWeight;
        to.Rating.GamesPlayedWeight = from.Rating.GamesPlayedWeight;
        to.Rating.ClosenessWeight = from.Rating.ClosenessWeight;
        to.Rating.GamesPlayedCap = from.Rating.GamesPlayedCap;
        to.Rating.DefaultRating = from.Rating.DefaultRating;
        to.Rating.RecentSessionsWindow = from.Rating.RecentSessionsWindow;
        to.Rating.RecentSessionMultiplier = from.Rating.RecentSessionMultiplier;

        to.Selection.AbilityAlgorithmChance = from.Selection.AbilityAlgorithmChance;
        to.Selection.TopPlayerPercentile = from.Selection.TopPlayerPercentile;
        to.Selection.TopClusterBonus = from.Selection.TopClusterBonus;
        to.Selection.RecentGamesLookback = from.Selection.RecentGamesLookback;
        to.Selection.Randomness = from.Selection.Randomness;

        CopyWeights(from.Selection.Balanced, to.Selection.Balanced);
        CopyWeights(from.Selection.Ability, to.Selection.Ability);
    }

    private static void CopyWeights(AlgorithmWeights from, AlgorithmWeights to)
    {
        to.WaitingWeight = from.WaitingWeight;
        to.MixingWeight = from.MixingWeight;
        to.BalanceWeight = from.BalanceWeight;
        to.PeerWeight = from.PeerWeight;
        to.HomogeneityWeight = from.HomogeneityWeight;
    }
}
