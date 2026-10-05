using EplanEdzProbe;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await new ProbeApplication().RunAsync(args, Console.Out, Console.Error, cancellation.Token);

