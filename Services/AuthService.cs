using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HamDeck.Services;

/// <summary>
/// Multi-user session-based authentication for the web control dashboard.
/// Supports admin role, user management, session tracking, and per-user TX permission.
/// </summary>
public class AuthService
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, UserInfo> _users = new();
    private readonly int _sessionTimeoutMinutes;

    public bool IsConfigured => _users.Count > 0;

    public AuthService(int sessionTimeoutMinutes = 480)
    {
        _sessionTimeoutMinutes = sessionTimeoutMinutes;
    }

    /// <summary>Add a user. First user added is always admin.</summary>
    public void AddUser(string username, string passwordHash, bool isAdmin = false, bool canTransmit = true)
    {
        var key = username.Trim().ToLower();
        var firstUser = _users.IsEmpty;
        _users[key] = new UserInfo
        {
            Username = key,
            PasswordHash = passwordHash,
            IsAdmin = isAdmin || firstUser,
            CanTransmit = canTransmit,
            CreatedAt = DateTime.UtcNow
        };
        Logger.Info("AUTH", "User '{0}' added (admin={1}, can_transmit={2})", key, isAdmin || firstUser, canTransmit);
    }

    /// <summary>Remove a user and kill their sessions.</summary>
    public bool RemoveUser(string username)
    {
        var key = username.Trim().ToLower();
        if (!_users.TryRemove(key, out _)) return false;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.Username == key)
                _sessions.TryRemove(kvp.Key, out _);
        }
        Logger.Info("AUTH", "User '{0}' removed", key);
        return true;
    }

    /// <summary>Change a user's password.</summary>
    public bool ChangePassword(string username, string newPasswordHash)
    {
        var key = username.Trim().ToLower();
        if (!_users.TryGetValue(key, out var user)) return false;
        user.PasswordHash = newPasswordHash;
        Logger.Info("AUTH", "Password changed for user '{0}'", key);
        return true;
    }

    /// <summary>Set whether a user is allowed to transmit. Also updates any live sessions.</summary>
    public bool SetUserCanTransmit(string username, bool canTransmit)
    {
        var key = username.Trim().ToLower();
        if (!_users.TryGetValue(key, out var user)) return false;
        user.CanTransmit = canTransmit;

        // Update any live sessions immediately
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.Username == key)
                kvp.Value.CanTransmit = canTransmit;
        }

        Logger.Info("AUTH", "User '{0}' can_transmit set to {1}", key, canTransmit);
        return true;
    }

    /// <summary>Login and create session. Returns token or null.</summary>
    public string? Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        var key = username.Trim().ToLower();
        if (!_users.TryGetValue(key, out var user))
            return null;

        if (HashPassword(password) != user.PasswordHash)
            return null;

        PurgeExpired();

        var token = GenerateToken();
        _sessions[token] = new SessionInfo
        {
            Username = key,
            IsAdmin = user.IsAdmin,
            CanTransmit = user.CanTransmit,
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };

        Logger.Info("AUTH", "Login successful for user '{0}' (admin={1}, can_transmit={2})", key, user.IsAdmin, user.CanTransmit);
        return token;
    }

    /// <summary>Validate session token. Updates last activity.</summary>
    public bool ValidateSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_sessions.TryGetValue(token, out var session)) return false;

        if (DateTime.UtcNow - session.LastActivity > TimeSpan.FromMinutes(_sessionTimeoutMinutes))
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        session.LastActivity = DateTime.UtcNow;
        return true;
    }

    /// <summary>Check if a session belongs to an admin user.</summary>
    public bool IsAdmin(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return _sessions.TryGetValue(token, out var s) && s.IsAdmin;
    }

    /// <summary>Check if a session is allowed to transmit.</summary>
    public bool CanTransmit(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        return _sessions.TryGetValue(token, out var s) && s.CanTransmit;
    }

    /// <summary>Get username for a session token.</summary>
    public string? GetUsername(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        return _sessions.TryGetValue(token, out var s) ? s.Username : null;
    }

    /// <summary>Destroy a session (logout).</summary>
    public void Logout(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token) && _sessions.TryRemove(token, out var session))
            Logger.Info("AUTH", "Logout for user '{0}'", session.Username);
    }

    /// <summary>Kill all sessions for a specific user (admin force-disconnect).</summary>
    public int KillUserSessions(string username)
    {
        var key = username.Trim().ToLower();
        int count = 0;
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.Username == key)
            {
                _sessions.TryRemove(kvp.Key, out _);
                count++;
            }
        }
        if (count > 0) Logger.Info("AUTH", "Killed {0} sessions for user '{1}'", count, key);
        return count;
    }

    /// <summary>Get all active sessions (for admin panel).</summary>
    public List<object> GetActiveSessions()
    {
        PurgeExpired();
        return _sessions.Select(kvp => (object)new
        {
            token_short = kvp.Key[..8] + "...",
            username = kvp.Value.Username,
            is_admin = kvp.Value.IsAdmin,
            can_transmit = kvp.Value.CanTransmit,
            created = kvp.Value.CreatedAt.ToString("o"),
            last_activity = kvp.Value.LastActivity.ToString("o"),
            idle_seconds = (int)(DateTime.UtcNow - kvp.Value.LastActivity).TotalSeconds
        }).ToList();
    }

    /// <summary>Get all registered users (for admin panel).</summary>
    public List<object> GetUsers()
    {
        return _users.Select(kvp => (object)new
        {
            username = kvp.Value.Username,
            is_admin = kvp.Value.IsAdmin,
            can_transmit = kvp.Value.CanTransmit,
            created = kvp.Value.CreatedAt.ToString("o")
        }).ToList();
    }

    public int ActiveSessionCount { get { PurgeExpired(); return _sessions.Count; } }

    public static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes).ToLower();
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLower();
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(_sessionTimeoutMinutes);
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastActivity < cutoff)
                _sessions.TryRemove(kvp.Key, out _);
        }
    }

    private class SessionInfo
    {
        public string Username { get; set; } = "";
        public bool IsAdmin { get; set; }
        public bool CanTransmit { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivity { get; set; }
    }

    private class UserInfo
    {
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public bool IsAdmin { get; set; }
        public bool CanTransmit { get; set; } = true;
        public DateTime CreatedAt { get; set; }
    }
}
