using System.Reflection;
using Zinwe.Agent.Core.Features;

namespace Zinwe.Agent.Service.Features;

/// <summary>
/// A feature's DI wiring. Each feature ships one parameterless implementation that registers its own services
/// (collectors, enforcers, options) and its <see cref="IFeatureModule"/>. The host discovers every implementation
/// in the Core and Service assemblies at startup and calls it, so a new feature never edits Program.cs.
/// </summary>
public interface IFeatureRegistration
{
    void Register(IServiceCollection services, IConfiguration config);
}

/// <summary>Registers all discovered feature modules and the host that runs them.</summary>
public static class FeatureModuleRegistration
{
    public static void AddFeatureModules(this IHostApplicationBuilder builder)
    {
        // Shared, policy-driven: the current policy each feature scope reads its options from.
        builder.Services.AddSingleton<FeaturePolicyState>();

        foreach (var registration in Discover())
        {
            registration.Register(builder.Services, builder.Configuration);
        }

        // One supervisor instance is the hosted service AND the IFeaturePolicyApply PolicySync calls.
        builder.Services.AddSingleton<FeatureSupervisor>();
        builder.Services.AddSingleton<IFeaturePolicyApply>(sp => sp.GetRequiredService<FeatureSupervisor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FeatureSupervisor>());
    }

    /// <summary>Every concrete <see cref="IFeatureRegistration"/> in the agent's own assemblies, instantiated.</summary>
    internal static IReadOnlyList<IFeatureRegistration> Discover()
    {
        var assemblies = new[] { typeof(IFeatureModule).Assembly, typeof(FeatureModuleRegistration).Assembly };
        return assemblies
            .SelectMany(SafeGetTypes)
            .Distinct()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IFeatureRegistration).IsAssignableFrom(t))
            .Select(t => (IFeatureRegistration)Activator.CreateInstance(t)!)
            .ToList();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }
}
