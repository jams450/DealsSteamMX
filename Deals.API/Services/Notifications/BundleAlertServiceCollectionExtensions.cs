using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deals.API.Services.Notifications;

/// <summary>
/// Registration of the red bundle reminders (spec S2). It is a separate extension so enabling the family is
/// one call next to <c>AddApiNtfyNotifications()</c>, and so the existing notification bootstrap does not need
/// to learn about a second outbox.
///
/// <para>
/// Both phases are scoped like the offer pipeline: one cycle shares the TimeProvider, the options and the
/// operator scope, and the operator is re-read inside the dispatcher for every claimed row. The required
/// dependencies are the ones already registered by <c>AddApiNtfyNotifications()</c>
/// (<see cref="IDealAlertRecipientScope"/>, <see cref="INtfyAlertSender"/>, <see cref="TimeProvider"/>), so
/// this method adds the bundle family only and never a second sender.
/// </para>
///
/// <para>
/// There is no unscoped path: every type here is a plain scoped service with required constructor
/// dependencies, and the operator gate is a constructor argument of both phases. Wiring it is one line, so a
/// missing registration cannot silently disable alerts — the ntfy job cannot be constructed without it.
/// </para>
/// </summary>
public static class BundleAlertServiceCollectionExtensions
{
    /// <summary>
    /// Adds the bundle outbox, its catalog source, the acquisition gate and both phases. Call it after
    /// <c>AddApiNtfyNotifications()</c>; nothing here re-registers the shared sender or the operator scope.
    /// </summary>
    public static IServiceCollection AddApiBundleAlerts(this IServiceCollection services)
    {
        services.TryAddScoped<IBundleAlertOutbox, EfBundleAlertOutbox>();
        services.TryAddScoped<IBundleAlertSource, EfBundleAlertSource>();
        services.TryAddScoped<IBundlePurchaseGate, EfBundlePurchaseGate>();
        services.TryAddScoped<BundleAlertProducer>();
        services.TryAddScoped<BundleAlertDispatcher>();
        return services;
    }
}
