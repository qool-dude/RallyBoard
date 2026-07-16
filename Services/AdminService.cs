using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.Options;
using RallyBoard.Models;

namespace RallyBoard.Services;

public class AdminService
{
    private const string StorageKey = "admin_session";

    private readonly ProtectedLocalStorage _storage;
    private readonly AdminOptions _options;

    private bool _isAdmin;
    private bool _restored;
    private DateTimeOffset _lastActivity;

    public AdminService(ProtectedLocalStorage storage, IOptions<AdminOptions> options)
    {
        _storage = storage;
        _options = options.Value;
    }

    public bool IsAdmin => _isAdmin;

    public event Action? OnChange;

    public TimeSpan InactivityTimeout =>
        TimeSpan.FromMinutes(_options.InactivityMinutes > 0 ? _options.InactivityMinutes : 30);

    public async Task RestoreAsync()
    {
        if (_restored)
            return;

        try
        {
            var result = await _storage.GetAsync<long>(StorageKey);
            _restored = true;

            if (!result.Success)
                return;

            var lastActivity = DateTimeOffset.FromUnixTimeSeconds(result.Value);
            if (DateTimeOffset.UtcNow - lastActivity > InactivityTimeout)
            {
                await ClearStorageAsync();
                return;
            }

            _isAdmin = true;
            _lastActivity = lastActivity;
        }
        catch
        {
            // Storage unavailable during prerender — retry after first render
        }
    }

    public async Task<bool> TryLoginAsync(string? password)
    {
        if (string.IsNullOrEmpty(password) || password != _options.Password)
            return false;

        await SetAdminSessionAsync();
        return true;
    }

    public async Task LogoutAsync()
    {
        if (!_isAdmin)
            return;

        _isAdmin = false;
        _lastActivity = default;
        await ClearStorageAsync();
        OnChange?.Invoke();
    }

    public async Task TouchAsync()
    {
        if (!_isAdmin)
            return;

        if (DateTimeOffset.UtcNow - _lastActivity > InactivityTimeout)
        {
            await LogoutAsync();
            return;
        }

        if (DateTimeOffset.UtcNow - _lastActivity < TimeSpan.FromSeconds(30))
            return;

        await PersistSessionAsync();
    }

    public async Task EnsureValidAsync()
    {
        await RestoreAsync();

        if (!_isAdmin)
            return;

        if (DateTimeOffset.UtcNow - _lastActivity > InactivityTimeout)
            await LogoutAsync();
    }

    private async Task SetAdminSessionAsync()
    {
        _isAdmin = true;
        await PersistSessionAsync();
        OnChange?.Invoke();
    }

    private async Task PersistSessionAsync()
    {
        _lastActivity = DateTimeOffset.UtcNow;
        try
        {
            await _storage.SetAsync(StorageKey, _lastActivity.ToUnixTimeSeconds());
        }
        catch
        {
            // Storage unavailable during prerender
        }
    }

    private async Task ClearStorageAsync()
    {
        try
        {
            await _storage.DeleteAsync(StorageKey);
        }
        catch
        {
            // Storage unavailable during prerender
        }
    }
}
