using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class GenerationArtifactWriter
{
    private const string FixedCorePropertiesPath =
        "package/services/metadata/core-properties/core-properties.psmdcp";

    private static readonly DateTimeOffset FixedZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex CorePropertiesRelationshipIdPattern = new(
        "(?<prefix><Relationship[^>]+metadata/core-properties[^>]+Id=\")[^\"]+(?<suffix>\")",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static void CanonicalizeNugetPackage(string packagePath)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? originalCorePropertiesPath = null;

        using (var packageStream = File.OpenRead(packagePath))
        using (var package = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (var entry in package.Entries)
            {
                using var entryStream = entry.Open();
                using var buffer = new MemoryStream();
                entryStream.CopyTo(buffer);

                var entryName = entry.FullName.Replace('\\', '/');
                if (entryName.StartsWith(
                        "package/services/metadata/core-properties/",
                        StringComparison.Ordinal) &&
                    entryName.EndsWith(".psmdcp", StringComparison.Ordinal))
                {
                    originalCorePropertiesPath = entryName;
                    entryName = FixedCorePropertiesPath;
                }

                entries.Add(entryName, buffer.ToArray());
            }
        }

        if (originalCorePropertiesPath is not null &&
            !string.Equals(
                originalCorePropertiesPath,
                FixedCorePropertiesPath,
                StringComparison.Ordinal))
        {
            RewritePackageRelationship(
                entries,
                "_rels/.rels",
                originalCorePropertiesPath,
                FixedCorePropertiesPath);
            RewritePackageRelationship(
                entries,
                "[Content_Types].xml",
                originalCorePropertiesPath,
                FixedCorePropertiesPath);
        }

        NormalizeCorePropertiesRelationship(entries);

        var temporaryPath = packagePath + ".canonical";
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var entry = package.CreateEntry(pair.Key, CompressionLevel.SmallestSize);
                    entry.LastWriteTime = FixedZipTimestamp;
                    entry.ExternalAttributes = 0;

                    using var entryStream = entry.Open();
                    entryStream.Write(pair.Value);
                }
            }

            File.Move(temporaryPath, packagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    internal static void GenerateGenerationSbom(
        IEnumerable<string> projectAssetsPaths,
        string outputPath,
        string version,
        string sourceCommit)
    {
        var components = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var assetsPath in projectAssetsPaths)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries))
            {
                continue;
            }

            foreach (var library in libraries.EnumerateObject())
            {
                var separatorIndex = library.Name.LastIndexOf('/');
                if (separatorIndex <= 0 || separatorIndex == library.Name.Length - 1)
                {
                    continue;
                }

                if (!library.Value.TryGetProperty("type", out var type) ||
                    !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = library.Name[..separatorIndex];
                var packageVersion = library.Name[(separatorIndex + 1)..];
                components[library.Name] = new
                {
                    type = "library",
                    name,
                    version = packageVersion,
                    purl = $"pkg:nuget/{Uri.EscapeDataString(name)}@{packageVersion}",
                };
            }
        }

        var sbom = new
        {
            bomFormat = "CycloneDX",
            specVersion = "1.5",
            version = 1,
            metadata = new
            {
                component = new
                {
                    type = "application",
                    name = "SharpNinja.Valhalla.Generation",
                    version,
                    properties = new[]
                    {
                        new
                        {
                            name = "valhalla-dotnet:source-commit",
                            value = sourceCommit,
                        },
                        new
                        {
                            name = "valhalla-dotnet:upstream",
                            value = "Valhalla 3.8.3 a60c7cbfc83e073f50887cd27e0109d02e6b64e5",
                        },
                    },
                },
            },
            components = components.Values,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(sbom, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static void WriteArtifactManifest(
        string artifactDirectory,
        string outputPath,
        string version,
        string sourceCommit)
    {
        var files = Directory
            .EnumerateFiles(artifactDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, outputPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new
            {
                path = Path.GetRelativePath(artifactDirectory, path).Replace('\\', '/'),
                bytes = new FileInfo(path).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            })
            .ToArray();

        var manifest = new
        {
            schemaVersion = 1,
            packageVersion = version,
            sourceCommit,
            upstreamVersion = "3.8.3",
            upstreamCommit = "a60c7cbfc83e073f50887cd27e0109d02e6b64e5",
            files,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static void WriteUpstreamParityReport(
        string manifestPath,
        string outputPath,
        string sourceCommit)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var stages = root.GetProperty("stages").EnumerateArray().ToArray();
        var tests = root.GetProperty("upstreamTests").EnumerateArray().ToArray();

        var report = new StringBuilder()
            .AppendLine("# Valhalla 3.8.3 generation parity")
            .AppendLine()
            .AppendLine($"- Managed source commit: {sourceCommit}")
            .AppendLine("- Upstream version: 3.8.3")
            .AppendLine("- Upstream commit: a60c7cbfc83e073f50887cd27e0109d02e6b64e5")
            .AppendLine($"- Canonical stages: {stages.Length}")
            .AppendLine($"- Applicable upstream tests: {tests.Length}")
            .AppendLine()
            .AppendLine("## Stages")
            .AppendLine();

        foreach (var stage in stages)
        {
            report
                .Append("- ")
                .Append(stage.GetProperty("name").GetString())
                .Append(": ")
                .AppendLine(stage.GetProperty("disposition").GetString());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(
            outputPath,
            report.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static string ComputeSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void NormalizeCorePropertiesRelationship(
        IDictionary<string, byte[]> entries)
    {
        const string relationshipPath = "_rels/.rels";
        if (!entries.TryGetValue(relationshipPath, out var bytes))
        {
            return;
        }

        var text = Encoding.UTF8.GetString(bytes);
        var normalized = CorePropertiesRelationshipIdPattern.Replace(
            text,
            "${prefix}RCOREPROPERTIES${suffix}");
        entries[relationshipPath] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(normalized);
    }

    private static void RewritePackageRelationship(
        IDictionary<string, byte[]> entries,
        string entryPath,
        string originalPath,
        string replacementPath)
    {
        if (!entries.TryGetValue(entryPath, out var bytes))
        {
            return;
        }

        var text = Encoding.UTF8.GetString(bytes)
            .Replace(originalPath, replacementPath, StringComparison.Ordinal)
            .Replace("/" + originalPath, "/" + replacementPath, StringComparison.Ordinal);
        entries[entryPath] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(text);
    }
}
