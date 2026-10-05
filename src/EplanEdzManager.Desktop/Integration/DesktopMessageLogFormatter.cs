using EplanEdzManager.AddIn.Protocol;

namespace EplanEdzManager.Desktop.Integration;

public static class DesktopMessageLogFormatter
{
    public static string Format(AddInMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message switch
        {
            HelloMessage hello =>
                $"type=Hello;instance={Sanitize(hello.InstanceId)};eplanVersion={Sanitize(hello.EplanVersion)};" +
                $"eplanPid={hello.EplanProcessId};addInVersion={Sanitize(hello.AddInVersion)}",
            EplanContextMessage context =>
                $"type=EplanContext;instance={Sanitize(context.InstanceId)};" +
                $"project={Status(context.ProjectName)};page={Status(context.PageName)};" +
                $"selection={Status(context.SelectionSummary)};selectedParts={Status(context.SelectedParts)};" +
                $"selectedPartCount={context.SelectedParts?.Items?.Count ?? 0};" +
                $"activePartsDatabase={Status(context.ActivePartsDatabase)}",
            OpenManagerMessage open =>
                $"type=OpenManager;instance={Sanitize(open.InstanceId)};reason={Sanitize(open.Reason)}",
            DisconnectMessage disconnect =>
                $"type=Disconnect;instance={Sanitize(disconnect.InstanceId)};reason={Sanitize(disconnect.Reason)}",
            _ => $"type={Sanitize(message.MessageType)};instance={Sanitize(message.InstanceId)}"
        };
    }

    private static string Status(ContextValue? value) =>
        value is null ? ContextAvailability.Unknown : Sanitize(value.Status);

    private static string Status(SelectedPartsContext? value) =>
        value is null ? ContextAvailability.Unknown : Sanitize(value.Status);

    private static string Sanitize(string? value) =>
        (value ?? string.Empty)
        .Replace("\r", " ")
        .Replace("\n", " ")
        .Replace(";", ",");
}
