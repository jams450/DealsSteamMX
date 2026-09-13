using Deals.BusinessLogic.Context;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Extensions;

public static class DatabaseExtensions
{
    public static IServiceCollection AddApiDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }
}
