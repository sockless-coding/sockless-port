using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Data;
using Sockless.Port.Setup;

namespace Sockless.Port.Core.Tests;

public sealed class SetupStateTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private PortDbContext _db = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(TestContext.Current.CancellationToken);
        _db = new PortDbContext(new DbContextOptionsBuilder<PortDbContext>().UseSqlite(_connection).Options);
        await _db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Empty_database_needs_setup_and_issues_a_token()
    {
        var state = new SetupState(NullLogger<SetupState>.Instance);

        await state.InitializeAsync(_db, TestContext.Current.CancellationToken);

        Assert.False(state.IsComplete);
        Assert.NotNull(state.Token);
        Assert.Equal(32, state.Token.Length);
    }

    [Fact]
    public async Task Existing_user_completes_setup_without_a_token()
    {
        _db.Users.Add(new PortUser { UserName = "admin", NormalizedUserName = "ADMIN" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var state = new SetupState(NullLogger<SetupState>.Instance);

        await state.InitializeAsync(_db, TestContext.Current.CancellationToken);

        Assert.True(state.IsComplete);
        Assert.Null(state.Token);
        Assert.False(state.ValidateToken("anything"));
    }

    [Fact]
    public async Task Token_validation_ignores_case_and_surrounding_whitespace()
    {
        var state = new SetupState(NullLogger<SetupState>.Instance);
        await state.InitializeAsync(_db, TestContext.Current.CancellationToken);

        Assert.True(state.ValidateToken($"  {state.Token!.ToUpperInvariant()}\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public async Task Wrong_or_missing_token_is_rejected(string? candidate)
    {
        var state = new SetupState(NullLogger<SetupState>.Instance);
        await state.InitializeAsync(_db, TestContext.Current.CancellationToken);

        Assert.False(state.ValidateToken(candidate));
    }

    [Fact]
    public async Task Token_is_stable_across_repeated_initialization()
    {
        var state = new SetupState(NullLogger<SetupState>.Instance);
        await state.InitializeAsync(_db, TestContext.Current.CancellationToken);
        var first = state.Token;

        await state.InitializeAsync(_db, TestContext.Current.CancellationToken);

        Assert.Equal(first, state.Token);
    }
}
