using HBSort.Core.Models;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den BricklinkClient – Phase R1.
/// Wir testen nur das Verhalten mit/ohne Tokens; ein echter API-Roundtrip
/// ist nicht testbar weil OAuth1 + IP-Whitelist + Rate-Limit involviert sind.
/// Den TestConnection-Endpunkt verifizieren wir manuell beim User-Test.
/// </summary>
public class BricklinkClientPhase1Tests
{
    [Fact]
    public async Task IsConfigured_false_when_no_tokens()
    {
        var storage = new FakeStorage();
        var sut = new BricklinkClient(storage);

        Assert.False(await sut.IsConfiguredAsync());
    }

    [Fact]
    public async Task IsConfigured_true_when_complete_tokens()
    {
        var storage = new FakeStorage
        {
            Tokens = new BricklinkTokens
            {
                ConsumerKey = "ck", ConsumerSecret = "cs",
                TokenValue = "tv", TokenSecret = "ts"
            }
        };
        var sut = new BricklinkClient(storage);

        Assert.True(await sut.IsConfiguredAsync());
    }

    [Fact]
    public async Task IsConfigured_false_when_tokens_incomplete()
    {
        var storage = new FakeStorage
        {
            Tokens = new BricklinkTokens
            {
                ConsumerKey = "ck", ConsumerSecret = "cs",
                TokenValue = "", TokenSecret = "ts"
            }
        };
        var sut = new BricklinkClient(storage);

        Assert.False(await sut.IsConfiguredAsync());
    }

    [Fact]
    public async Task TestConnection_without_tokens_returns_failure()
    {
        var storage = new FakeStorage();
        var sut = new BricklinkClient(storage);

        var result = await sut.TestConnectionAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Tokens", result.ErrorMessage!);
    }

    [Fact]
    public async Task GetItem_throws_BricklinkAuthException_when_no_tokens()
    {
        // Phase R2: GetItem ist implementiert. Ohne Tokens muss
        // BricklinkAuthException kommen, NICHT NotImplementedException.
        var sut = new BricklinkClient(new FakeStorage());
        await Assert.ThrowsAsync<HBSort.Core.Models.Exceptions.BricklinkAuthException>(
            async () => await sut.GetItemAsync("M", "arc007"));
    }

    [Fact]
    public async Task GetSubsets_throws_BricklinkAuthException_when_no_tokens()
    {
        var sut = new BricklinkClient(new FakeStorage());
        await Assert.ThrowsAsync<HBSort.Core.Models.Exceptions.BricklinkAuthException>(
            async () => await sut.GetSubsetsAsync("M", "arc007", breakMinifigs: false));
    }

    /// <summary>Fake-Storage fuer die Phase-R1-Tests (ohne DPAPI-Roundtrip).</summary>
    private sealed class FakeStorage : IBricklinkTokenStorage
    {
        public BricklinkTokens? Tokens { get; set; }

        public bool HasTokens() => Tokens != null;
        public Task<BricklinkTokens?> LoadAsync() => Task.FromResult(Tokens);
        public Task SaveAsync(BricklinkTokens tokens) { Tokens = tokens; return Task.CompletedTask; }
        public Task ClearAsync() { Tokens = null; return Task.CompletedTask; }
    }
}
