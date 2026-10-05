using EplanEdzManager.AddIn.Protocol;
using EplanEdzManager.Desktop.Integration;
using Xunit;

namespace EplanEdzManager.AddIn.IntegrationTests;

public sealed class DesktopMessageLogFormatterTests
{
    [Fact]
    public void Context_log_contains_only_availability_and_count()
    {
        var message = new EplanContextMessage
        {
            InstanceId = "eplan-42",
            ProjectName = ContextValue.Available("Secret Project"),
            ProjectPath = ContextValue.Available(@"C:\Confidential\secret.elk"),
            PageName = ContextValue.Available("=A+B/1"),
            SelectionSummary = ContextValue.Available("Function: secret"),
            ActivePartsDatabase = ContextValue.Available(@"C:\Confidential\parts.alk"),
            SelectedParts = new SelectedPartsContext
            {
                Status = ContextAvailability.Available,
                Items = new List<SelectedPartContext>
                {
                    new() { PartNumber = "SECRET-PART" }
                }
            }
        };

        var result = DesktopMessageLogFormatter.Format(message);

        Assert.Contains("project=Available", result);
        Assert.Contains("page=Available", result);
        Assert.Contains("selection=Available", result);
        Assert.Contains("selectedPartCount=1", result);
        Assert.Contains("activePartsDatabase=Available", result);
        Assert.DoesNotContain("Secret Project", result);
        Assert.DoesNotContain("secret.elk", result);
        Assert.DoesNotContain("SECRET-PART", result);
        Assert.DoesNotContain("parts.alk", result);
    }

    [Fact]
    public void Hello_log_keeps_only_connection_diagnostics()
    {
        var result = DesktopMessageLogFormatter.Format(new HelloMessage
        {
            InstanceId = "eplan-42",
            EplanVersion = "2.9.4.14642",
            EplanProcessId = 42,
            AddInVersion = "1.0.0"
        });

        Assert.Equal(
            "type=Hello;instance=eplan-42;eplanVersion=2.9.4.14642;eplanPid=42;addInVersion=1.0.0",
            result);
    }
}
