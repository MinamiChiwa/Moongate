using Xunit;

namespace MabiTool.MusicLibrary.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MusicLibraryTestCollection
{
    public const string Name = "Music library SQLite tests";
}
