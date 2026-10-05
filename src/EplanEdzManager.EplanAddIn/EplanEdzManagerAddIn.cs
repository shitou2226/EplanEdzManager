using System;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Gui;

namespace EplanEdzManager.EplanAddIn;

public sealed class EplanEdzManagerAddIn : IEplAddIn, IEplAddInShadowCopy
{
    public void OnBeforeInit(string strOriginalAssemblyPath)
    {
        try { AddInRuntime.SetOriginalAssemblyPath(strOriginalAssemblyPath); }
        catch (Exception exception) { AddInLog.Error(AddInRuntime.SessionId, "OnBeforeInit", exception); }
    }

    public bool OnRegister(ref bool bLoadOnStart)
    {
        try
        {
            bLoadOnStart = true;
            AddInLog.Information(AddInRuntime.SessionId, "OnRegister", "loadOnStart=true");
            return true;
        }
        catch (Exception exception)
        {
            AddInLog.Error(AddInRuntime.SessionId, "OnRegister", exception);
            return false;
        }
    }

    public bool OnUnregister()
    {
        try
        {
            DesktopConnector.NotifyDisconnect("Add-In unregistered");
            AddInLog.Information(AddInRuntime.SessionId, "OnUnregister");
        }
        catch (Exception exception) { AddInLog.Error(AddInRuntime.SessionId, "OnUnregister", exception); }
        return true;
    }

    public bool OnInit()
    {
        try
        {
            var version = AddInRuntime.EplanVersion;
            if (!AddInRuntime.IsSupportedRuntime)
                AddInLog.Warning(AddInRuntime.SessionId, "UnsupportedRuntime", "version=" + version);
            else
                AddInLog.Information(AddInRuntime.SessionId, "OnInit", "version=" + version);
        }
        catch (Exception exception) { AddInLog.Error(AddInRuntime.SessionId, "OnInit", exception); }
        return true;
    }

    public bool OnInitGui()
    {
        try
        {
            using (var menu = new Menu()) menu.AddMenuItem("EPLAN EDZ Manager", OpenManagerAction.ActionName);
            AddInLog.Information(AddInRuntime.SessionId, "OnInitGui", "action=" + OpenManagerAction.ActionName + ";menuItemAdded=true");
        }
        catch (Exception exception) { AddInLog.Error(AddInRuntime.SessionId, "OnInitGui", exception); }
        return true;
    }

    public bool OnExit()
    {
        try
        {
            DesktopConnector.NotifyDisconnect("EPLAN closing");
            AddInLog.Information(AddInRuntime.SessionId, "OnExit");
        }
        catch (Exception exception) { AddInLog.Error(AddInRuntime.SessionId, "OnExit", exception); }
        return true;
    }
}
