using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RallyBoard.Data;
using RallyBoard.Models;
using RallyBoard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// CreateBuilder already loads appsettings*.json — don't AddJsonFile again
// (extra reloadOnChange watchers blow the Linux inotify limit on small hosts).
var conn = builder.Configuration.GetConnectionString("DefaultConnection") ?? "postgresql://neondb_owner:npg_s6l7KMCoVLgq@ep-damp-night-abakelpg.eu-west-2.aws.neon.tech/neondb?sslmode=require";

// If a URI was provided, convert it to a key/value connection string that Npgsql can parse reliably
if (conn.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
{
    var uri = new Uri(conn);
    var userInfo = (uri.UserInfo ?? string.Empty).Split(':', 2);
    var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty;
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
    var database = uri.AbsolutePath?.TrimStart('/') ?? string.Empty;

    var npgBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Username = username,
        Password = password,
        Database = database,
        SslMode = SslMode.Require,
        TrustServerCertificate = true
    };

    conn = npgBuilder.ConnectionString;
}

// Register a DbContext factory so singletons can create short-lived DbContext instances
builder.Services.AddDbContextFactory<RallyBoardDbContext>(options => options.UseNpgsql(conn));

builder.Services.AddDbContext<RallyBoardDbContext>(options => options.UseNpgsql(conn));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));
builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddSingleton<MatchmakingSettingsService>();
builder.Services.AddSingleton<MatchmakingService>();
builder.Services.AddSingleton<SessionService>();
// Centralized CourtAllocationService used by UI components (use singleton so all components share one timer)
builder.Services.AddSingleton<CourtAllocationService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
