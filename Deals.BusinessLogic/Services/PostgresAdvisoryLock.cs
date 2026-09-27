using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Deals.BusinessLogic.Services;

/// <summary>Cross-process PostgreSQL session advisory lock. The connection stays open until disposal.</summary>
public sealed class PostgresAdvisoryLock(DbContext context, long key) : IAsyncDisposable
{
    private readonly DbContext _context = context;
    private readonly long _key = key;
    private bool _acquired;

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        command.Parameters.AddWithValue("key", _key);
        _acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        return _acquired;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_acquired) return;
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            // A DbContext owns this connection. If it was closed by another operation, there is no
            // session lock left to release and opening a new session would unlock nothing.
            _acquired = false;
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@key)";
        command.Parameters.AddWithValue("key", _key);
        await command.ExecuteNonQueryAsync();
        _acquired = false;
    }
}
