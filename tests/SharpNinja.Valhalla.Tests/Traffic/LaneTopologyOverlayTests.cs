using System.Net;
using System.Text;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class LaneTopologyOverlayTests
{
    [Fact]
    public async Task JsonFileSource_LoadsVersionedCuratedOverlayForExactGraphSignature()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"lane-overlay-{Guid.NewGuid():N}.json");
        try
        {
            CanonicalLaneTopologyOverlay overlay = CreateOverlay("graph-exact");
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(overlay),
                TestContext.Current.CancellationToken);
            var source = new JsonFileLaneTopologyOverlaySource(path);

            LaneTopologyOverlayLoadResult result = await source.LoadAsync(
                new LaneTopologyOverlayRequest("graph-exact", [11UL, 12UL]),
                TestContext.Current.CancellationToken);

            Assert.Equal(LaneTopologyOverlayLoadStatus.Loaded, result.Status);
            CanonicalLaneTopologyOverlay loaded = Assert.IsType<CanonicalLaneTopologyOverlay>(
                result.Overlay);
            Assert.Equal(1, loaded.Descriptor.SchemaVersion);
            Assert.Equal("fixture", loaded.Descriptor.DatasetId);
            Assert.Equal("1.0.0", loaded.Descriptor.DatasetVersion);
            Assert.Equal(LaneTopologyOverlayProvenance.Curated, loaded.Descriptor.Provenance);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HttpSource_UsesConfiguredExactUrlWithoutReconstruction()
    {
        var expected = new Uri(
            "https://proxy.example.test/canonical/lane-overlay.json?tenant=central");
        Uri? requested = null;
        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            requested = request.RequestUri;
            string json = JsonSerializer.Serialize(CreateOverlay("graph-exact"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        });
        using var transport = new HttpMessageInvoker(handler);
        var source = new HttpLaneTopologyOverlaySource(transport, expected);

        LaneTopologyOverlayLoadResult result = await source.LoadAsync(
            new LaneTopologyOverlayRequest("graph-exact", [11UL, 12UL]),
            TestContext.Current.CancellationToken);

        Assert.Equal(LaneTopologyOverlayLoadStatus.Loaded, result.Status);
        Assert.Equal(expected, requested);
    }

    [Fact]
    public async Task CompositeSource_UsesRegistrationOrderAndStopsOnInvalidDataset()
    {
        var notFound = new StubOverlaySource(
            LaneTopologyOverlayLoadResult.NotFound("first"));
        var invalid = new StubOverlaySource(
            LaneTopologyOverlayLoadResult.Invalid(
                "second",
                new LaneTopologyOverlayDiagnostic(
                    LaneTopologyOverlayDiagnosticCode.UnsupportedSchemaVersion,
                    "unsupported")));
        var loaded = new StubOverlaySource(
            LaneTopologyOverlayLoadResult.Loaded(CreateOverlay("graph-exact")));
        var source = new CompositeLaneTopologyOverlaySource([notFound, invalid, loaded]);

        LaneTopologyOverlayLoadResult result = await source.LoadAsync(
            new LaneTopologyOverlayRequest("graph-exact", [11UL, 12UL]),
            TestContext.Current.CancellationToken);

        Assert.Equal(LaneTopologyOverlayLoadStatus.Invalid, result.Status);
        Assert.Equal(1, notFound.CallCount);
        Assert.Equal(1, invalid.CallCount);
        Assert.Equal(0, loaded.CallCount);
    }

    [Fact]
    public async Task JsonSource_UnknownSchemaVersionReturnsTypedInvalidResult()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"lane-overlay-{Guid.NewGuid():N}.json");
        try
        {
            CanonicalLaneTopologyOverlay overlay = CreateOverlay("graph-exact") with
            {
                Descriptor = CreateOverlay("graph-exact").Descriptor with
                {
                    SchemaVersion = 99,
                },
            };
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(overlay),
                TestContext.Current.CancellationToken);
            var source = new JsonFileLaneTopologyOverlaySource(path);

            LaneTopologyOverlayLoadResult result = await source.LoadAsync(
                new LaneTopologyOverlayRequest("graph-exact", [11UL, 12UL]),
                TestContext.Current.CancellationToken);

            Assert.Equal(LaneTopologyOverlayLoadStatus.Invalid, result.Status);
            Assert.Contains(
                result.Diagnostics,
                static diagnostic =>
                    diagnostic.Code ==
                    LaneTopologyOverlayDiagnosticCode.UnsupportedSchemaVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validator_ExactGraphSignatureMismatchFailsClosed()
    {
        LaneTopologyOverlayValidationResult result =
            LaneTopologyOverlayValidator.Validate(
                CreateOverlay("fixture-signature"),
                "runtime-signature",
                CreateSegments());

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code ==
                LaneTopologyOverlayDiagnosticCode.GraphSignatureMismatch);
    }

    [Fact]
    public void Validator_MissingCanonicalEdgeFailsClosed()
    {
        CanonicalLaneTopologyOverlay overlay = CreateOverlay("graph-exact") with
        {
            Edges =
            [
                new CanonicalLaneEdgeOverlay(999UL, 100UL, 101UL, 2),
            ],
            Transitions = [],
            FrictionPoints = [],
        };

        LaneTopologyOverlayValidationResult result =
            LaneTopologyOverlayValidator.Validate(
                overlay,
                "graph-exact",
                CreateSegments());

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code ==
                LaneTopologyOverlayDiagnosticCode.CanonicalEdgeMissing);
    }

    [Fact]
    public void Validator_CanonicalNodeMismatchFailsClosed()
    {
        CanonicalLaneTopologyOverlay overlay = CreateOverlay("graph-exact") with
        {
            Edges =
            [
                new CanonicalLaneEdgeOverlay(11UL, 100UL, 999UL, 2),
                new CanonicalLaneEdgeOverlay(12UL, 101UL, 102UL, 2),
            ],
        };

        LaneTopologyOverlayValidationResult result =
            LaneTopologyOverlayValidator.Validate(
                overlay,
                "graph-exact",
                CreateSegments());

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code ==
                LaneTopologyOverlayDiagnosticCode.CanonicalNodeMismatch);
    }

    [Fact]
    public void Validator_OutOfRangeLaneFailsClosed()
    {
        CanonicalLaneTopologyOverlay overlay = CreateOverlay("graph-exact") with
        {
            Transitions =
            [
                new CanonicalLaneTransitionOverlay(
                    11UL,
                    12UL,
                    101UL,
                    [new LaneTransitionOption(3, 1)],
                    LaneTopologyChangeKind.Merge,
                    true,
                    "curated transition"),
            ],
        };

        LaneTopologyOverlayValidationResult result =
            LaneTopologyOverlayValidator.Validate(
                overlay,
                "graph-exact",
                CreateSegments());

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code ==
                LaneTopologyOverlayDiagnosticCode.LaneOutOfRange);
    }

    [Fact]
    public void Validator_ValidOverlayRetainsDatasetProvenance()
    {
        CanonicalLaneTopologyOverlay overlay = CreateOverlay("graph-exact");

        LaneTopologyOverlayValidationResult result =
            LaneTopologyOverlayValidator.Validate(
                overlay,
                "graph-exact",
                CreateSegments());

        Assert.True(result.IsValid);
        Assert.Same(overlay, result.Overlay);
        Assert.Equal(
            LaneTopologyOverlayProvenance.Curated,
            result.Overlay!.Descriptor.Provenance);
        Assert.Empty(result.Diagnostics);
    }


    [Fact]
    public void OverlayConstruction_DefensivelyCopiesEveryCollectionAndNestedOptions()
    {
        var options = new List<LaneTransitionOption>
        {
            new(1, 1),
            new(2, 2),
        };
        var transition = new CanonicalLaneTransitionOverlay(
            11UL,
            12UL,
            101UL,
            options,
            LaneTopologyChangeKind.Continuation,
            false,
            "immutable transition");
        var edges = new List<CanonicalLaneEdgeOverlay>
        {
            new(11UL, 100UL, 101UL, 2),
            new(12UL, 101UL, 102UL, 2),
        };
        var transitions = new List<CanonicalLaneTransitionOverlay> { transition };
        var points = new List<CanonicalLaneFrictionOverlay>
        {
            new(
                11UL,
                1,
                50d,
                LaneFrictionContributionKind.Weave,
                2,
                false,
                "immutable point"),
        };
        var overlay = new CanonicalLaneTopologyOverlay(
            new LaneTopologyOverlayDescriptor(
                1,
                "immutable",
                "1",
                "graph-exact",
                LaneTopologyOverlayProvenance.Test),
            edges,
            transitions,
            points);

        options.Clear();
        edges.Clear();
        transitions.Clear();
        points.Clear();

        Assert.Equal(2, overlay.Edges.Count);
        CanonicalLaneTransitionOverlay retained = Assert.Single(overlay.Transitions);
        Assert.Equal(2, retained.Options.Count);
        Assert.Single(overlay.FrictionPoints);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LaneTransitionOption>)retained.Options).Add(new(1, 2)));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CanonicalLaneEdgeOverlay>)overlay.Edges).Clear());
    }

    [Fact]
    public async Task HttpSource_ChunkedPayloadStopsAtLimitAndRedactsAllUrlSecrets()
    {
        var exactUrl = new Uri(
            "https://user:password@proxy.example.test/private/api-key/overlay.json?token=secret#fragment");
        byte[] bytes = Enumerable.Range(0, 65).Select(static value => (byte)value).ToArray();
        var handler = new DelegateHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableChunkedStream(bytes, 7)),
            }));
        using var transport = new HttpMessageInvoker(handler);
        var source = new HttpLaneTopologyOverlaySource(
            transport,
            exactUrl,
            maximumPayloadBytes: 64);

        LaneTopologyOverlayLoadResult result = await source.LoadAsync(
            new LaneTopologyOverlayRequest("graph-exact", [11UL]),
            TestContext.Current.CancellationToken);

        Assert.Equal(LaneTopologyOverlayLoadStatus.Invalid, result.Status);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == LaneTopologyOverlayDiagnosticCode.PayloadTooLarge);
        Assert.Equal("https://proxy.example.test/redacted", result.SourceId.TrimEnd('/'));
        Assert.DoesNotContain("user", result.SourceId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", result.SourceId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-key", result.SourceId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.SourceId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_HugeDistanceSeverityAndInvalidEnumsReturnTypedDiagnostics()
    {
        CanonicalLaneTopologyOverlay overlay = CreateOverlay("graph-exact") with
        {
            Descriptor = CreateOverlay("graph-exact").Descriptor with
            {
                Provenance = (LaneTopologyOverlayProvenance)999,
            },
            Transitions =
            [
                CreateOverlay("graph-exact").Transitions[0] with
                {
                    ChangeKind = (LaneTopologyChangeKind)999,
                },
            ],
            FrictionPoints =
            [
                new CanonicalLaneFrictionOverlay(
                    11UL,
                    1,
                    1e308,
                    (LaneFrictionContributionKind)999,
                    int.MaxValue,
                    true,
                    "hostile huge point"),
            ],
        };

        LaneTopologyOverlayValidationResult result = LaneTopologyOverlayValidator.Validate(
            overlay,
            "graph-exact",
            CreateSegments());

        Assert.False(result.IsValid);
        Assert.Null(result.Overlay);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == LaneTopologyOverlayDiagnosticCode.LaneOutOfRange);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == LaneTopologyOverlayDiagnosticCode.InvalidMetadata);
    }


    [Fact]
    public async Task JsonSource_NullDescriptorReturnsTypedMalformedResult()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"lane-overlay-null-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "descriptor": null,
                  "edges": [],
                  "transitions": [],
                  "frictionPoints": []
                }
                """,
                TestContext.Current.CancellationToken);
            var source = new JsonFileLaneTopologyOverlaySource(path);

            LaneTopologyOverlayLoadResult result = await source.LoadAsync(
                new LaneTopologyOverlayRequest("graph-exact", []),
                TestContext.Current.CancellationToken);

            Assert.Equal(LaneTopologyOverlayLoadStatus.Invalid, result.Status);
            Assert.Contains(
                result.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == LaneTopologyOverlayDiagnosticCode.MalformedPayload);
        }
        finally
        {
            File.Delete(path);
        }
    }


    [Theory]
    [InlineData("edges")]
    [InlineData("transitions")]
    [InlineData("frictionPoints")]
    [InlineData("options")]
    public async Task JsonSource_NullNestedElementReturnsTypedMalformedResult(
        string collection)
    {
        System.Text.Json.Nodes.JsonNode root =
            System.Text.Json.Nodes.JsonNode.Parse(
                JsonSerializer.Serialize(CreateOverlay("graph-exact")))!;
        if (string.Equals(collection, "options", StringComparison.Ordinal))
        {
            root["Transitions"]![0]!["Options"] =
                new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null);
        }
        else
        {
            string propertyName = collection switch
            {
                "edges" => "Edges",
                "transitions" => "Transitions",
                "frictionPoints" => "FrictionPoints",
                _ => throw new ArgumentOutOfRangeException(nameof(collection)),
            };
            root[propertyName] =
                new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null);
        }

        string path = Path.Combine(
            Path.GetTempPath(),
            $"lane-overlay-null-element-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                path,
                root.ToJsonString(),
                TestContext.Current.CancellationToken);
            var source = new JsonFileLaneTopologyOverlaySource(path);

            LaneTopologyOverlayLoadResult result = await source.LoadAsync(
                new LaneTopologyOverlayRequest("graph-exact", [11UL, 12UL]),
                TestContext.Current.CancellationToken);

            Assert.Equal(LaneTopologyOverlayLoadStatus.Invalid, result.Status);
            Assert.Contains(
                result.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == LaneTopologyOverlayDiagnosticCode.MalformedPayload);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CanonicalLaneTopologyOverlay CreateOverlay(string graphSignature)
        => new(
            new LaneTopologyOverlayDescriptor(
                SchemaVersion: 1,
                DatasetId: "fixture",
                DatasetVersion: "1.0.0",
                GraphSignature: graphSignature,
                Provenance: LaneTopologyOverlayProvenance.Curated,
                SourceReference: "driver-validated narrative"),
            Edges:
            [
                new CanonicalLaneEdgeOverlay(11UL, 100UL, 101UL, 2),
                new CanonicalLaneEdgeOverlay(12UL, 101UL, 102UL, 2),
            ],
            Transitions:
            [
                new CanonicalLaneTransitionOverlay(
                    11UL,
                    12UL,
                    101UL,
                    [new LaneTransitionOption(1, 1), new LaneTransitionOption(2, 2)],
                    LaneTopologyChangeKind.Continuation,
                    false,
                    "curated continuation"),
            ],
            FrictionPoints:
            [
                new CanonicalLaneFrictionOverlay(
                    11UL,
                    2,
                    75d,
                    LaneFrictionContributionKind.AdjacentMerge,
                    2,
                    true,
                    "curated right-side merge"),
            ]);

    private static IReadOnlyDictionary<ulong, LaneTopologySegment> CreateSegments()
    {
        var first = new LaneTopologySegment(
            "000000000000000B",
            2,
            100d,
            [LaneTurnIntent.Through, LaneTurnIntent.Through],
            [])
        {
            CanonicalDirectedEdgeId = 11UL,
            GraphEvidence = new LaneTopologyGraphEvidence(
                100UL,
                101UL,
                0,
                90d,
                90d,
                Use.Road,
                true,
                [],
                []),
        };
        var second = new LaneTopologySegment(
            "000000000000000C",
            2,
            100d,
            [LaneTurnIntent.Through, LaneTurnIntent.Through],
            [])
        {
            CanonicalDirectedEdgeId = 12UL,
            GraphEvidence = new LaneTopologyGraphEvidence(
                101UL,
                102UL,
                1,
                90d,
                90d,
                Use.Road,
                true,
                [],
                []),
        };
        return new Dictionary<ulong, LaneTopologySegment>
        {
            [11UL] = first,
            [12UL] = second,
        };
    }

    private sealed class StubOverlaySource : ILaneTopologyOverlaySource
    {
        private readonly LaneTopologyOverlayLoadResult _result;

        public StubOverlaySource(LaneTopologyOverlayLoadResult result)
            => _result = result;

        public int CallCount { get; private set; }

        public ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
            LaneTopologyOverlayRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(_result);
        }
    }


    private sealed class NonSeekableChunkedStream(byte[] bytes, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(
                Math.Min(count, chunkSize),
                bytes.Length - _position);
            if (available <= 0)
            {
                return 0;
            }

            Array.Copy(bytes, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int available = Math.Min(
                Math.Min(buffer.Length, chunkSize),
                bytes.Length - _position);
            if (available <= 0)
            {
                return ValueTask.FromResult(0);
            }

            bytes.AsMemory(_position, available).CopyTo(buffer);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(
            Func<
                HttpRequestMessage,
                CancellationToken,
                Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
