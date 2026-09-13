using System.Net.Mail;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Deals.API.Extensions;

public static class StartupAdminExtensions
{
    private const string SectionName = "BootstrapAdmin";

    public static async Task SeedStartupAdminAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<AppDbContext>();

        if (await dbContext.Users.AnyAsync(user => user.Admin))
        {
            return;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupAdminSeeder");
        var name = configuration[$"{SectionName}:Name"]?.Trim();
        var email = configuration[$"{SectionName}:Email"]?.Trim().ToLowerInvariant();
        var password = configuration[$"{SectionName}:Password"];

        if (!IsValid(name, email, password))
        {
            logger.LogWarning("No administrator exists and BootstrapAdmin configuration is missing or invalid; startup admin was not created.");
            return;
        }

        dbContext.Users.Add(new User
        {
            Name = name!,
            Email = email!,
            Password = services.GetRequiredService<IPasswordService>().HashPassword(password!),
            Active = true,
            Admin = true,
            SessionVersion = 1
        });
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Initial administrator account created.");
    }

    private static bool IsValid(string? name, string? email, string? password)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
            string.IsNullOrWhiteSpace(email) || email.Length > 100 ||
            string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            return false;
        }

        try
        {
            _ = new MailAddress(email);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
