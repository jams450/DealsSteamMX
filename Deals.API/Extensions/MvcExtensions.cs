using System.Text.Json.Serialization;

namespace Deals.API.Extensions;

public static class MvcExtensions
{
    public static IServiceCollection AddApiMvc(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

        return services;
    }

    public static IServiceCollection AddApiOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi();
        return services;
    }

    public static IServiceCollection AddApiHttpContext(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        return services;
    }
}
