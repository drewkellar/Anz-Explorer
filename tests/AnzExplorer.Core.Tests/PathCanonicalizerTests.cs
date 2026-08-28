using AnzExplorer.Core;

namespace AnzExplorer.Core.Tests;

public sealed class PathCanonicalizerTests
{
    [Fact]
    public void Canonicalize_RemovesTrailingSeparatorFromNonRoot()
    {
        var input = Path.Combine(Path.GetTempPath(), "AnzExplorer", "folder") + Path.DirectorySeparatorChar;
        var result = PathCanonicalizer.Canonicalize(input);
        Assert.False(Path.EndsInDirectorySeparator(result));
    }

    [Fact]
    public void IsSameOrDescendant_DoesNotAcceptSharedPrefixSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "AnzExplorer", "foo");
        var sibling = Path.Combine(Path.GetTempPath(), "AnzExplorer", "foobar");
        Assert.False(PathCanonicalizer.IsSameOrDescendant(sibling, root));
    }
}

