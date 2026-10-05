using EplanEdzManager.AddIn.Protocol;

namespace EplanEdzManager.Desktop.Integration;

public sealed class DesktopContextRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, EplanConnectionState> _connections = new(StringComparer.Ordinal);
    private string? _currentInstanceId;

    public IReadOnlyDictionary<string, EplanConnectionState> Connections
    {
        get { lock (_sync) return new Dictionary<string, EplanConnectionState>(_connections, StringComparer.Ordinal); }
    }

    public EplanConnectionState? Current
    {
        get
        {
            lock (_sync)
                return _currentInstanceId is not null && _connections.TryGetValue(_currentInstanceId, out var state)
                    ? state
                    : null;
        }
    }

    public EplanConnectionState Apply(AddInMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            if (!_connections.TryGetValue(message.InstanceId, out var state))
            {
                state = new EplanConnectionState(message.InstanceId);
                _connections.Add(message.InstanceId, state);
            }

            state.LastSeen = message.Timestamp;
            switch (message)
            {
                case HelloMessage hello:
                    state.Connected = true;
                    state.EplanProcessId = hello.EplanProcessId;
                    state.EplanVersion = hello.EplanVersion;
                    state.DisconnectReason = string.Empty;
                    _currentInstanceId = state.InstanceId;
                    break;
                case EplanContextMessage context:
                    state.Connected = true;
                    state.Context = context;
                    state.DisconnectReason = string.Empty;
                    _currentInstanceId = state.InstanceId;
                    break;
                case OpenManagerMessage:
                case PingMessage:
                    state.Connected = true;
                    state.DisconnectReason = string.Empty;
                    _currentInstanceId = state.InstanceId;
                    break;
                case DisconnectMessage disconnect:
                    state.Connected = false;
                    state.DisconnectReason = disconnect.Reason;
                    if (_currentInstanceId == state.InstanceId)
                        _currentInstanceId = _connections.Values
                            .Where(item => item.Connected)
                            .OrderByDescending(item => item.LastSeen)
                            .Select(item => item.InstanceId)
                            .FirstOrDefault();
                    break;
            }
            return state;
        }
    }
}

public sealed class EplanConnectionState
{
    internal EplanConnectionState(string instanceId) => InstanceId = instanceId;

    public string InstanceId { get; }
    public bool Connected { get; internal set; }
    public int EplanProcessId { get; internal set; }
    public string EplanVersion { get; internal set; } = string.Empty;
    public DateTimeOffset LastSeen { get; internal set; }
    public string DisconnectReason { get; internal set; } = string.Empty;
    public EplanContextMessage? Context { get; internal set; }
}
