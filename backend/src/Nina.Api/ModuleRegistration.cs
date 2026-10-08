using Nina.Family;
using Nina.Identity;
using Nina.Notifications;
using Nina.Privacy;
using Nina.SleepIntelligence;
using Nina.Subscriptions;
using Nina.Tracking;

namespace Nina.Api;

internal static class ModuleRegistration
{
    public static readonly IReadOnlyList<string> ModuleNames =
    [
        IdentityModule.Name,
        FamilyModule.Name,
        TrackingModule.Name,
        SleepIntelligenceModule.Name,
        NotificationsModule.Name,
        SubscriptionsModule.Name,
        PrivacyModule.Name,
    ];

    public static IServiceCollection AddNinaModules(this IServiceCollection services, IConfiguration configuration) => services
        .AddIdentityModule(configuration)
        .AddFamilyModule()
        .AddTrackingModule(configuration)
        .AddSleepIntelligenceModule()
        .AddNotificationsModule()
        .AddSubscriptionsModule()
        .AddPrivacyModule();

    public static IEndpointRouteBuilder MapNinaModules(this IEndpointRouteBuilder app)
    {
        app.MapIdentityModule();
        app.MapFamilyModule();
        app.MapTrackingModule();
        return app;
    }
}
