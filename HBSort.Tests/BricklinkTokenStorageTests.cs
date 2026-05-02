using System.Security.Cryptography;
using HBSort.Core.Models;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den BricklinkTokenStorage.
/// Round-Trip Save->Load ueber DPAPI + ein Negativtest fuer beschaedigte Daten.
/// </summary>
public class BricklinkTokenStorageTests
{
    [Fact]
    public async Task Save_then_load_round_trip_returns_same_tokens()
    {
        var settings = new InMemorySettingsService();
        var sut = new BricklinkTokenStorage(settings);

        var input = new BricklinkTokens
        {
            ConsumerKey = "ck-12345",
            ConsumerSecret = "cs-67890",
            TokenValue = "tv-abcdef",
            TokenSecret = "ts-ghijkl"
        };

        await sut.SaveAsync(input);

        // Storage hat das Blob in settings.Current.Bricklink.TokensEncrypted abgelegt
        Assert.False(string.IsNullOrEmpty(settings.Current.Bricklink.TokensEncrypted));
        Assert.True(sut.HasTokens());

        var loaded = await sut.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal("ck-12345", loaded!.ConsumerKey);
        Assert.Equal("cs-67890", loaded.ConsumerSecret);
        Assert.Equal("tv-abcdef", loaded.TokenValue);
        Assert.Equal("ts-ghijkl", loaded.TokenSecret);
        Assert.True(loaded.IsComplete);
    }

    [Fact]
    public async Task Load_returns_null_when_no_tokens_stored()
    {
        var settings = new InMemorySettingsService();
        var sut = new BricklinkTokenStorage(settings);

        Assert.False(sut.HasTokens());
        var loaded = await sut.LoadAsync();
        Assert.Null(loaded);
    }

    [Fact]
    public async Task Clear_removes_tokens_from_settings()
    {
        var settings = new InMemorySettingsService();
        var sut = new BricklinkTokenStorage(settings);

        await sut.SaveAsync(new BricklinkTokens
        {
            ConsumerKey = "x", ConsumerSecret = "x", TokenValue = "x", TokenSecret = "x"
        });
        Assert.True(sut.HasTokens());

        await sut.ClearAsync();
        Assert.False(sut.HasTokens());
        Assert.Equal(string.Empty, settings.Current.Bricklink.TokensEncrypted);
    }

    [Fact]
    public async Task Load_throws_CryptographicException_for_invalid_blob()
    {
        var settings = new InMemorySettingsService();
        // Kein gueltiges DPAPI-Blob – nur garbage Base64
        settings.Current.Bricklink.TokensEncrypted = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });

        var sut = new BricklinkTokenStorage(settings);

        await Assert.ThrowsAsync<CryptographicException>(async () => await sut.LoadAsync());
    }

    [Fact]
    public async Task Load_throws_for_invalid_base64()
    {
        var settings = new InMemorySettingsService();
        settings.Current.Bricklink.TokensEncrypted = "not-base64!@#";

        var sut = new BricklinkTokenStorage(settings);

        await Assert.ThrowsAsync<CryptographicException>(async () => await sut.LoadAsync());
    }

    /// <summary>Minimaler ISettingsService-Stub mit In-Memory-AppSettings.</summary>
    private sealed class InMemorySettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }
}
