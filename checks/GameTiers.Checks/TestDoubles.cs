using System.Linq.Expressions;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GameTiersChecks;

/// <summary>
/// Minimal <see cref="IRepository"/> over the in-memory context. Only the members the tier service uses
/// are implemented; every other member throws, so an accidental dependency fails loudly instead of
/// returning an empty result that would look like a passing check. No PostgreSQL server is contacted and
/// every fixture is synthetic.
/// </summary>
internal sealed class InMemoryRepository : IRepository
{
    private readonly AppDbContext _context;

    public InMemoryRepository(AppDbContext context) => _context = context;

    public IQueryable<T> Get<T>() where T : class => _context.Set<T>().AsNoTracking();

    public IQueryable<T> Get<T>(Expression<Func<T, bool>> predicate) where T : class
        => _context.Set<T>().AsNoTracking().Where(predicate);

    public DbSet<T> GetTrack<T>() where T : class => _context.Set<T>();

    public async Task<T> Save<T>(T model) where T : class
    {
        await _context.Set<T>().AddAsync(model);
        await _context.SaveChangesAsync();
        return model;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _context.SaveChangesAsync(cancellationToken);

    public Task<int> RemoveAsync<T>(T entity) where T : class
    {
        _context.Set<T>().Remove(entity);
        return _context.SaveChangesAsync();
    }

    public Task<T?> GetByIdAsync<T>(int id) where T : class => throw new NotSupportedException(nameof(GetByIdAsync));

    public Task<T?> GetByIdAsync<T>(Guid id) where T : class => throw new NotSupportedException(nameof(GetByIdAsync));

    public Task<T> SaveUpdate<T>(int Id, T model) where T : class => throw new NotSupportedException(nameof(SaveUpdate));

    public Task<T> SaveUpdate<T>(Guid Id, T model) where T : class => throw new NotSupportedException(nameof(SaveUpdate));

    public Task<T> AddOrUpdate<T>(T obj) where T : class => throw new NotSupportedException(nameof(AddOrUpdate));

    public Task<bool> UpdateFieldAsync<T>(int id, string propertyName, object value) where T : class
        => throw new NotSupportedException(nameof(UpdateFieldAsync));

    public Task<bool> UpdateFieldAsync<T, TValue>(int id, Expression<Func<T, TValue>> propertySelector, TValue value) where T : class
        => throw new NotSupportedException(nameof(UpdateFieldAsync));

    public Task<int> UpdateFieldsAsync<T>(int id, Dictionary<string, object> fields) where T : class
        => throw new NotSupportedException(nameof(UpdateFieldsAsync));

    public Task<int> UpdateFieldsAsync<T>(T entity, Dictionary<string, object> fields) where T : class
        => throw new NotSupportedException(nameof(UpdateFieldsAsync));

    public Task<int> RemoveAsync<T>(int id) where T : class => throw new NotSupportedException(nameof(RemoveAsync));

    public Task<int> RemoveRangeAsync<T>(List<T> entities) where T : class => throw new NotSupportedException(nameof(RemoveRangeAsync));

    public Task<List<T>> SyncAsync<T>(List<T> newListModel, List<T> currentListModel, Func<T, object> keySelector) where T : class
        => throw new NotSupportedException(nameof(SyncAsync));

    public (List<T> ToAdd, List<T> ToRemove) DiffList<T, TKey>(IEnumerable<T> original, IEnumerable<T> updated, Func<T, TKey> keySelector) where TKey : notnull
        => throw new NotSupportedException(nameof(DiffList));

    public Task<int> ExecuteSqlRawAsync(string sql, params object[] parameters)
        => throw new NotSupportedException(nameof(ExecuteSqlRawAsync));

    public Task<List<T>> SqlQueryAsync<T>(string sql, params object[] parameters) where T : class
        => throw new NotSupportedException(nameof(SqlQueryAsync));

    public Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> operation)
        => throw new NotSupportedException(nameof(ExecuteInTransactionAsync));
}

/// <summary>
/// Request identity stub. The in-memory context audits inserts through <see cref="GetName"/>, which is the
/// only member this check reaches.
/// </summary>
internal sealed class StubCurrentUserService : ICurrentUserService
{
    public int? GetUserId() => null;

    public int GetRequiredUserId() => throw new InvalidOperationException("A check runs without a request user.");

    public int? GetSessionVersion() => null;

    public Guid? GetSessionId() => null;

    public string GetName() => "game-tiers-checks";

    public string GetEmail() => "game-tiers-checks@example.com";

    public bool IsAdmin() => false;
}
