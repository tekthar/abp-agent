using Microsoft.Extensions.DependencyInjection;

namespace AbpAgent.Tests;

public class AgentRunHereTests
{
    [Fact]
    public void It_is_made_in_an_application_with_no_generic_host_and_stops_when_disposed()
    {
        // An ABP test or console application has no IHostApplicationLifetime: making the service mustn't need one.
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<AgentRunHere>();
        ServiceProvider provider = services.BuildServiceProvider();

        AgentRunHere here = provider.GetRequiredService<AgentRunHere>();
        Assert.NotNull(here);
        provider.Dispose(); // and it lets go when the application's services do
    }
}
