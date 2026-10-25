#nullable enable
using System.Linq.Expressions;
using Deals.API;
using Deals.API.Extensions;
using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Checks the personal-operator configuration contract (spec S4, L3/L15) through the real DI registration and the
/// real <see cref="DealAlertRecipientScope"/>: the id is never inferred, a deployment with no configuration alerts
/// the single-user default account (1), and an explicitly non-positive override is still refused. Nothing here
/// reads configuration, the environment or a database: the options come from code and the repository is a fake.
/// </summary>
internal static class NtfyOptionsChecks
{
    private const string Token = "aB3dE5fG7hJ9kLmN2pQ4rS6tU8vW0xY1";

    public static async Task RunAsync()
    {
        DefaultsToOperatorOne();
        EnabledUsesDefaultOperatorWithoutConfiguration();
        EnabledRefusesExplicitNonPositiveOperator();
        EnabledAcceptsPositiveOperator();
        DisabledDoesNotValidateOperator();
        await DefaultOperatorResolvesAccountOneAsync();
        await DefaultOperatorFailsClosedWhenAccountOneIsMissingAsync();
        await DefaultOperatorFailsClosedWhenAccountOneIsInactiveAsync();
        await ExplicitZeroFailsClosedBeforeAnyQueryAsync();
        Console.WriteLine("PASS: ntfy options checks");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
    }

    /// <summary>No configuration at all is the single-user case: the operator is account 1.</summary>
    private static void DefaultsToOperatorOne()
    {
        var options = new NtfyOptions();
        Check(!options.Enabled, "the notifier is still disabled by default");
        Check(options.UserId == 1,
            "the operator user id defaults to 1 (single-user deployment), never inferred from an admin or the caller");
    }

    /// <summary>
    /// Enabling the notifier must not require a hand-written id: the default operator is a valid configuration.
    /// </summary>
    private static void EnabledUsesDefaultOperatorWithoutConfiguration()
    {
        var error = ValidationError(options =>
        {
            options.Enabled = true;
            options.AuthToken = Token;
        });
        Check(error is null, "an enabled notifier validates with the default operator 1 and no manual setting");
    }

    /// <summary>An explicit non-positive override is still a configuration error, never a silent "unset".</summary>
    private static void EnabledRefusesExplicitNonPositiveOperator()
    {
        var zero = ValidationError(options =>
        {
            options.Enabled = true;
            options.AuthToken = Token;
            options.UserId = 0;
        });
        Check(zero is not null && zero.Contains("Ntfy:UserId", StringComparison.Ordinal),
            "an explicit operator 0 is refused when the notifier is enabled");

        var negative = ValidationError(options =>
        {
            options.Enabled = true;
            options.AuthToken = Token;
            options.UserId = -3;
        });
        Check(negative is not null && negative.Contains("Ntfy:UserId", StringComparison.Ordinal),
            "a non-positive operator id is not a valid operator");
    }

    private static void EnabledAcceptsPositiveOperator()
    {
        var error = ValidationError(options =>
        {
            options.Enabled = true;
            options.AuthToken = Token;
            options.UserId = 7;
        });
        Check(error is null, "an explicit positive operator id still validates");
    }

    private static void DisabledDoesNotValidateOperator()
    {
        // PRESERVE: validation stays conditional, so a disabled notifier still boots whatever the operator is.
        var error = ValidationError(_ => { });
        Check(error is null, "a disabled notifier validates without an explicit operator id");
    }

    /// <summary>
    /// The default is not just a number in the options: the real scope reads account 1 from the repository with no
    /// configuration, which is what makes "no setup necessary" true for a single-user deployment.
    /// </summary>
    private static async Task DefaultOperatorResolvesAccountOneAsync()
    {
        var repository = new FakeRepository();
        repository.Users.Add(new User { UserId = 1, Active = true });
        var scope = new DealAlertRecipientScope(repository, Options.Create(new NtfyOptions()));

        var recipient = await scope.LoadAsync(CancellationToken.None);

        Check(recipient is not null && recipient.UserId == 1,
            "an unconfigured notifier alerts the default account 1");
        Check(repository.GetCalls > 0, "the default operator is read from the account table, never inferred");
    }

    /// <summary>Fail closed: no account 1 means no producer and no dispatcher, even though the id is positive.</summary>
    private static async Task DefaultOperatorFailsClosedWhenAccountOneIsMissingAsync()
    {
        var repository = new FakeRepository();
        var scope = new DealAlertRecipientScope(repository, Options.Create(new NtfyOptions()));

        var recipient = await scope.LoadAsync(CancellationToken.None);

        Check(recipient is null, "a deployment where account 1 does not exist fails closed");
    }

    /// <summary>Fail closed: an inactive default account receives nothing.</summary>
    private static async Task DefaultOperatorFailsClosedWhenAccountOneIsInactiveAsync()
    {
        var repository = new FakeRepository();
        repository.Users.Add(new User { UserId = 1, Active = false });
        var scope = new DealAlertRecipientScope(repository, Options.Create(new NtfyOptions()));

        var recipient = await scope.LoadAsync(CancellationToken.None);

        Check(recipient is null, "an inactive default account 1 fails closed");
    }

    /// <summary>The explicit reset still short-circuits before any query, so nothing is read at all.</summary>
    private static async Task ExplicitZeroFailsClosedBeforeAnyQueryAsync()
    {
        var repository = new FakeRepository();
        repository.Users.Add(new User { UserId = 1, Active = true });
        var scope = new DealAlertRecipientScope(repository, Options.Create(new NtfyOptions { UserId = 0 }));

        var recipient = await scope.LoadAsync(CancellationToken.None);

        Check(recipient is null, "an explicitly unset operator fails closed");
        Check(repository.GetCalls == 0, "an explicitly unset operator queries nothing");
    }

    /// <summary>Resolves the options exactly like the host does and returns the validation failure, if any.</summary>
    private static string? ValidationError(Action<NtfyOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApiNtfyNotifications();
        services.Configure(configure);

        using var provider = services.BuildServiceProvider();
        try
        {
            _ = provider.GetRequiredService<IOptions<NtfyOptions>>().Value;
            return null;
        }
        catch (OptionsValidationException exception)
        {
            return exception.Message;
        }
    }
}

/// <summary>
/// Minimal read-only repository over in-memory rows. Only <see cref="Get{T}"/> is implemented because the scope
/// only reads identity tables; every write path throws so a check cannot silently mutate anything. EF's async
/// operators are supported by the async queryable below, so the real scope query runs without a database.
/// </summary>
internal sealed class FakeRepository : IRepository
{
    public List<User> Users { get; } = [];

    /// <summary>Number of <see cref="Get{T}"/> calls, so "fails closed before any query" is observable.</summary>
    public int GetCalls { get; private set; }

    public IQueryable<T> Get<T>() where T : class
    {
        GetCalls++;
        IEnumerable<T> rows = typeof(T) == typeof(User)
            ? Users.Cast<T>()
            : [];
        return new TestAsyncEnumerable<T>(rows.ToList());
    }

    public IQueryable<T> Get<T>(Expression<Func<T, bool>> predicate) where T : class => Get<T>().Where(predicate);

    public DbSet<T> GetTrack<T>() where T : class => throw NotSupported();
    public Task<T?> GetByIdAsync<T>(int id) where T : class => throw NotSupported();
    public Task<T?> GetByIdAsync<T>(Guid id) where T : class => throw NotSupported();
    public Task<T> Save<T>(T model) where T : class => throw NotSupported();
    public Task<T> SaveUpdate<T>(int Id, T model) where T : class => throw NotSupported();
    public Task<T> SaveUpdate<T>(Guid Id, T model) where T : class => throw NotSupported();
    public Task<T> AddOrUpdate<T>(T obj) where T : class => throw NotSupported();
    public Task<bool> UpdateFieldAsync<T>(int id, string propertyName, object value) where T : class => throw NotSupported();
    public Task<bool> UpdateFieldAsync<T, TValue>(int id, Expression<Func<T, TValue>> propertySelector, TValue value) where T : class => throw NotSupported();
    public Task<int> UpdateFieldsAsync<T>(int id, Dictionary<string, object> fields) where T : class => throw NotSupported();
    public Task<int> UpdateFieldsAsync<T>(T entity, Dictionary<string, object> fields) where T : class => throw NotSupported();
    public Task<int> RemoveAsync<T>(T entity) where T : class => throw NotSupported();
    public Task<int> RemoveAsync<T>(int id) where T : class => throw NotSupported();
    public Task<int> RemoveRangeAsync<T>(List<T> entities) where T : class => throw NotSupported();
    public Task<List<T>> SyncAsync<T>(List<T> newListModel, List<T> currentListModel, Func<T, object> keySelector) where T : class => throw NotSupported();
    public (List<T> ToAdd, List<T> ToRemove) DiffList<T, TKey>(IEnumerable<T> original, IEnumerable<T> updated, Func<T, TKey> keySelector) where TKey : notnull => throw NotSupported();
    public Task<int> ExecuteSqlRawAsync(string sql, params object[] parameters) => throw NotSupported();
    public Task<List<T>> SqlQueryAsync<T>(string sql, params object[] parameters) where T : class => throw NotSupported();
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw NotSupported();
    public Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> operation) => throw NotSupported();

    private static NotSupportedException NotSupported() =>
        new("the operator-scope checks are read-only: no write or raw SQL path may run");
}

/// <summary>Enumerable queryable that also answers EF's async operators over in-memory rows.</summary>
internal sealed class TestAsyncEnumerable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public TestAsyncEnumerable(IEnumerable<T> enumerable) : base(enumerable) { }
    public TestAsyncEnumerable(Expression expression) : base(expression) { }

    IQueryProvider IQueryable.Provider => new TestAsyncQueryProvider<T>(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new TestAsyncEnumerator<T>(this.AsEnumerable().GetEnumerator());
}

internal sealed class TestAsyncEnumerator<T>(IEnumerator<T> inner) : IAsyncEnumerator<T>
{
    public T Current => inner.Current;
    public ValueTask<bool> MoveNextAsync() => new(inner.MoveNext());
    public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Adapts the in-memory provider to EF's async surface without a database connection.</summary>
internal sealed class TestAsyncQueryProvider<TEntity>(IQueryProvider inner) : IAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression) => new TestAsyncEnumerable<TEntity>(expression);
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new TestAsyncEnumerable<TElement>(expression);
    public object? Execute(Expression expression) => inner.Execute(expression);
    public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
    public IAsyncEnumerable<TResult> ExecuteAsync<TResult>(Expression expression) => new TestAsyncEnumerable<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken)
    {
        var resultType = typeof(TResult).GetGenericArguments()[0];
        var execution = typeof(IQueryProvider)
            .GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!
            .MakeGenericMethod(resultType)
            .Invoke(inner, [expression]);
        return (TResult)typeof(Task)
            .GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(resultType)
            .Invoke(null, [execution])!;
    }
}
