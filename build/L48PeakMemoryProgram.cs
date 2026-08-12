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

if (Directory.Exists(work)) Directory.Delete(work, true);
if (Directory.Exists(tiles)) Directory.Delete(tiles, true);
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

using var cts = new CancellationTokenSource();
var sampler = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            proc.Refresh();
            peakWs = Math.Max(peakWs, proc.PeakWorkingSet64);
            peakPrivate = Math.Max(peakPrivate, proc.PrivateMemorySize64);
            peakGc = Math.Max(peakGc, GC.GetTotalMemory(false));
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