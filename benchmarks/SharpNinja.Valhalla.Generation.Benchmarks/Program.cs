using BenchmarkDotNet.Running;
using SharpNinja.Valhalla.Generation.Benchmarks;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static async Task<int> RunAsync(
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken)
{
    if (arguments.Count == 0)
    {
        WriteUsage();
        return 2;
    }

    if (string.Equals(arguments[0], "micro", StringComparison.Ordinal))
    {
        BenchmarkRunner.Run<GenerationKernelBenchmarks>();
        return 0;
    }

    try
    {
        var options = ParseOptions(arguments.Skip(1).ToArray());
        var configurationPath = RequireOption(options, "config");
        var outputDirectory = Path.GetFullPath(RequireOption(options, "output"));
        var configuration =
            ProcessGenerationBenchmarkHarness.LoadConfiguration(configurationPath);
        var command = arguments[0];
        var enforceQualification =
            string.Equals(command, "qualify-nashville", StringComparison.Ordinal) ||
            string.Equals(command, "qualify-lower48", StringComparison.Ordinal);
        var lower48 =
            string.Equals(command, "qualify-lower48", StringComparison.Ordinal);

        if (!string.Equals(command, "benchmark-nashville", StringComparison.Ordinal) &&
            !enforceQualification)
        {
            WriteUsage();
            return 2;
        }

        var harness = new ProcessGenerationBenchmarkHarness();
        await harness.RunAsync(
                configuration,
                outputDirectory,
                options.GetValueOrDefault("managed-image"),
                options.GetValueOrDefault("official-image"),
                enforceQualification,
                lower48,
                cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }
    catch (GenerationBenchmarkConfigurationException exception)
    {
        Console.Error.WriteLine($"configuration: {exception.Message}");
        return 2;
    }
    catch (GenerationQualificationException exception)
    {
        Console.Error.WriteLine($"qualification: {exception.Message}");
        return 3;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("canceled");
        return 4;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            $"unexpected: {exception.GetType().Name}: {exception.Message}");
        return 5;
    }
}

static Dictionary<string, string> ParseOptions(IReadOnlyList<string> arguments)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Count; index += 2)
    {
        var name = arguments[index];
        if (!name.StartsWith("--", StringComparison.Ordinal) ||
            index + 1 >= arguments.Count)
        {
            throw new GenerationBenchmarkConfigurationException(
                "Options must use --name value pairs.");
        }

        var key = name[2..];
        if (!options.TryAdd(key, arguments[index + 1]))
        {
            throw new GenerationBenchmarkConfigurationException(
                $"Duplicate option: --{key}");
        }
    }

    return options;
}

static string RequireOption(
    IReadOnlyDictionary<string, string> options,
    string name)
{
    if (!options.TryGetValue(name, out var value) ||
        string.IsNullOrWhiteSpace(value))
    {
        throw new GenerationBenchmarkConfigurationException(
            $"Required option is missing: --{name}");
    }

    return value;
}

static void WriteUsage()
{
    Console.Error.WriteLine(
        "Usage: generation-benchmarks micro | " +
        "benchmark-nashville|qualify-nashville|qualify-lower48 " +
        "--config <json> --output <directory> " +
        "[--managed-image <image>] [--official-image <image>]");
}
