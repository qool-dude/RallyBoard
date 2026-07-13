using Microsoft.Extensions.Options;
using RallyBoard.Models;

namespace RallyBoard.Services;

/// <summary>
/// Per-circuit admin gate. Scoped so each browser session has its own login state.
/// </summary>
public class AdminService
{
    private readonly string _password;

    public AdminService(IOptions<AdminOptions> options)
    {
        _password = options.Value.Password ?? "stanway123";
    }

    public bool IsAdmin { get; private set; }

    public event Action? OnChange;

    public bool TryLogin(string? password)
    {
        if (string.IsNullOrEmpty(password) || password != _password)
            return false;

        if (IsAdmin) return true;
        IsAdmin = true;
        OnChange?.Invoke();
        return true;
    }

    public void Logout()
    {
        if (!IsAdmin) return;
        IsAdmin = false;
        OnChange?.Invoke();
    }
}
