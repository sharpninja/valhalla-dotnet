using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Storage;

public sealed class IntermediateStoreSourceContractTests
{
    [Theory]
    [InlineData("IntermediateBlobStore.cs")]
    [InlineData("IntermediateSequenceStore.cs")]
    public async Task DataSegments_BufferWritesUntilCompletionBarrier(string sourceFileName)
    {
        string sourcePath = FindRepositoryFile(
            "src",
            "SharpNinja.Valhalla.Generation",
            "Storage",
            sourceFileName);
        string source = await File.ReadAllTextAsync(
            sourcePath,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            "FileOptions.RandomAccess | FileOptions.WriteThrough",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "stream.Flush(flushToDisk: true);",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Repository source file was not found.",
            Path.Combine(parts));
    }
}
