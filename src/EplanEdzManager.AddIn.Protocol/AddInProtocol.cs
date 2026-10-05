using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace EplanEdzManager.AddIn.Protocol;

public static class AddInProtocol
{
    public const string Version = "1.0";

    public static class MessageTypes
    {
        public const string Hello = "Hello";
        public const string OpenManager = "OpenManager";
        public const string EplanContext = "EplanContext";
        public const string Ping = "Ping";
        public const string Shutdown = "Shutdown";
        public const string Disconnect = "Disconnect";
        public const string DesktopStatus = "DesktopStatus";
        public const string Error = "Error";
    }

    public static class ErrorCodes
    {
        public const string ProtocolMismatch = "ADDIN001";
        public const string InvalidMessage = "ADDIN002";
        public const string DesktopUnavailable = "ADDIN101";
    }
}

public static class ContextAvailability
{
    public const string Available = "Available";
    public const string Unavailable = "Unavailable";
    public const string Unsupported = "Unsupported";
    public const string Unknown = "Unknown";

    public static bool IsKnown(string value) =>
        value == Available || value == Unavailable || value == Unsupported || value == Unknown;
}

public sealed class ContextValue
{
    public string Status { get; set; } = ContextAvailability.Unknown;
    public string Value { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;

    public static ContextValue Available(string value) => new ContextValue
    {
        Status = ContextAvailability.Available,
        Value = value ?? string.Empty
    };

    public static ContextValue Unavailable(string detail) => new ContextValue
    {
        Status = ContextAvailability.Unavailable,
        Detail = detail ?? string.Empty
    };

    public static ContextValue Unsupported(string detail) => new ContextValue
    {
        Status = ContextAvailability.Unsupported,
        Detail = detail ?? string.Empty
    };

    public static ContextValue Unknown(string detail) => new ContextValue
    {
        Status = ContextAvailability.Unknown,
        Detail = detail ?? string.Empty
    };
}

public abstract class AddInMessage
{
    public string ProtocolVersion { get; set; } = AddInProtocol.Version;
    public string MessageType { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MessageHeader : AddInMessage
{
}

public sealed class HelloMessage : AddInMessage
{
    public HelloMessage() => MessageType = AddInProtocol.MessageTypes.Hello;
    public string EplanVersion { get; set; } = string.Empty;
    public int EplanProcessId { get; set; }
    public string AddInVersion { get; set; } = string.Empty;
}

public sealed class OpenManagerMessage : AddInMessage
{
    public OpenManagerMessage() => MessageType = AddInProtocol.MessageTypes.OpenManager;
    public string Reason { get; set; } = string.Empty;
}

public sealed class PingMessage : AddInMessage
{
    public PingMessage() => MessageType = AddInProtocol.MessageTypes.Ping;
}

public sealed class ShutdownMessage : AddInMessage
{
    public ShutdownMessage() => MessageType = AddInProtocol.MessageTypes.Shutdown;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DisconnectMessage : AddInMessage
{
    public DisconnectMessage() => MessageType = AddInProtocol.MessageTypes.Disconnect;
    public string Reason { get; set; } = string.Empty;
}

public sealed class DesktopStatusMessage : AddInMessage
{
    public DesktopStatusMessage() => MessageType = AddInProtocol.MessageTypes.DesktopStatus;
    public bool Connected { get; set; }
    public string Status { get; set; } = string.Empty;
    public string DesktopVersion { get; set; } = string.Empty;
}

public sealed class ErrorMessage : AddInMessage
{
    public ErrorMessage() => MessageType = AddInProtocol.MessageTypes.Error;
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
}

public sealed class SelectedPartContext
{
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
}

public sealed class SelectedPartsContext
{
    public string Status { get; set; } = ContextAvailability.Unknown;
    public string Detail { get; set; } = string.Empty;
    public List<SelectedPartContext> Items { get; set; } = new List<SelectedPartContext>();
}

public sealed class EplanContextMessage : AddInMessage
{
    public EplanContextMessage() => MessageType = AddInProtocol.MessageTypes.EplanContext;
    public ContextValue EplanVersion { get; set; } = ContextValue.Unknown("Not collected.");
    public ContextValue EplanProcessId { get; set; } = ContextValue.Unknown("Not collected.");
    public ContextValue ProjectName { get; set; } = ContextValue.Unknown("Not collected.");
    public ContextValue ProjectPath { get; set; } = ContextValue.Unknown("Not collected.");
    public ContextValue ProjectDirectory { get; set; } = ContextValue.Unknown("Not collected.");
    public ContextValue PageName { get; set; } = ContextValue.Unknown("Not collected.");
    public ContextValue SelectionSummary { get; set; } = ContextValue.Unknown("Not collected.");
    public ContextValue ActivePartsDatabase { get; set; } = ContextValue.Unknown("Not collected.");
    public SelectedPartsContext SelectedParts { get; set; } = new SelectedPartsContext();
}

public sealed class AddInProtocolException : JsonException
{
    public AddInProtocolException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

public static class AddInJson
{
    private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Include,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        TypeNameHandling = TypeNameHandling.None,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        Formatting = Formatting.None
    };

    public static string Serialize(AddInMessage message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        ValidateEnvelope(message);
        return JsonConvert.SerializeObject(message, Settings);
    }

    public static MessageHeader ReadHeader(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new JsonException("The Add-In message is empty.");
        var value = JObject.Parse(json);
        DateTimeOffset timestamp;
        var timestampText = value["timestamp"]?.ToString() ?? string.Empty;
        if (!DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
            timestamp = default(DateTimeOffset);
        var header = new MessageHeader
        {
            ProtocolVersion = value.Value<string>("protocolVersion") ?? string.Empty,
            MessageType = value.Value<string>("messageType") ?? string.Empty,
            InstanceId = value.Value<string>("instanceId") ?? string.Empty,
            Timestamp = timestamp
        };
        ValidateEnvelope(header);
        return header;
    }

    public static T Deserialize<T>(string json) where T : AddInMessage
    {
        var message = JsonConvert.DeserializeObject<T>(json, Settings)
            ?? throw new JsonException("The Add-In message deserialized to null.");
        ValidateEnvelope(message);
        return message;
    }

    public static AddInMessage DeserializeKnown(string json)
    {
        var header = ReadHeader(json);
        if (!string.Equals(header.ProtocolVersion, AddInProtocol.Version, StringComparison.Ordinal))
            throw new AddInProtocolException(AddInProtocol.ErrorCodes.ProtocolMismatch,
                "Unsupported Add-In protocol version '" + header.ProtocolVersion + "'. Expected '" + AddInProtocol.Version + "'.");

        AddInMessage message;
        switch (header.MessageType)
        {
            case AddInProtocol.MessageTypes.Hello: message = Deserialize<HelloMessage>(json); break;
            case AddInProtocol.MessageTypes.OpenManager: message = Deserialize<OpenManagerMessage>(json); break;
            case AddInProtocol.MessageTypes.EplanContext: message = Deserialize<EplanContextMessage>(json); break;
            case AddInProtocol.MessageTypes.Ping: message = Deserialize<PingMessage>(json); break;
            case AddInProtocol.MessageTypes.Shutdown: message = Deserialize<ShutdownMessage>(json); break;
            case AddInProtocol.MessageTypes.Disconnect: message = Deserialize<DisconnectMessage>(json); break;
            case AddInProtocol.MessageTypes.DesktopStatus: message = Deserialize<DesktopStatusMessage>(json); break;
            case AddInProtocol.MessageTypes.Error: message = Deserialize<ErrorMessage>(json); break;
            default: throw new AddInProtocolException(AddInProtocol.ErrorCodes.InvalidMessage,
                "Unknown Add-In message type: " + header.MessageType);
        }
        var context = message as EplanContextMessage;
        if (context != null) ValidateContext(context);
        return message;
    }

    private static void ValidateContext(EplanContextMessage context)
    {
        var values = new[]
        {
            context.EplanVersion, context.EplanProcessId, context.ProjectName, context.ProjectPath, context.ProjectDirectory,
            context.PageName, context.SelectionSummary, context.ActivePartsDatabase
        };
        foreach (var value in values)
        {
            if (value == null || !ContextAvailability.IsKnown(value.Status))
                throw new AddInProtocolException(AddInProtocol.ErrorCodes.InvalidMessage,
                    "EPLAN context contains an unknown availability status.");
        }
        if (context.SelectedParts == null || !ContextAvailability.IsKnown(context.SelectedParts.Status))
            throw new AddInProtocolException(AddInProtocol.ErrorCodes.InvalidMessage,
                "Selected parts context contains an unknown availability status.");
    }

    private static void ValidateEnvelope(AddInMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ProtocolVersion)) throw new JsonException("protocolVersion is required.");
        if (string.IsNullOrWhiteSpace(message.MessageType)) throw new JsonException("messageType is required.");
        if (string.IsNullOrWhiteSpace(message.InstanceId)) throw new JsonException("instanceId is required.");
        if (message.Timestamp == default(DateTimeOffset)) throw new JsonException("timestamp is required.");
    }
}

public static class EplanVersionCompatibility
{
    public const string VerifiedVersion = "2.9.4.14642";

    public static bool IsSupported(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var normalized = version.Trim().Replace(',', '.').Split(' ')[0];
        return string.Equals(normalized, VerifiedVersion, StringComparison.Ordinal);
    }
}

public sealed class AddInConfiguration
{
    public string DesktopExecutablePath { get; set; } = string.Empty;
    public int ConnectTimeoutMilliseconds { get; set; } = 5000;

    public AddInConfigurationValidation Validate(string? addInDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(DesktopExecutablePath))
            return AddInConfigurationValidation.Invalid("desktopExecutablePath is required.");
        if (!Path.IsPathRooted(DesktopExecutablePath) && string.IsNullOrWhiteSpace(addInDirectory))
            return AddInConfigurationValidation.Invalid("A relative desktopExecutablePath requires the Add-In installation directory.");
        var fullPath = Path.GetFullPath(Path.IsPathRooted(DesktopExecutablePath)
            ? DesktopExecutablePath
            : Path.Combine(addInDirectory!, DesktopExecutablePath));
        if (!string.Equals(Path.GetFileName(fullPath), "EplanEdzManager.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            return AddInConfigurationValidation.Invalid("desktopExecutablePath must name EplanEdzManager.Desktop.exe.");
        if (!string.IsNullOrWhiteSpace(addInDirectory))
        {
            var expected = Path.GetFullPath(Path.Combine(addInDirectory, "..", "Desktop", "EplanEdzManager.Desktop.exe"));
            if (!string.Equals(fullPath, expected, StringComparison.OrdinalIgnoreCase))
                return AddInConfigurationValidation.Invalid("desktopExecutablePath must point to the sibling Desktop installation directory.");
        }
        if (ConnectTimeoutMilliseconds < 250 || ConnectTimeoutMilliseconds > 60000)
            return AddInConfigurationValidation.Invalid("connectTimeoutMilliseconds must be between 250 and 60000.");
        return AddInConfigurationValidation.Valid(fullPath);
    }
}

public sealed class AddInConfigurationValidation
{
    private AddInConfigurationValidation(bool isValid, string desktopExecutablePath, string error)
    {
        IsValid = isValid;
        DesktopExecutablePath = desktopExecutablePath;
        Error = error;
    }

    public bool IsValid { get; }
    public string DesktopExecutablePath { get; }
    public string Error { get; }

    public static AddInConfigurationValidation Valid(string path) => new AddInConfigurationValidation(true, path, string.Empty);
    public static AddInConfigurationValidation Invalid(string error) => new AddInConfigurationValidation(false, string.Empty, error);
}
