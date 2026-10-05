using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eplan.EplApi.Starter;

namespace EplanEdzManager.EplanBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        BridgeBootstrapOptions? options = null;
        BridgeLogger? logger = null;
        string? temporaryRoot = null;
        var exitCode = 1;
        try
        {
            options = BridgeBootstrapOptions.Parse(args);
            logger = new BridgeLogger(options.SessionId);
            temporaryRoot = BridgeBootstrap.CreateTemporaryRoot(options.SessionId);
            logger.Write(options.SessionId, string.Empty, "CreatingTemporaryEnvironment", temporaryRoot);
            var temporaryVariantBin = BridgeBootstrap.PrepareTemporaryVariant(options.VariantBinDirectory, temporaryRoot, options.TargetMasterDataRoot);
            RegisterInstalledEplanAssemblyFallback(options.PlatformBinDirectory);
            exitCode = RunAfterPin(options, temporaryRoot, temporaryVariantBin, logger);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            logger?.Write(options?.SessionId ?? string.Empty, string.Empty, "FatalBridgeError", exception.GetType().Name + ": " + exception.Message);
            exitCode = 1;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryRoot) && !BridgeBootstrap.TryDeleteTemporaryRoot(temporaryRoot!, out var warning))
            {
                logger?.Write(options?.SessionId ?? string.Empty, string.Empty, "BRIDGE401 CleanupWarning", warning);
                if (exitCode == 0) exitCode = 2;
            }
            logger?.Dispose();
        }
        return exitCode;
    }

    private static void RegisterInstalledEplanAssemblyFallback(string platformBinDirectory)
    {
        var directory = Path.GetFullPath(platformBinDirectory);
        var starter = Path.Combine(directory, "Eplan.EplApi.Starteru.dll");
        if (!File.Exists(starter)) throw new FileNotFoundException("The installed EPLAN Starter API assembly was not found.", starter);
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("Eplan.", StringComparison.OrdinalIgnoreCase)) return null;
            var candidate = Path.Combine(directory, name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunAfterPin(BridgeBootstrapOptions options, string temporaryRoot, string temporaryVariantBin, BridgeLogger logger)
    {
        var resolver = new AssemblyResolver();
        resolver.SetBinPaths(options.PlatformBinDirectory, temporaryVariantBin);
        resolver.PinToEplan();
        using var server = new BridgeServer(options, temporaryRoot, temporaryVariantBin, logger);
        return server.Run();
    }
}
