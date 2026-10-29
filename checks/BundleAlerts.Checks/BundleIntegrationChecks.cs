using Deals.API.Services.Notifications;
using Deals.BusinessLogic.Context;
using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class BundleIntegrationChecks
{
    public static void Run()
    {
        // Build the actual provider model without opening a connection or reading deployment secrets.
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options, new NoCurrentUser());
        Check(context.Model.FindEntityType(typeof(BundleAlertEvent))?.GetTableName() == "bundle_alert_events", "EF discovers bundle outbox");
        Check(context.Model.FindEntityType(typeof(BundlePurchase))?.GetTableName() == "bundle_purchases", "EF discovers purchase header");
        Check(context.Model.FindEntityType(typeof(BundlePurchaseGame))?.GetTableName() == "bundle_purchase_games", "EF discovers purchased items");
        var services = new ServiceCollection();
        services.AddApiBundleAlerts();
        foreach (var type in new[] { typeof(IBundleAlertOutbox), typeof(IBundleAlertSource), typeof(IBundlePurchaseGate), typeof(BundleAlertProducer), typeof(BundleAlertDispatcher) })
            Check(services.Any(service => service.ServiceType == type), $"DI registers {type.Name}");

        var root = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(root, "Deals.sln")))
            root = Directory.GetParent(root)?.FullName ?? throw new Exception("Repository root unavailable");
        var schema = File.ReadAllText(Path.Combine(root, "SQL/schema.sql"));
        Check(schema.Contains("CREATE TABLE IF NOT EXISTS public.bundle_alert_events ("), "fresh schema includes outbox");
        var program = File.ReadAllText(Path.Combine(root, "Deals.API/Program.cs"));
        Check(program.Contains(".AddApiBundleAlerts()"), "bootstrap activates bundle alert services");
        var merge = File.ReadAllText(Path.Combine(root, "Deals.BusinessLogic/Services/GameMergeService.cs"));
        var repoint = merge.IndexOf("UPDATE public.bundle_purchase_games SET game_id = {0}", StringComparison.Ordinal);
        var deletion = merge.IndexOf("DELETE FROM public.games", StringComparison.Ordinal);
        Check(repoint >= 0 && deletion > repoint, "merge repoints purchase evidence before deleting canonical game");
        Check(!merge.Contains("DELETE FROM public.bundle_purchase_games"), "merge never deletes acquired membership");
        Console.WriteLine("PASS: bundle EF/DI/schema/merge composition");
    }

    private sealed class NoCurrentUser : ICurrentUserService
    {
        public int? GetUserId() => null;
        public int? GetSessionVersion() => null;
        public Guid? GetSessionId() => null;
        public string GetName() => string.Empty;
        public string GetEmail() => string.Empty;
        public bool IsAdmin() => false;
        public int GetRequiredUserId() => throw new InvalidOperationException("No user in model-only check");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
