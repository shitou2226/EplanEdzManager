using System;
using System.Windows.Forms;
using Eplan.EplApi.ApplicationFramework;

namespace EplanEdzManager.EplanAddIn;

public sealed class OpenManagerAction : IEplAction
{
    public const string ActionName = "EplanEdzManager_Open";

    public bool Execute(ActionCallingContext ctx)
    {
        AddInLog.Information(AddInRuntime.SessionId, "OpenManagerExecuteStarted");
        try
        {
            if (!AddInRuntime.IsSupportedRuntime)
                throw new NotSupportedException("This Add-In supports EPLAN 2.9.x only. Detected: " + AddInRuntime.EplanVersion);
            DesktopConnector.OpenManager();
            return true;
        }
        catch (Exception exception)
        {
            AddInLog.Error(AddInRuntime.SessionId, "OpenManagerFailed", exception);
            try
            {
                MessageBox.Show(
                    "EPLAN EDZ Manager could not be opened.\n\n" + exception.Message + "\n\nLog: " + AddInLog.CurrentLogPath,
                    "EPLAN EDZ Manager",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception)
            {
                // The action boundary must remain fail-soft even if EPLAN is shutting down.
            }
            return false;
        }
    }

    public bool OnRegister(ref string Name, ref int Ordinal)
    {
        Name = ActionName;
        Ordinal = 20;
        AddInLog.Information(AddInRuntime.SessionId, "OpenManagerActionRegistered", "action=" + Name + ";ordinal=" + Ordinal);
        return true;
    }

    public void GetActionProperties(ref ActionProperties actionProperties)
    {
        // The 2.9.4 binary exposes no public Description setter although the XML example mentions one.
        // No action parameters are accepted, so there is nothing safe to add here.
    }
}
