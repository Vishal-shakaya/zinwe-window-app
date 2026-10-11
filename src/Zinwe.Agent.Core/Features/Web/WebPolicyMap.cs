using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Features.Web;

/// <summary>Turns the owner's <see cref="WebPolicy"/> block into the module's <see cref="WebFeatureOptions"/>.</summary>
public static class WebPolicyMap
{
    /// <summary>
    /// <paramref name="defaults"/> are the appsettings-bound values. A null <paramref name="block"/> (backend
    /// hasn't configured web yet) keeps them. Otherwise the owner's choices win; sampling cadence and the
    /// (opt-in, off-by-default) title-detail flag stay on their local defaults.
    /// </summary>
    public static WebFeatureOptions FromPolicy(WebFeatureOptions defaults, WebPolicy? block)
    {
        if (block is null)
        {
            return defaults;
        }
        return new WebFeatureOptions
        {
            Enabled = block.Enabled,
            SampleIntervalSeconds = defaults.SampleIntervalSeconds,
            CollectForegroundUsage = defaults.CollectForegroundUsage,
            IncludeDetails = defaults.IncludeDetails,
            BlockingEnabled = block.Mode == WebMode.Block,
            UseBuiltInSampleList = true,
            Categories = [.. block.Categories],
            CustomDomains = [.. block.CustomDomains],
        };
    }
}
