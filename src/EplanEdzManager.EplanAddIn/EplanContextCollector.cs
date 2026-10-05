using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.HEServices;
using EplanEdzManager.AddIn.Protocol;

namespace EplanEdzManager.EplanAddIn;

internal static class EplanContextCollector
{
    public static EplanContextMessage Collect()
    {
        var message = new EplanContextMessage
        {
            InstanceId = AddInRuntime.SessionId,
            EplanVersion = Value(() => AddInRuntime.EplanVersion, "EPLAN runtime version is unavailable."),
            EplanProcessId = ContextValue.Available(Process.GetCurrentProcess().Id.ToString())
        };

        CollectSelection(message);
        message.ActivePartsDatabase = CollectActivePartsDatabase();
        return message;
    }

    private static void CollectSelection(EplanContextMessage message)
    {
        try
        {
            var selectionSet = new SelectionSet
            {
                LockProjectByDefault = false,
                LockSelectionByDefault = false
            };

            var project = selectionSet.GetCurrentProject(false);
            if (project is null)
            {
                message.ProjectName = ContextValue.Unavailable("No active project is open.");
                message.ProjectPath = ContextValue.Unavailable("No active project is open.");
                message.ProjectDirectory = ContextValue.Unavailable("No active project is open.");
            }
            else
            {
                message.ProjectName = Value(() => project.ProjectName, "The active project name could not be read.");
                message.ProjectPath = Value(() => project.ProjectLinkFilePath, "The active project path could not be read.");
                message.ProjectDirectory = DirectoryValue(message.ProjectPath);
            }

            var page = selectionSet.CurrentlyEdited as Page;
            if (page is null)
            {
                var opened = selectionSet.OpenedPages;
                page = opened != null && opened.Length > 0 ? opened[0] : null;
            }
            message.PageName = page is null
                ? ContextValue.Unavailable("No page is open in the graphical editor.")
                : Value(() => page.Name, "The current page name could not be read.");

            var selection = selectionSet.Selection ?? Array.Empty<StorableObject>();
            var typeSummary = selection.GroupBy(item => item.GetType().Name)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key + "=" + group.Count());
            message.SelectionSummary = ContextValue.Available(selection.Length + " object(s)" +
                (selection.Length == 0 ? string.Empty : ": " + string.Join(", ", typeSummary)));
            message.SelectedParts = CollectSelectedParts(selection);
        }
        catch (Exception exception)
        {
            message.ProjectName = ContextValue.Unknown("SelectionSet failed: " + exception.Message);
            message.ProjectPath = ContextValue.Unknown("SelectionSet failed.");
            message.ProjectDirectory = ContextValue.Unknown("SelectionSet failed.");
            message.PageName = ContextValue.Unknown("SelectionSet failed.");
            message.SelectionSummary = ContextValue.Unknown("SelectionSet failed.");
            message.SelectedParts = new SelectedPartsContext
            {
                Status = ContextAvailability.Unknown,
                Detail = "SelectionSet failed: " + exception.Message
            };
        }
    }

    private static SelectedPartsContext CollectSelectedParts(IEnumerable<StorableObject> selection)
    {
        var selectedObjects = selection.ToArray();
        if (selectedObjects.Length == 0)
            return new SelectedPartsContext { Status = ContextAvailability.Unavailable, Detail = "No objects are selected." };

        var parts = selectedObjects.OfType<Function>()
            .SelectMany(GetArticleOwners)
            .SelectMany(GetPartContexts)
            .Where(part => !string.IsNullOrWhiteSpace(part.PartNumber))
            .GroupBy(item => item.PartNumber + "\u001f" + item.Variant, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.PartNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Variant, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count == 0
            ? new SelectedPartsContext
            {
                Status = ContextAvailability.Unavailable,
                Detail = "The current selection contains no function article references."
            }
            : new SelectedPartsContext
            {
                Status = ContextAvailability.Available,
                Detail = "Manufacturer is not exposed because the selected function reference only provides part number and variant reliably.",
                Items = parts
            };
    }

    private static IEnumerable<Function> GetArticleOwners(Function selectedFunction)
    {
        yield return selectedFunction;

        Function parentFunction;
        try
        {
            parentFunction = selectedFunction.ParentFunction;
        }
        catch (Exception)
        {
            yield break;
        }

        if (parentFunction != null) yield return parentFunction;
    }

    private static IEnumerable<SelectedPartContext> GetPartContexts(Function function)
    {
        var parts = new List<SelectedPartContext>();
        try
        {
            parts.AddRange((function.ArticleReferences ?? Array.Empty<ArticleReference>())
                .Where(reference => !string.IsNullOrWhiteSpace(reference.PartNr))
                .Select(reference => new SelectedPartContext
                {
                    Manufacturer = string.Empty,
                    PartNumber = reference.PartNr,
                    Variant = reference.VariantNr ?? string.Empty
                }));
        }
        catch (Exception)
        {
            // The documented indexed properties below are the read-only fallback.
        }

        for (var index = 1; index <= 50; index++)
        {
            try
            {
                string partNumber = function.Properties[
                    Eplan.EplApi.DataModel.Properties.Function.FUNC_ARTICLE_PARTNR, index];
                if (string.IsNullOrWhiteSpace(partNumber)) continue;

                string variant = function.Properties[
                    Eplan.EplApi.DataModel.Properties.Function.FUNC_ARTICLE_VARIANT, index];
                parts.Add(new SelectedPartContext
                {
                    Manufacturer = string.Empty,
                    PartNumber = partNumber,
                    Variant = variant ?? string.Empty
                });
            }
            catch (Exception)
            {
                // A missing/unsupported property index must not break the EPLAN action.
            }
        }

        return parts;
    }

    private static ContextValue CollectActivePartsDatabase()
    {
        try
        {
            using (var partsService = new PartsService())
            {
                var value = partsService.PartsDatabase;
                if (string.IsNullOrWhiteSpace(value))
                    return ContextValue.Unavailable("EPLAN did not report an active parts database.");
                if (!Path.IsPathRooted(value) || value.IndexOf('=') >= 0 || value.IndexOf(';') >= 0)
                    return ContextValue.Unsupported("The active database is not a plain local file path; connection details are not transmitted.");
                return ContextValue.Available(value);
            }
        }
        catch (Exception exception)
        {
            return ContextValue.Unknown("The active parts database getter failed (" + exception.GetType().Name + "). Connection details were not collected.");
        }
    }

    private static ContextValue Value(Func<string> getter, string unavailableDetail)
    {
        try
        {
            var value = getter();
            return string.IsNullOrWhiteSpace(value) ? ContextValue.Unavailable(unavailableDetail) : ContextValue.Available(value);
        }
        catch (Exception exception)
        {
            return ContextValue.Unknown(unavailableDetail + " " + exception.Message);
        }
    }

    private static ContextValue DirectoryValue(ContextValue projectPath)
    {
        if (projectPath.Status != ContextAvailability.Available)
            return new ContextValue { Status = projectPath.Status, Detail = projectPath.Detail };

        try
        {
            var directory = Path.GetDirectoryName(projectPath.Value);
            return string.IsNullOrWhiteSpace(directory)
                ? ContextValue.Unavailable("The active project directory could not be derived.")
                : ContextValue.Available(directory);
        }
        catch (Exception exception)
        {
            return ContextValue.Unknown("The active project directory could not be derived (" + exception.GetType().Name + ").");
        }
    }
}
