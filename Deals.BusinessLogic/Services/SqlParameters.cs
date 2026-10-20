using Npgsql;
using NpgsqlTypes;

namespace Deals.BusinessLogic.Services;

/// <summary>
/// Explicitly typed bindings for the raw-SQL writers.
///
/// Why this exists: EF Core 9 infers a store type for every raw-SQL argument from the runtime type of the
/// value it receives, and it has no mapping for <see cref="DBNull"/>. A nullable provider field written as
/// <c>(object?)value ?? DBNull.Value</c> therefore aborts the statement during command construction, before
/// the connection is even opened:
/// <code>The current provider doesn't have a store type mapping for properties of type 'DBNull'.</code>
/// That is the 2026-10-19 <c>bundles.global-sync</c> failure (spec S1): the first bundle whose shop, page
/// URL or dates were absent killed the whole sweep, so the previous snapshot was preserved and never
/// refreshed. The same binding was in the gg.deals bundle writer, the global deals writer and the merge
/// audit insert.
///
/// The boxed-null alternative (<c>(object?)value</c>) does execute, but it sends an <c>unknown</c>-typed
/// NULL and leaves the type to server-side inference. These factories name the type on the client instead,
/// which keeps the statement identical for a present and an absent value and keeps <see cref="DBNull"/> out
/// of every call site.
///
/// The declared types mirror what EF used to infer from the CLR value
/// (<see cref="NpgsqlDbType.Text"/>, <see cref="NpgsqlDbType.Integer"/>, <see cref="NpgsqlDbType.TimestampTz"/>,
/// <see cref="NpgsqlDbType.Date"/>, <see cref="NpgsqlDbType.Numeric"/>), so every value that was already
/// bound successfully keeps its exact store type. JSONB columns keep their <c>::jsonb</c> cast and bind as
/// text, so the server still validates the payload rather than a client-side converter.
///
/// Regression coverage: <c>checks/SqlParameters.Checks</c> builds the production statements through EF Core
/// with the real Npgsql provider and fails if any argument is not an explicitly typed parameter.
/// </summary>
public static class SqlParameters
{
    /// <summary>A nullable text, varchar or JSONB (with its <c>::jsonb</c> cast) column.</summary>
    public static NpgsqlParameter Text(string name, string? value) => Typed(name, value, NpgsqlDbType.Text);

    /// <summary>A nullable <c>integer</c> column.</summary>
    public static NpgsqlParameter Integer(string name, int? value) => Typed(name, value, NpgsqlDbType.Integer);

    /// <summary>A <c>bigint</c> identifier column.</summary>
    public static NpgsqlParameter Bigint(string name, long? value) => Typed(name, value, NpgsqlDbType.Bigint);

    /// <summary>A nullable <c>boolean</c> column.</summary>
    public static NpgsqlParameter Boolean(string name, bool? value) => Typed(name, value, NpgsqlDbType.Boolean);

    /// <summary>A nullable <c>timestamp with time zone</c> column.</summary>
    public static NpgsqlParameter TimestampTz(string name, DateTime? value) =>
        Typed(name, value, NpgsqlDbType.TimestampTz);

    /// <summary>A nullable <c>date</c> column.</summary>
    public static NpgsqlParameter Date(string name, DateOnly? value) => Typed(name, value, NpgsqlDbType.Date);

    /// <summary>A nullable <c>numeric</c> column.</summary>
    public static NpgsqlParameter Numeric(string name, decimal? value) => Typed(name, value, NpgsqlDbType.Numeric);

    private static NpgsqlParameter Typed(string name, object? value, NpgsqlDbType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value,
            IsNullable = true,
        };
    }
}
