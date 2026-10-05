using EplanEdzManager.Core.Identity;
using Xunit;

namespace EplanEdzManager.Core.Tests;

public sealed class StablePartIdentityTests
{
    [Fact]
    public void Primary_identity_is_normalized_and_does_not_depend_on_source_package()
    {
        var first = StablePartIdentity.Create(" Siemens  AG ", " 6ES7\t214 ", "1", "2025-package");
        var second = StablePartIdentity.Create("siemens ag", "6es7 214", "1", "2026-package");

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(StableIdentityKind.Primary, first.Kind);
    }

    [Fact]
    public void Variant_is_part_of_logical_identity()
    {
        var first = StablePartIdentity.Create("OMR", "CJ2M", "1", "CJ2M");
        var second = StablePartIdentity.Create("OMR", "CJ2M", "2", "CJ2M");

        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public void Package_fallback_is_explicit_and_missing_identity_is_rejected()
    {
        var fallback = StablePartIdentity.Create(null, null, null, "PK-123");

        Assert.Equal(StableIdentityKind.PackageFallback, fallback.Kind);
        Assert.Throws<InvalidOperationException>(() => StablePartIdentity.Create(null, null, null, null));
    }

    [Fact]
    public void Source_identity_distinguishes_paths_while_logical_identity_does_not()
    {
        var logicalA = StablePartIdentity.Create("SIEMENS", "6ES7", "1", "A");
        var logicalB = StablePartIdentity.Create("SIEMENS", "6ES7", "1", "B");
        var sourceA = PartSourceIdentity.Create(Path.Combine(Path.GetTempPath(), "a.edz"), "A", "items/partxml/a.xml");
        var sourceB = PartSourceIdentity.Create(Path.Combine(Path.GetTempPath(), "b.edz"), "B", "items/partxml/b.xml");

        Assert.Equal(logicalA.Value, logicalB.Value);
        Assert.NotEqual(sourceA, sourceB);
    }
}
