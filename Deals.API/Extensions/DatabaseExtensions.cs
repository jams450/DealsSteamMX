using Deals.BusinessLogic.Context;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Extensions;

public static class DatabaseExtensions
{
    /// <summary>
    /// Finite by default. The context used to run every command with no timeout at all
    /// (<c>SetCommandTimeout(0)</c>), so a command waiting on a lock could pin its pooled connection until
    /// the other side finished - and forever when the other side was stuck, which is how a single hung
    /// writer takes the pool down with it. Zero still means "no timeout" for an operator who needs it.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 60;

    public static IServiceCollection AddApiDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        var commandTimeoutSeconds = configuration.GetValue(
            "Database:CommandTimeoutSeconds",
            DefaultCommandTimeoutSeconds);

        if (commandTimeoutSeconds < 0)
        {
            throw new InvalidOperationException(
                "Database:CommandTimeoutSeconds must be zero (no timeout) or a positive number of seconds.");
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.CommandTimeout(commandTimeoutSeconds)));

        return services;
    }
}
