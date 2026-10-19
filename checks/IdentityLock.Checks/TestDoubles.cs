using System.Linq.Expressions;
using Deals.BusinessLogic.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace IdentityLockChecks;

/// <summary>
/// Records the statements the identity gate issues and can be told to fail. Only
/// <see cref="ExecuteSqlRawAsync"/> is implemented: every other member throws, so an accidental
/// dependency fails loudly instead of returning an empty result that would look like a passing check.
/// </summary>
internal sealed class RecordingRepository : IRepository
{
    public List<string> Statements { get; } = [];

    public Exception? FailWith { get; set; }

    public Task<int> ExecuteSqlRawAsync(string sql, params object[] parameters)
    {
        Statements.Add(sql);
        return FailWith is null
            ? Task.FromResult(0)
            : Task.FromException<int>(FailWith);
    }

    public IQueryable<T> Get<T>() where T : class => throw new NotSupportedException(nameof(Get));

    public IQueryable<T> Get<T>(Expression<Func<T, bool>> predicate) where T : class => throw new NotSupportedException(nameof(Get));

    public DbSet<T> GetTrack<T>() where T : class => throw new NotSupportedException(nameof(GetTrack));

    public Task<T?> GetByIdAsync<T>(int id) where T : class => throw new NotSupportedException(nameof(GetByIdAsync));

    public Task<T?> GetByIdAsync<T>(Guid id) where T : class => throw new NotSupportedException(nameof(GetByIdAsync));

    public Task<T> Save<T>(T model) where T : class => throw new NotSupportedException(nameof(Save));

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

    public Task<int> RemoveAsync<T>(T entity) where T : class => throw new NotSupportedException(nameof(RemoveAsync));

    public Task<int> RemoveAsync<T>(int id) where T : class => throw new NotSupportedException(nameof(RemoveAsync));

    public Task<int> RemoveRangeAsync<T>(List<T> entities) where T : class => throw new NotSupportedException(nameof(RemoveRangeAsync));

    public Task<List<T>> SyncAsync<T>(List<T> newListModel, List<T> currentListModel, Func<T, object> keySelector) where T : class
        => throw new NotSupportedException(nameof(SyncAsync));

    public (List<T> ToAdd, List<T> ToRemove) DiffList<T, TKey>(IEnumerable<T> original, IEnumerable<T> updated, Func<T, TKey> keySelector) where TKey : notnull
        => throw new NotSupportedException(nameof(DiffList));

    public Task<List<T>> SqlQueryAsync<T>(string sql, params object[] parameters) where T : class
        => throw new NotSupportedException(nameof(SqlQueryAsync));

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(nameof(SaveChangesAsync));

    public Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> operation)
        => throw new NotSupportedException(nameof(ExecuteInTransactionAsync));
}
