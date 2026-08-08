using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SharpNinja.Valhalla.Generations;

public sealed record ValhallaGenerationArtifactIdentity(
    string GenerationId,
    Uri ArtifactUri,
    string ArtifactSha256,
    long ByteLength)
{
    public static ValhallaGenerationArtifactIdentity From(ValhallaGraphGenerationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new(manifest.GenerationId, manifest.ArtifactUri, manifest.ArtifactSha256, manifest.ByteLength);
    }

    public static ValhallaGenerationArtifactIdentity From(ValhallaOverlayGenerationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new(manifest.GenerationId, manifest.ArtifactUri, manifest.ArtifactSha256, manifest.ByteLength);
    }
}

public interface IValhallaGenerationArtifactSource
{
    string Scheme { get; }

    ValueTask<Stream> OpenReadAsync(Uri artifactUri, CancellationToken cancellationToken);
}

public sealed class ValhallaGenerationArtifactSourceRegistry
{
    private readonly IReadOnlyDictionary<string, IValhallaGenerationArtifactSource> _sources;

    public ValhallaGenerationArtifactSourceRegistry(IEnumerable<IValhallaGenerationArtifactSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var registrations =
            new Dictionary<string, IValhallaGenerationArtifactSource>(StringComparer.OrdinalIgnoreCase);

        foreach (IValhallaGenerationArtifactSource source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            string scheme = source.Scheme?.Trim()
                ?? throw new ArgumentException("Artifact source scheme is required.", nameof(sources));
            if (scheme.Length == 0 || !registrations.TryAdd(scheme, source))
            {
                throw new ArgumentException(
                    $"Duplicate or empty artifact source scheme '{scheme}'.",
                    nameof(sources));
            }
        }

        _sources = registrations;
    }

    public IValhallaGenerationArtifactSource Resolve(Uri artifactUri)
    {
        Uri validated = ValhallaGenerationValidation.RequireArtifactUri(
            artifactUri,
            nameof(artifactUri));
        if (_sources.TryGetValue(validated.Scheme, out IValhallaGenerationArtifactSource? source))
        {
            return source;
        }

        throw new ValhallaGenerationException(
            ValhallaGenerationFailureCode.ArtifactSourceUnavailable,
            $"No Valhalla generation artifact source is registered for scheme '{validated.Scheme}'.");
    }
}

public sealed class FileValhallaGenerationArtifactSource : IValhallaGenerationArtifactSource
{
    public string Scheme => Uri.UriSchemeFile;

    public ValueTask<Stream> OpenReadAsync(Uri artifactUri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri validated = ValhallaGenerationValidation.RequireArtifactUri(
            artifactUri,
            nameof(artifactUri));
        if (!validated.IsFile)
        {
            throw new ValhallaGenerationException(
                ValhallaGenerationFailureCode.ArtifactSourceUnavailable,
                "The file artifact source accepts only file URIs.");
        }

        Stream stream = new FileStream(
            validated.LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }
}

public sealed record ValhallaMaterializedArtifact(
    string GenerationId,
    string ContentSha256,
    long ByteLength,
    string LocalPath);

/// <summary>
/// Content-addressed, atomic local materialization. Materialization never updates an active pointer;
/// activation is a separate host-owned operation after cohort validation.
/// </summary>
public sealed class ValhallaGenerationArtifactMaterializer
{
    private readonly string _rootDirectory;
    private readonly ValhallaGenerationArtifactSourceRegistry _sources;
    private readonly ConcurrentDictionary<string, Lazy<Task<ValhallaMaterializedArtifact>>> _work =
        new(StringComparer.OrdinalIgnoreCase);

    public ValhallaGenerationArtifactMaterializer(
        string rootDirectory,
        ValhallaGenerationArtifactSourceRegistry sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public async Task<ValhallaMaterializedArtifact> MaterializeAsync(
        ValhallaGenerationArtifactIdentity artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        string expectedSha = ValhallaGenerationValidation.RequireSha256(
            artifact.ArtifactSha256,
            nameof(artifact.ArtifactSha256));
        _ = ValhallaGenerationValidation.RequirePositiveLength(
            artifact.ByteLength,
            nameof(artifact.ByteLength));
        _ = ValhallaGenerationValidation.RequireArtifactUri(
            artifact.ArtifactUri,
            nameof(artifact.ArtifactUri));
        _ = ValhallaGenerationValidation.RequireIdentity(
            artifact.GenerationId,
            nameof(artifact.GenerationId));

        string key = $"{expectedSha}:{artifact.ByteLength}";
        Lazy<Task<ValhallaMaterializedArtifact>> lazy = _work.GetOrAdd(
            key,
            _ => new Lazy<Task<ValhallaMaterializedArtifact>>(
                () => MaterializeCoreAsync(artifact, expectedSha),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
            {
                _work.TryRemove(new KeyValuePair<string, Lazy<Task<ValhallaMaterializedArtifact>>>(
                    key,
                    lazy));
            }

            throw;
        }
    }

    private async Task<ValhallaMaterializedArtifact> MaterializeCoreAsync(
        ValhallaGenerationArtifactIdentity artifact,
        string expectedSha)
    {
        string contentDirectory = Path.Combine(_rootDirectory, expectedSha.ToLowerInvariant());
        string targetPath = Path.Combine(contentDirectory, "generation.bin");
        Directory.CreateDirectory(contentDirectory);

        if (File.Exists(targetPath))
        {
            return await ValidateAsync(artifact, expectedSha, targetPath).ConfigureAwait(false);
        }

        string temporaryPath = Path.Combine(
            contentDirectory,
            $".generation.{Guid.NewGuid():N}.tmp");
        try
        {
            IValhallaGenerationArtifactSource source = _sources.Resolve(artifact.ArtifactUri);
            await using Stream input = await source
                .OpenReadAsync(artifact.ArtifactUri, CancellationToken.None)
                .ConfigureAwait(false);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, CancellationToken.None).ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            ValhallaMaterializedArtifact validated =
                await ValidateAsync(artifact, expectedSha, temporaryPath).ConfigureAwait(false);
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                return await ValidateAsync(artifact, expectedSha, targetPath).ConfigureAwait(false);
            }

            return validated with { LocalPath = targetPath };
        }
        catch (ValhallaGenerationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ValhallaGenerationException(
                ValhallaGenerationFailureCode.ArtifactAcquisitionFailed,
                $"Valhalla generation '{artifact.GenerationId}' could not be acquired from scheme "
                    + $"'{artifact.ArtifactUri.Scheme}'.",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<ValhallaMaterializedArtifact> ValidateAsync(
        ValhallaGenerationArtifactIdentity artifact,
        string expectedSha,
        string path)
    {
        var info = new FileInfo(path);
        if (info.Length != artifact.ByteLength)
        {
            throw new ValhallaGenerationException(
                ValhallaGenerationFailureCode.ArtifactIntegrityMismatch,
                $"Valhalla generation '{artifact.GenerationId}' length did not match its manifest.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, CancellationToken.None).ConfigureAwait(false);
        string actualSha = Convert.ToHexString(hash);
        if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValhallaGenerationException(
                ValhallaGenerationFailureCode.ArtifactIntegrityMismatch,
                $"Valhalla generation '{artifact.GenerationId}' checksum did not match its manifest.");
        }

        return new(artifact.GenerationId, actualSha, info.Length, Path.GetFullPath(path));
    }
}
