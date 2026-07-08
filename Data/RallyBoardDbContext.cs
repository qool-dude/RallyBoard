using Microsoft.EntityFrameworkCore;
using RallyBoard.Models;

namespace RallyBoard.Data;

public class RallyBoardDbContext : DbContext
{
    public RallyBoardDbContext(DbContextOptions<RallyBoardDbContext> options) : base(options) { }

    public DbSet<Player> Players { get; set; } = null!;
    public DbSet<Assignment> Assignments { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Player>().HasKey(p => p.Id);
        modelBuilder.Entity<Assignment>().HasKey(a => a.Id);
        base.OnModelCreating(modelBuilder);
    }
}
