using Deals.API.Interfaces;
using Deals.API.Services;
using Deals.BusinessLogic.Interfaces;
using Deals.BusinessLogic.Services;
using IPasswordService = Deals.BusinessLogic.Interfaces.IPasswordService;
using PasswordService = Deals.BusinessLogic.Services.PasswordService;

namespace Deals.API.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPasswordService, PasswordService>();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRepository, Repository>();

        return services;
    }
}
