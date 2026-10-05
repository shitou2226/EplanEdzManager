using EplanEdzManager.AddIn.Protocol;
using Newtonsoft.Json;
using Xunit;

namespace EplanEdzManager.EplanAddIn.Tests;

public sealed class AddInProtocolTests
{
    [Fact]
    public void Context_round_trips_explicit_evidence_statuses_and_selected_parts()
    {
        var source = new EplanContextMessage
        {
            InstanceId = "eplan-1234-abc",
            Timestamp = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(8)),
            EplanVersion = ContextValue.Available("2.9.4.14642"),
            EplanProcessId = ContextValue.Available("1234"),
            ProjectName = ContextValue.Unavailable("No project is open."),
            ProjectPath = ContextValue.Unavailable("No project is open."),
            ProjectDirectory = ContextValue.Unavailable("No project is open."),
            PageName = ContextValue.Unknown("The graphical editor has no current page."),
            SelectionSummary = ContextValue.Available("1 function selected"),
            ActivePartsDatabase = ContextValue.Unsupported("SQL connection strings are not transmitted."),
            SelectedParts = new SelectedPartsContext
            {
                Status = ContextAvailability.Available,
                Items = new List<SelectedPartContext>
                {
                    new() { PartNumber = "DR-100-24", Variant = "1" }
                }
            }
        };

        var json = AddInJson.Serialize(source);
        var parsed = Assert.IsType<EplanContextMessage>(AddInJson.DeserializeKnown(json));

        Assert.Equal(AddInProtocol.Version, parsed.ProtocolVersion);
        Assert.Equal(ContextAvailability.Unavailable, parsed.ProjectName.Status);
        Assert.Equal(ContextAvailability.Unavailable, parsed.ProjectDirectory.Status);
        Assert.Equal(ContextAvailability.Unsupported, parsed.ActivePartsDatabase.Status);
        Assert.Equal("DR-100-24", Assert.Single(parsed.SelectedParts.Items).PartNumber);
    }

    [Fact]
    public void Unknown_fields_are_ignored_for_forward_compatibility()
    {
        const string json = "{\"protocolVersion\":\"1.0\",\"messageType\":\"Ping\",\"instanceId\":\"i\",\"timestamp\":\"2026-10-03T00:00:00Z\",\"futureField\":42}";
        var parsed = AddInJson.Deserialize<PingMessage>(json);
        Assert.Equal("i", parsed.InstanceId);
    }

    [Fact]
    public void Protocol_mismatch_is_explicit()
    {
        const string json = "{\"protocolVersion\":\"9.0\",\"messageType\":\"Ping\",\"instanceId\":\"i\",\"timestamp\":\"2026-10-03T00:00:00Z\"}";
        var exception = Assert.Throws<AddInProtocolException>(() => AddInJson.DeserializeKnown(json));
        Assert.Equal(AddInProtocol.ErrorCodes.ProtocolMismatch, exception.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"protocolVersion\":\"1.0\"}")]
    public void Invalid_envelope_is_rejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => AddInJson.ReadHeader(json));
    }

    [Fact]
    public void Required_message_types_parse()
    {
        AddInMessage[] messages =
        {
            new HelloMessage { InstanceId = "i", EplanVersion = "2.9.4.14642", EplanProcessId = 1, AddInVersion = "1.0.0" },
            new OpenManagerMessage { InstanceId = "i", Reason = "EplanMenu" },
            new PingMessage { InstanceId = "i" },
            new DesktopStatusMessage { InstanceId = "desktop", Connected = true, Status = "Ready" },
            new DisconnectMessage { InstanceId = "i", Reason = "EPLAN closing" }
        };

        foreach (var message in messages)
        {
            var parsed = AddInJson.DeserializeKnown(AddInJson.Serialize(message));
            Assert.Equal(message.MessageType, parsed.MessageType);
        }
    }

    [Fact]
    public void Multiple_eplan_instances_remain_distinguishable()
    {
        var first = new HelloMessage { InstanceId = "eplan-100-a", EplanProcessId = 100 };
        var second = new HelloMessage { InstanceId = "eplan-200-b", EplanProcessId = 200 };
        Assert.NotEqual(AddInJson.Deserialize<HelloMessage>(AddInJson.Serialize(first)).InstanceId,
            AddInJson.Deserialize<HelloMessage>(AddInJson.Serialize(second)).InstanceId);
    }

    [Fact]
    public void Context_status_outside_the_defined_four_state_model_is_rejected()
    {
        var json = AddInJson.Serialize(new EplanContextMessage
        {
            InstanceId = "eplan-a",
            ProjectName = new ContextValue { Status = "Maybe", Value = "P" }
        });

        var exception = Assert.Throws<AddInProtocolException>(() => AddInJson.DeserializeKnown(json));
        Assert.Equal(AddInProtocol.ErrorCodes.InvalidMessage, exception.ErrorCode);
    }
}
