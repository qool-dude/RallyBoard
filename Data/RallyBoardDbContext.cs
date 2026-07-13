using Microsoft.EntityFrameworkCore;
using RallyBoard.Models;

namespace RallyBoard.Data;

public class RallyBoardDbContext : DbContext
{
    public RallyBoardDbContext(DbContextOptions<RallyBoardDbContext> options) : base(options) { }

    public DbSet<Player> Players { get; set; } = null!;
    public DbSet<Assignment> Assignments { get; set; } = null!;
    public DbSet<Session> Sessions { get; set; } = null!;
    public DbSet<SessionAttendance> SessionAttendances { get; set; } = null!;
    public DbSet<Game> Games { get; set; } = null!;
    public DbSet<GamePlayer> GamePlayers { get; set; } = null!;
    public DbSet<MatchmakingExplanation> MatchmakingExplanations { get; set; } = null!;
    public DbSet<AppSetting> AppSettings { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Player>().HasKey(p => p.Id);

        modelBuilder.Entity<Assignment>().HasKey(a => a.Id);

        modelBuilder.Entity<AppSetting>().HasKey(s => s.Key);

        modelBuilder.Entity<Session>().HasKey(s => s.Id);
        modelBuilder.Entity<Session>()
            .HasIndex(s => s.Date);

        modelBuilder.Entity<SessionAttendance>().HasKey(a => a.Id);
        modelBuilder.Entity<SessionAttendance>()
            .HasIndex(a => new { a.SessionId, a.PlayerId })
            .IsUnique();
        modelBuilder.Entity<SessionAttendance>()
            .HasOne(a => a.Session)
            .WithMany(s => s.Attendances)
            .HasForeignKey(a => a.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SessionAttendance>()
            .HasOne(a => a.Player)
            .WithMany()
            .HasForeignKey(a => a.PlayerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Game>().HasKey(g => g.Id);
        modelBuilder.Entity<Game>()
            .HasOne(g => g.Session)
            .WithMany(s => s.Games)
            .HasForeignKey(g => g.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Game>()
            .HasIndex(g => g.SessionId);

        modelBuilder.Entity<GamePlayer>().HasKey(gp => gp.Id);
        modelBuilder.Entity<GamePlayer>()
            .HasOne(gp => gp.Game)
            .WithMany(g => g.Players)
            .HasForeignKey(gp => gp.GameId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GamePlayer>()
            .HasOne(gp => gp.Player)
            .WithMany()
            .HasForeignKey(gp => gp.PlayerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<MatchmakingExplanation>().HasKey(m => m.Id);
        modelBuilder.Entity<MatchmakingExplanation>()
            .HasOne(m => m.Session)
            .WithMany()
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<MatchmakingExplanation>()
            .HasOne(m => m.Game)
            .WithMany()
            .HasForeignKey(m => m.GameId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<MatchmakingExplanation>()
            .HasIndex(m => m.SessionId);
        modelBuilder.Entity<MatchmakingExplanation>()
            .HasIndex(m => m.GameId);

        base.OnModelCreating(modelBuilder);
    }
}
