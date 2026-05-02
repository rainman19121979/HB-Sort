using HBSort.Core.Models;
using HBSort.Core.Models.Bricklink;
using HBSort.Core.Services;

namespace HBSort.Tests;

/// <summary>
/// Tests fuer den BricklinkRateLimiter mit echtem BlCacheRepository in einer
/// Test-SQLite-Datei. So testen wir auch die SQL-Counter-Queries.
/// </summary>
public class BricklinkRateLimiterTests : IDisposable
{
    private readonly string _testDir = Path.Combine(
        Path.GetTempPath(), $"lego-ratelimiter-tests-{Guid.NewGuid():N}");
    private readonly BlCacheRepository _repo;
    private readonly InMemorySettings _settings = new();
    private readonly BricklinkRateLimiter _sut;

    public BricklinkRateLimiterTests()
    {
        Directory.CreateDirectory(_testDir);
        _repo = new BlCacheRepository(Path.Combine(_testDir, "bl_cache.db"));
        _sut = new BricklinkRateLimiter(_repo, _settings);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task LogCall_increments_24h_counter()
    {
        await _sut.LogCallAsync("GetItem", "M", "arc007", 250, 200, true);
        await _sut.LogCallAsync("GetItem", "M", "arc008", 220, 200, true);

        var status = await _sut.GetStatusAsync();
        Assert.Equal(2, status.CallsLast24h);
    }

    [Fact]
    public async Task GetStatus_returns_thresholds_from_settings()
    {
        _settings.Current.Bricklink.SoftThreshold = 100;
        _settings.Current.Bricklink.HardThreshold = 500;

        var status = await _sut.GetStatusAsync();

        Assert.Equal(100, status.SoftThreshold);
        Assert.Equal(500, status.HardThreshold);
        Assert.Equal(BricklinkConstants.BlRealLimit, status.BlRealLimit);
    }

    [Theory]
    [InlineData(0, RateLimitState.Ok)]
    [InlineData(999, RateLimitState.Ok)]
    [InlineData(1000, RateLimitState.Warning)]   // genau am Soft -> Warning
    [InlineData(1001, RateLimitState.Warning)]
    [InlineData(4499, RateLimitState.Warning)]
    [InlineData(4500, RateLimitState.Blocked)]   // genau am Hard -> Blocked
    [InlineData(4900, RateLimitState.Blocked)]
    public void ComputeState_at_boundaries(int calls, RateLimitState expected)
    {
        // Verwendet die statische Funktion direkt – keine DB noetig.
        var state = BricklinkRateLimiter.ComputeState(calls, softThreshold: 1000, hardThreshold: 4500);
        Assert.Equal(expected, state);
    }

    [Fact]
    public void ComputeState_critical_when_above_95_percent_of_bl_limit()
    {
        // Critical = ab 95% von 5000 = 4750. Hard ist hier hoeher gesetzt damit
        // wir den Critical-Bereich ueberhaupt erreichen koennen.
        var state = BricklinkRateLimiter.ComputeState(callsLast24h: 4800,
            softThreshold: 1000, hardThreshold: 4900);
        Assert.Equal(RateLimitState.Critical, state);
    }

    [Fact]
    public async Task CanMakeCall_false_when_blocked()
    {
        _settings.Current.Bricklink.HardThreshold = 3;
        // 3 Calls einsetzen
        for (int i = 0; i < 3; i++)
        {
            await _sut.LogCallAsync("GetItem", "P", $"part{i}", 100, 200, true);
        }

        var status = await _sut.GetStatusAsync();
        Assert.Equal(RateLimitState.Blocked, status.State);
        Assert.False(await _sut.CanMakeCallAsync());
    }

    [Fact]
    public async Task CanMakeCall_true_when_not_yet_blocked()
    {
        _settings.Current.Bricklink.HardThreshold = 3;
        await _sut.LogCallAsync("GetItem", "P", "1", 100, 200, true);
        Assert.True(await _sut.CanMakeCallAsync());
    }

    [Fact]
    public async Task StatusChanged_event_fires_on_log_call()
    {
        var received = new List<RateLimitStatus>();
        _sut.StatusChanged += (_, s) => received.Add(s);

        await _sut.LogCallAsync("GetItem", "M", "arc007", 100, 200, true);

        Assert.Single(received);
        Assert.Equal(1, received[0].CallsLast24h);
    }

    [Fact]
    public async Task PruneOld_removes_entries_older_than_7_days()
    {
        // Direkt im Repo einen alten Eintrag schreiben (Bypass des Limiters,
        // damit wir den Timestamp manuell setzen koennen).
        await InsertOldCallAsync(daysAgo: 10);
        await _sut.LogCallAsync("GetItem", "P", "fresh", 100, 200, true);

        var beforePrune = await _sut.GetStatusAsync();
        // Window24h sieht nur 1 (frisch); Pre-Prune im DB sind aber 2 Eintraege.

        await _sut.PruneOldEntriesAsync();

        // Nach Prune ist auch in der Tabelle nur noch der frische Eintrag.
        // Wir testen das indirekt ueber ein anderes 30-Tage-Window:
        var allWithin30 = await _repo.GetCallCountInWindowAsync(TimeSpan.FromDays(30));
        Assert.Equal(1, allWithin30);
    }

    [Fact]
    public async Task Rolling_window_excludes_calls_older_than_24h()
    {
        await InsertOldCallAsync(hoursAgo: 25);  // ausserhalb
        await InsertOldCallAsync(hoursAgo: 12);  // innerhalb
        await _sut.LogCallAsync("GetItem", "P", "now", 100, 200, true);

        var status = await _sut.GetStatusAsync();
        Assert.Equal(2, status.CallsLast24h); // nur die 12h-alte + die neue
    }

    /// <summary>Schreibt direkt einen alten Call-Eintrag mit gestelltem Timestamp.</summary>
    private async Task InsertOldCallAsync(double? daysAgo = null, double? hoursAgo = null)
    {
        // Kleiner Hack: wir nutzen die echte LogApiCall-Methode + UPDATE direkt in der DB,
        // weil die Default-LogApiCallAsync-Methode immer "jetzt" als Timestamp setzt.
        // Stattdessen fuegen wir einen Eintrag mit kuenstlichem Timestamp via SQL ein.
        var ts = DateTime.UtcNow;
        if (daysAgo.HasValue) ts = ts.AddDays(-daysAgo.Value);
        if (hoursAgo.HasValue) ts = ts.AddHours(-hoursAgo.Value);

        // Wir nutzen die LogApi + manuelle SQL-Update Methode hier nicht – stattdessen direkter Insert.
        var connStr = $"Data Source={Path.Combine(_testDir, "bl_cache.db")}";
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO api_call_log
            (timestamp, method, item_type, item_no, response_time_ms, status_code, success)
            VALUES ($ts, 'GetItem', NULL, NULL, 100, 200, 1);";
        cmd.Parameters.AddWithValue("$ts", ts.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class InMemorySettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            Bricklink = new BricklinkSettings { SoftThreshold = 1000, HardThreshold = 4500 }
        };
        public Task LoadAsync() => Task.CompletedTask;
        public Task SaveAsync() => Task.CompletedTask;
    }
}
