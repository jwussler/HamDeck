using System;
using System.Security.Cryptography;
using System.Text;
using HamDeck.Services;
using Xunit;

namespace HamDeck.Tests;

public class AuthServiceTests
{
    [Fact]
    public void HashAndVerify_RoundTrips()
    {
        var hash = AuthService.HashPassword("hunter2");
        Assert.StartsWith("pbkdf2:", hash);
        Assert.True(AuthService.VerifyPassword("hunter2", hash));
        Assert.False(AuthService.VerifyPassword("wrong", hash));
    }

    [Fact]
    public void Verify_AcceptsLegacySha256()
    {
        // Legacy stored hash = lowercase hex SHA256(password)
        var legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("oldpass"))).ToLower();
        Assert.True(AuthService.VerifyPassword("oldpass", legacy));
        Assert.False(AuthService.VerifyPassword("nope", legacy));
    }

    [Fact]
    public void Login_SucceedsWithCorrectPassword()
    {
        var auth = new AuthService(480);
        auth.AddUser("alice", AuthService.HashPassword("secret"));
        var token = auth.Login("alice", "secret");
        Assert.NotNull(token);
        Assert.True(auth.ValidateSession(token));
    }

    [Fact]
    public void Login_LocksOutAfterFiveFailures()
    {
        var auth = new AuthService(480);
        auth.AddUser("bob", AuthService.HashPassword("secret"));

        for (int i = 0; i < 5; i++)
            Assert.Null(auth.Login("bob", "wrong"));

        Assert.True(auth.IsLockedOut("bob"));
        // Even the CORRECT password is refused while locked out.
        Assert.Null(auth.Login("bob", "secret"));
    }

    [Fact]
    public void SuccessfulLogin_ClearsFailureWindow()
    {
        var auth = new AuthService(480);
        auth.AddUser("carol", AuthService.HashPassword("secret"));

        // 4 failures (one short of lockout), then a success should reset the counter.
        for (int i = 0; i < 4; i++) Assert.Null(auth.Login("carol", "wrong"));
        Assert.NotNull(auth.Login("carol", "secret"));
        Assert.False(auth.IsLockedOut("carol"));

        // A fresh failure must not immediately re-lock.
        Assert.Null(auth.Login("carol", "wrong"));
        Assert.False(auth.IsLockedOut("carol"));
    }

    [Fact]
    public void FirstUser_IsAlwaysAdmin()
    {
        var auth = new AuthService(480);
        auth.AddUser("first", AuthService.HashPassword("pw"), isAdmin: false);
        var token = auth.Login("first", "pw");
        Assert.True(auth.IsAdmin(token));
    }
}
