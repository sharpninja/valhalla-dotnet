using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpNinja.Valhalla.Generation;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Mjolnir;

var pbf = args[0];
var work = args[1];
var tiles = args[2];
var outJson = args[3];
var memBudget = long.Parse(args[4]);
var scratchBudget = long.Parse(args[5]);
var dop = int.Parse(args[6]);
var heartbeatSeconds = args.Length > 7 && int.TryParse(args[7], out var hb) && hb > 0 ? hb : 10;

static void TryDelete(string path)
{
    for (var attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return;
        }
        catch (IOException) when (attempt < 4) { Thread.Sleep(500); }
        catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(500); }
    }
}

static long DirectorySizeBytes(string root)
{
    if (!Directory.Exists(root)) return 0;
    long total = 0;
    try
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; } catch { }
        }
    }
    catch { }
    return total;
}

static string InferStage(string workDir, string tilesDir)
{
    // Prefer the most "advanced" stage folder present (order matters).
    string[][] stages =
    [
        ["tile", "tiles", "bounded-tiles", "tile-write", "graph-tiles"],
        ["restriction", "restrictions", "pooled-restriction"],
        ["enhance", "enhancer", "pooled-enhance"],
        ["frontier", "pooled-frontier", "path-frontier", "graph-build"],
        ["pooled-semantic", "semantic", "canonical-metadata", "canonical-way-nodes", "node-incidence"],
        ["osm-intermediate", "osm-nodes", "osm-ways", "osm-relations", "pbf", "ingest"],
    ];
    string[] labels =
    [
        "tile-write",
        "restrictions",
        "enhance",
        "graph-frontier",
        "semantic",
        "pbf-ingestion",
    ];

    var dirs = new List<string>();
    void Collect(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                dirs.Add(d);
            dirs.Add(root);
        }
        catch { }
    }
    Collect(workDir);
    Collect(tilesDir);

    if (Directory.Exists(tilesDir))
    {
        try
        {
            if (Directory.EnumerateFiles(tilesDir, "*", SearchOption.AllDirectories).Any())
                return "tile-write";
        }
        catch { }
    }

    for (var i = 0; i < stages.Length; i++)
    {
        foreach (var marker in stages[i])
        {
            if (dirs.Any(d => d.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                return labels[i];
        }
    }

    return Directory.Exists(workDir) ? "startup-or-idle" : "starting";
}

TryDelete(work);
TryDelete(tiles);
if (Directory.Exists(work) || Directory.Exists(tiles))
{
    work += "-" + Guid.NewGuid().ToString("N")[..8];
    tiles += "-" + Guid.NewGuid().ToString("N")[..8];
}
Directory.CreateDirectory(work);
Directory.CreateDirectory(tiles);

using var proc = Process.GetCurrentProcess();
var sw = Stopwatch.StartNew();
bool ok = true;
string? fail = null;
string hash = "NONE";
long peakIntermediate = 0;
long peakWs = 0;
long peakPrivate = 0;
long peakGc = 0;
int tileCount = 0;
int selectedDop = 0;
long hwmMem = 0;
long hwmScratch = 0;
object? resource = null;

Console.WriteLine($"HEARTBEAT_CONFIG intervalSec={heartbeatSeconds} work={work} tiles={tiles} dop={dop} memBudgetBytes={memBudget}");
Console.Out.Flush();

using var cts = new CancellationTokenSource();
var sampler = Task.Run(async () =>
{
    var nextHeartbeat = TimeSpan.Zero;
    while (!cts.IsCancellationRequested)
    {
        try
        {
            proc.Refresh();
            // Peak measurement path unchanged: continuous high-water tracking.
            peakWs = Math.Max(peakWs, proc.PeakWorkingSet64);
            peakPrivate = Math.Max(peakPrivate, proc.PrivateMemorySize64);
            peakGc = Math.Max(peakGc, GC.GetTotalMemory(false));

            if (sw.Elapsed >= nextHeartbeat)
            {
                var stage = InferStage(work, tiles);
                var curWs = proc.WorkingSet64;
                var workBytes = DirectorySizeBytes(work);
                var tilesBytes = DirectorySizeBytes(tiles);
                var diskBytes = workBytes + tilesBytes;
                Console.WriteLine(
                    "HEARTBEAT " +
                    $"elapsedSec={sw.Elapsed.TotalSeconds:F1} " +
                    $"stage={stage} " +
                    $"wsGiB={curWs / (1024.0 * 1024 * 1024):F3} " +
                    $"peakWsGiB={peakWs / (1024.0 * 1024 * 1024):F3} " +
                    $"privateGiB={proc.PrivateMemorySize64 / (1024.0 * 1024 * 1024):F3} " +
                    $"gcGiB={peakGc / (1024.0 * 1024 * 1024):F3} " +
                    $"diskGiB={diskBytes / (1024.0 * 1024 * 1024):F3} " +
                    $"workGiB={workBytes / (1024.0 * 1024 * 1024):F3} " +
                    $"tilesGiB={tilesBytes / (1024.0 * 1024 * 1024):F3}");
                Console.Out.Flush();
                nextHeartbeat = sw.Elapsed + TimeSpan.FromSeconds(heartbeatSeconds);
            }
        }
        catch { }

        try { await Task.Delay(250, cts.Token); } catch { break; }
    }
});

try
{
    var request = new ManagedRoadGraphBuildRequest(
        [pbf],
        work,
        tiles,
        IntermediateStorageMode.MemoryMapped,
        memBudget,
        scratchBudget,
        new TileBuilderConfig
        {
            GridDivisions = 8,
            Hierarchy = false,
            Shortcuts = false,
            MaxDegreeOfParallelism = dop
        })
    {
        Pipeline = ManagedRoadGraphPipeline.PooledFrontier
    };

    var r = await new ManagedRoadGraphBuilder().BuildAsync(request);
    ok = r.TileBuilderResult.Success;
    peakIntermediate = r.PeakIntermediateMemoryBytes;
    tileCount = r.TileBuilderResult.TileCount;
    if (r.ResourceMetrics is { } rm)
    {
        selectedDop = rm.SelectedDop;
        hwmMem = rm.MemoryHighWaterMarkBytes;
        hwmScratch = rm.ScratchHighWaterMarkBytes;
        resource = new
        {
            rm.IngestionMemoryPeakBytes,
            rm.SemanticPhaseMemoryPeakBytes,
            rm.GraphAndTilePhaseMemoryPeakBytes,
            rm.RestrictionPhaseMemoryPeakBytes,
            rm.IngestionScratchPeakBytes,
            rm.SemanticPhaseScratchPeakBytes,
            rm.GraphAndTilePhaseScratchPeakBytes,
            rm.RestrictionPhaseScratchPeakBytes,
            rm.SelectedDop,
            rm.PerWorkerMemoryReservationBytes,
            rm.PerWorkerScratchReservationBytes,
            MemoryHighWaterMarkBytes = rm.MemoryHighWaterMarkBytes,
            ScratchHighWaterMarkBytes = rm.ScratchHighWaterMarkBytes
        };
    }

    using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var f in Directory.GetFiles(tiles, "*", SearchOption.AllDirectories)
                 .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
    {
        inc.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(tiles, f)));
        var len = new FileInfo(f).Length;
        inc.AppendData(BitConverter.GetBytes(len));
    }
    hash = Convert.ToHexString(inc.GetHashAndReset());
}
catch (Exception ex)
{
    ok = false;
    fail = ex.ToString();
}
finally
{
    cts.Cancel();
    try { await sampler; } catch { }
    proc.Refresh();
    peakWs = Math.Max(peakWs, proc.PeakWorkingSet64);
    peakPrivate = Math.Max(peakPrivate, proc.PrivateMemorySize64);
    peakGc = Math.Max(peakGc, GC.GetTotalMemory(false));
}

sw.Stop();
var obj = new
{
    success = ok,
    failure = fail,
    durationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3),
    peakIntermediateMemoryBytes = peakIntermediate,
    peakWorkingSetBytes = peakWs,
    peakPrivateBytes = peakPrivate,
    peakGcHeapBytes = peakGc,
    resourceMetricsHighWaterMarkBytes = hwmMem,
    scratchHighWaterMarkBytes = hwmScratch,
    selectedDop,
    outputTreeSha256 = hash,
    tileCount,
    resourceMetrics = resource
};
var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(outJson, json);
Console.WriteLine(json);
Environment.Exit(ok ? 0 : 2);