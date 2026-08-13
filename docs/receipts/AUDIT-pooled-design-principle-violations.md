# Audit: pooled L48 path vs design principles

TimestampUtc: 2026-08-13T14:45:00Z
Scope: SharpNinja.Valhalla.Generation (Pbf, Roads/Frontier, Storage) on branch tip of pooled-node-frontier worktree
Method: static code inspection against stated principles (chunk-scoped decode + recycled value types; mid-edge release; extract-scaling heap forbidden)

## Principles under audit

P1. Decode is chunk-scoped: max live decode state is O(chunk x workers), buffers recycled; no extract-growing managed tables for decode.
P2. Live generation state is only recycled value-type pools (true Rent/Return or slabs); no fake pools that allocate-and-drop.
P3. Mid-edge / secondary nodes return to the pool when their durable contribution is written and no live handle remains.
P4. Hot-path tags/names are ids (or equivalent), not Dictionary string,string / new string per entity on multi-pass rehydrate.
P5. Intermediate durable data is disk/MMF sequences, not growing List/Dictionary of entities proportional to the extract.

---

## VIOLATIONS (ordered by severity for L48 GC peak)

### V1 — Extract-scoped string interning (P1, P4) — CRITICAL
File: `src/SharpNinja.Valhalla.Generation/Pbf/StoredOsmPbfEntitySource.cs`
- L24-25: `Dictionary<string,int> internedStringIds` + `List<string> internedStrings` fields live for the source lifetime.
- L304-314 `InternString`: every new distinct string is **added and retained** for the whole extract.
- Called from tag/role write paths (L419, L461-462) during ingest.

Why it violates: heap grows with **unique string cardinality of lower-48**, not with max PBF chunk. Survives across all chunks until source dispose.

### V2 — Tag rehydrate as managed Dictionary + strings (P1, P4) — CRITICAL
File: `src/SharpNinja.Valhalla.Generation/Roads/Frontier/CompactOsmSemanticStore.cs`
- L1122-1150 `ReadTags`: allocates `new Dictionary<string,string>`, `ReadString` → managed strings, returns heap dictionary.
- L1229: `ReadPayload` uses `GC.AllocateUninitializedArray<byte>` per read.

Call sites forcing rehydrate on later passes:
- `BoundedRoadTileWriter.cs` L332, L525
- `PooledRoadEdgeBuilder.cs` L435
- `ComplexRestrictionSequenceSet.cs` L338
- `SimpleRestrictionMaskIndex.cs` L180
- `ComplexRestrictionSemantics.cs` L68

Why it violates: even if compact blobs are on disk, **every tag consumer rebuilds heap objects**. Not chunk-bounded; volume scales with entities processed across the extract.

### V3 — NonRetainingArrayPool is not a pool (P2) — CRITICAL
File: `src/SharpNinja.Valhalla.Generation/Roads/Frontier/PooledNodeArena.cs`
- L118-121: default pools are `new NonRetainingArrayPool<T>()`.
- L384-396: `Rent` → `GC.AllocateUninitializedArray`; `Return` does **not** store the array for reuse.

Why it violates: principle requires recycled slabs/pools. This is **allocate-per-slab-grow, drop on Return**. Peak live slab arrays can still be multi-GiB when peak live slots are high; churn is pure GC.

### V4 — Way node-ref ToArray on non-span visitor path (P1) — HIGH
File: `src/SharpNinja.Valhalla.Generation/Pbf/StoredOsmPbfEntitySource.cs`
- L335-343: if visitor is not `IOsmPbfSpanVisitor`, `nodeReferences.ToArray()` allocates a new `ulong[]` per way.

File: `src/SharpNinja.Valhalla.Generation/Roads/Frontier/CompactOsmSemanticStore.cs`
- L887: `Way(..., IReadOnlyList)` falls through to `nodeRefs.ToArray()` when not array/List.

Why it violates: per-entity heap arrays during country-scale way volume; not recycled value-type buffers.

### V5 — Shape materialization allocates managed arrays (P2, P5) — HIGH
File: `src/SharpNinja.Valhalla.Generation/Roads/Frontier/DurableFrontierEdgeSink.cs`
- L257-261: `ReadShape` does `GC.AllocateUninitializedArray<byte>` + `GC.AllocateUninitializedArray<GenerationNodeRecord>` and returns managed arrays of full shapes.

Why it violates: shapes that were durable on disk are rehydrated as large managed arrays rather than pooled/unmanaged spans.

### V6 — Restriction / tile stages allocate large unmanaged-of-managed buffers (P2) — MEDIUM-HIGH
Files:
- `PooledRoadRestrictionStage.cs` L576-579, L1132, L1162: multiple `GC.AllocateUninitializedArray`
- `BoundedTilesetRestamper.cs` L41, L46: header/tile id arrays via GC allocate

Why it violates: late-stage still uses one-shot GC arrays, not recycled pools.

### V7 — Catalog structures use Dictionary/List (P2, P5) — MEDIUM
File: `BoundedRestrictionTileCatalog.cs` L76, L96, L105: `Dictionary<byte, List<GraphId>>` growth for tile catalogs.

Why it violates: managed collections scaling with tile topology rather than fixed pools (may be smaller than V1/V2 but still principle-breaking).

### V8 — IntermediateBlobStore.Read always heap-allocates payload (P1, P2) — MEDIUM
File: `IntermediateBlobStore.cs` L196 area / L662: `Read` returns `byte[]` via `GC.AllocateUninitializedArray`.

Why it violates: hot reads cannot stay in recycled buffers if every Read returns a new array (callers that use Span overloads are better; Dictionary path uses the allocating Read).

---

## COMPLIANT or PARTIALLY COMPLIANT patterns (for balance)

### C1 — Secondary node release (P3) — COMPLIANT in path session
File: `PooledPathWaySession.cs`
- L172-188 `AppendSecondary`: Rent → write shape → mark durable → **Release** immediately.
- L233-234: source anchor Released after edge PersistEdge.
- L102-105: final anchor Released at Complete.

This matches "release mid-edge nodes once durable contribution is written."

### C2 — Real ArrayPool use in some PBF helpers — PARTIAL
File: `Pbf/PooledBuffer.cs` L18, L78-98: rents from `ArrayPool<T>.Shared` and Returns on grow/dispose.
File: `StreamingOsmPbfReader.cs`: ArrayPool for block buffers with Return.

These are closer to chunk-scoped reuse; they do not cancel V1-V5.

### C3 — Intermediate sequences of unmanaged T — PARTIAL
File: `IntermediateSequenceStore.cs`: for MemoryMapped mode, appends go to segments (not growing managed entity graphs of nodes). Memory mode still keeps managed `BoundedMemoryBuffer`.

Pooled path requests MemoryMapped for L48 probe — good for P5 durability; still combined with V1/V2 heap on the side.

### C4 — NodeWorkItem as value type in slabs — PARTIAL
File: `PooledNodeArena.cs` + `NodeWorkItem`: value-type slots + generation handles are the intended model.
Broken by V3 (slab arrays not truly pooled) and by heap outside the arena.

---

## Principle scorecard

| Principle | Status | Dominant violation |
| --- | --- | --- |
| P1 chunk-scoped decode | FAIL | V1 extract string intern; V4 ToArray; V2 multi-pass tag rehydrate |
| P2 recycled value-type pools only | FAIL | V3 NonRetainingArrayPool; V5/V6 GC.Allocate* |
| P3 mid-edge release when satisfied | PASS (path session) | C1 — arena path is largely correct |
| P4 id-only tags on hot path | FAIL | V1 + V2 string dictionaries |
| P5 intermediates not entity heap graphs | PARTIAL | MMF sequences help; catalogs/rehydrate reintroduce heap |

---

## Implication for ~9 GiB GC peak (corrected narrative)

Chunk-bounded decode **cannot** be the sole story for multi-GiB GC if buffers were recycled and nothing was retained. The audit shows **clear extract-scoped and rehydrate retention**:

1. **V1** grows managed string tables for the whole country.
2. **V2** allocates Dictionary+strings per tag read across later passes.
3. **V3/V5/V6** allocate large managed arrays without true recycle.

P3 (node release) is **not** the main contradiction: secondaries are released. The design failure is **everything around the frontier still uses managed, extract-scaling or high-churn heap**.

---

## Recommended fix order (audit only; not implemented here)

1. External/durable string table + integer ids; ban `Dictionary<string,string>` on generation hot path.
2. Replace NonRetainingArrayPool with true slab ownership or Shared ArrayPool with Return on dispose.
3. Span-only way node refs; delete ToArray fallbacks on production visitors.
4. Shape/tag read APIs that fill rented buffers or return spans into MMF, not new[] every time.
5. Then re-measure GC peak on L48.

---

## Evidence method
- Static inspection of worktree generation sources under `src/SharpNinja.Valhalla.Generation/{Pbf,Roads,Storage}`.
- Cross-check against L48 peak run 20260812T231118Z (GC peak ~9.2 GiB, RSS ~14.3 GiB, fail in restrictions).
