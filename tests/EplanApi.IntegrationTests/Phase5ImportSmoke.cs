using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using EplanEdzManager.EplanApi;

namespace EplanApi.IntegrationTests;

internal static class Phase5ImportSmoke
{
    public static int Run(
        string selectedEdz,
        string outputDirectory,
        string temporaryRoot,
        string variantBin,
        string platformBin,
        string schemeName)
    {
        var target = Path.Combine(temporaryRoot, "TargetImportTest.mdb");
        var service = new SafePartsDatabaseImporter(variantBin, platformBin, temporaryRoot, schemeName);
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var fixture = Path.Combine(outputDirectory, "EmptyTargetTemplate.mdb");
            service.CreateEmptyIntegrationTestDatabase(target);
            File.Copy(target, fixture, true);
            File.Delete(target);
            var conflictTarget = Path.Combine(temporaryRoot, "ConflictTargetTemplate.mdb");
            service.CreateConflictIntegrationTestDatabase(conflictTarget, selectedEdz, "DR-100-24");
            File.Copy(conflictTarget, Path.Combine(outputDirectory, "ConflictTargetTemplate.mdb"), true);
            var report = service.RunSmokeInvestigation(target, selectedEdz, "DR-100-24", "1");
            File.WriteAllText(
                Path.Combine(outputDirectory, "Phase5SmokeReport.json"),
                new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(report));

            Require(report.BeforeCount == 0, "The official temporary target database was not empty.");
            Require(report.AfterCount == 1, "One requested part was not added to the empty target database.");
            Require(report.ReopenedPartFound, "The requested part was not found after close/reopen.");
            Require(report.MetadataVerified, "Requested metadata was not verified after close/reopen.");
            Require(report.PictureReferencesVerified, "Picture references were not verified.");
            Require(report.MacroReferencesVerified, "EMA macro references were not verified.");
            Require(report.RepeatedAppendCount == report.AfterCount, "AppendNewRecords created an uncontrolled duplicate.");
            Require(report.VerifiedImportModes.SequenceEqual(new[] { "AppendNewRecords", "UpdateExistingRecords", "UpdateAndAppend" }),
                "All documented EPLAN 2.9 import modes were not runtime verified.");
            Console.WriteLine("PHASE 5 API SMOKE PASS");
            return 0;
        }
        finally
        {
            service.Dispose();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
