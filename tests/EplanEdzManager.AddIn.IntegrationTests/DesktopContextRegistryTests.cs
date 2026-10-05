using EplanEdzManager.AddIn.Protocol;
using EplanEdzManager.Desktop.Integration;
using Xunit;

namespace EplanEdzManager.AddIn.IntegrationTests;

public sealed class DesktopContextRegistryTests
{
    [Fact]
    public void Registry_tracks_multiple_instances_and_latest_context_without_merging_them()
    {
        var registry = new DesktopContextRegistry();
        registry.Apply(new HelloMessage { InstanceId = "eplan-a", EplanProcessId = 100 });
        registry.Apply(new EplanContextMessage { InstanceId = "eplan-a", ProjectName = ContextValue.Available("A") });
        registry.Apply(new HelloMessage { InstanceId = "eplan-b", EplanProcessId = 200 });
        registry.Apply(new EplanContextMessage { InstanceId = "eplan-b", ProjectName = ContextValue.Available("B") });

        Assert.Equal(2, registry.Connections.Count);
        Assert.Equal("B", registry.Current!.Context!.ProjectName.Value);
        Assert.Equal("A", registry.Connections["eplan-a"].Context!.ProjectName.Value);
    }

    [Fact]
    public void Disconnect_marks_only_the_matching_instance_offline()
    {
        var registry = new DesktopContextRegistry();
        registry.Apply(new HelloMessage { InstanceId = "eplan-a", EplanProcessId = 100 });
        registry.Apply(new HelloMessage { InstanceId = "eplan-b", EplanProcessId = 200 });
        registry.Apply(new DisconnectMessage { InstanceId = "eplan-a", Reason = "EPLAN closing" });

        Assert.False(registry.Connections["eplan-a"].Connected);
        Assert.True(registry.Connections["eplan-b"].Connected);
        Assert.Equal("eplan-b", registry.Current!.InstanceId);
    }
}
