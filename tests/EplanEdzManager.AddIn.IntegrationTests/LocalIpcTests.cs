using System.IO.Pipes;
using System.Text;
using EplanEdzManager.AddIn.Protocol;
using EplanEdzManager.Desktop.Integration;
using Xunit;

namespace EplanEdzManager.AddIn.IntegrationTests;

public sealed class LocalIpcTests
{
    [Fact]
    public async Task Pipe_client_round_trips_a_protocol_message_with_current_user_server()
    {
        var pipeName = "EplanEdzManager.Test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var request = AddInJson.DeserializeKnown((await reader.ReadLineAsync())!);
            Assert.IsType<OpenManagerMessage>(request);
            await writer.WriteLineAsync(AddInJson.Serialize(new DesktopStatusMessage
            {
                InstanceId = "desktop-primary",
                Connected = true,
                Status = "Ready"
            }));
        });

        var result = await LocalPipeClient.SendAsync(pipeName,
            new OpenManagerMessage { InstanceId = "eplan-42", Reason = "EplanMenu" },
            TimeSpan.FromSeconds(2), CancellationToken.None);

        var status = Assert.IsType<DesktopStatusMessage>(result);
        Assert.True(status.Connected);
        await serverTask;
    }

    [Fact]
    public async Task Pipe_client_reports_connection_timeout_without_hanging()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => LocalPipeClient.SendAsync(
            "EplanEdzManager.Missing." + Guid.NewGuid().ToString("N"),
            new PingMessage { InstanceId = "i" },
            TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Contains("connect", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pipe_client_times_out_when_connected_server_does_not_respond()
    {
        var pipeName = "EplanEdzManager.Silent." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await Task.Delay(500);
            server.Dispose();
        });

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => LocalPipeClient.SendAsync(
            pipeName,
            new PingMessage { InstanceId = "i" },
            TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Contains("respond", exception.Message, StringComparison.OrdinalIgnoreCase);
        await serverTask;
    }

    [Fact]
    public void Endpoint_names_are_deterministic_per_user_session_and_do_not_expose_raw_identity()
    {
        var first = DesktopEndpoint.ForCurrentUserSession();
        var second = DesktopEndpoint.ForCurrentUserSession();

        Assert.Equal(first.PipeName, second.PipeName);
        Assert.Equal(first.MutexName, second.MutexName);
        Assert.StartsWith("EplanEdzManager.Desktop.", first.PipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, first.PipeName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_one_desktop_instance_can_hold_the_endpoint_lease()
    {
        var name = "Local\\EplanEdzManager.Test." + Guid.NewGuid().ToString("N");
        using var first = DesktopInstanceLease.TryAcquire(name);
        using var second = DesktopInstanceLease.TryAcquire(name);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public async Task Desktop_server_accepts_multiple_sequential_addin_messages()
    {
        var pipeName = "EplanEdzManager.DesktopTest." + Guid.NewGuid().ToString("N");
        var received = new List<string>();
        await using var server = new DesktopIpcServer(pipeName, message =>
        {
            lock (received) received.Add(message.MessageType);
            return Task.FromResult(new DesktopStatusMessage
            {
                InstanceId = "desktop-primary",
                Connected = true,
                Status = "Ready"
            });
        });
        server.Start();

        var messages = new AddInMessage[]
        {
            new HelloMessage { InstanceId = "eplan-a" },
            new EplanContextMessage { InstanceId = "eplan-a" },
            new OpenManagerMessage { InstanceId = "eplan-a" }
        };
        foreach (var message in messages)
        {
            var response = await LocalPipeClient.SendAsync(pipeName, message, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.IsType<DesktopStatusMessage>(response);
        }

        Assert.Equal(new[] { "Hello", "EplanContext", "OpenManager" }, received);
    }

    [Fact]
    public async Task Desktop_server_drops_oversized_frame_and_accepts_next_client()
    {
        var pipeName = "EplanEdzManager.Oversized." + Guid.NewGuid().ToString("N");
        await using var server = CreateReadyServer(pipeName, TimeSpan.FromSeconds(2));
        server.Start();

        using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(new string('x', LocalPipeFraming.MaximumFrameCharacters + 1));
        }

        var recovered = await LocalPipeClient.SendAsync(pipeName, new PingMessage { InstanceId = "next" },
            TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.IsType<DesktopStatusMessage>(recovered);
    }

    [Fact]
    public async Task Desktop_server_times_out_silent_client_and_accepts_next_client()
    {
        var pipeName = "EplanEdzManager.SilentClient." + Guid.NewGuid().ToString("N");
        await using var server = CreateReadyServer(pipeName, TimeSpan.FromMilliseconds(100));
        server.Start();

        using (var silent = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await silent.ConnectAsync(2000);
            await Task.Delay(300);
        }

        var recovered = await LocalPipeClient.SendAsync(pipeName, new PingMessage { InstanceId = "next" },
            TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.IsType<DesktopStatusMessage>(recovered);
    }

    [Fact]
    public void Lease_can_be_reacquired_after_primary_exits()
    {
        var name = "Local\\EplanEdzManager.RecoveryTest." + Guid.NewGuid().ToString("N");
        using (var first = DesktopInstanceLease.TryAcquire(name)) Assert.True(first.IsPrimary);
        using var recovered = DesktopInstanceLease.TryAcquire(name);
        Assert.True(recovered.IsPrimary);
    }

    private static DesktopIpcServer CreateReadyServer(string pipeName, TimeSpan readTimeout) => new(
        pipeName,
        _ => Task.FromResult(new DesktopStatusMessage
        {
            InstanceId = "desktop-primary",
            Connected = true,
            Status = "Ready"
        }),
        readTimeout);
}
