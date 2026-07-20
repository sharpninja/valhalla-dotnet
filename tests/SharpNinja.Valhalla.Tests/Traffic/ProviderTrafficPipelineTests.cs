using System.Net;
using System.Net.Http;
using System.Text;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers;
using SharpNinja.Valhalla.Traffic.Providers.Here;
using SharpNinja.Valhalla.Traffic.Providers.TomTom;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class ProviderTrafficPipelineTests
{
    private static readonly DateTimeOffset EvaluationTime =
        new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly ulong TileBaseId = new GraphId(1234, 2, 0).Value;
    private static readonly ulong ForwardEdgeId = new GraphId(1234, 2, 10).Value;
    private static readonly ulong ReverseEdgeId = new GraphId(1234, 2, 11).Value;

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task NormalizedProxyExtension_RequiresExplicitHostOptInIndependentOfSourceKind(
        string providerId)
    {
        string json = ClosureJson(providerId, "alongCoordinates");
        NormalizedTrafficSnapshot directDefault = await CreateSnapshotAsync(
            providerId,
            TrafficFeedKind.Closure,
            json,
            TrafficPolicy.Disabled);
        NormalizedTrafficSnapshot proxyWithoutOptIn = await CreateSnapshotAsync(
            providerId,
            TrafficFeedKind.Closure,
            json,
            TrafficPolicy.Disabled,
            sourceKind: TrafficSourceKind.Proxy,
            allowNormalizedProxyExtensions: false);
        NormalizedTrafficSnapshot proxyWithOptIn = await CreateSnapshotAsync(
            providerId,
            TrafficFeedKind.Closure,
            json,
            TrafficPolicy.Disabled,
            sourceKind: TrafficSourceKind.Proxy,
            allowNormalizedProxyExtensions: true);

        Assert.Equal(
            TrafficGeometryDirection.Unknown,
            Assert.Single(directDefault.Events).Geometry.Direction);
        Assert.Equal(
            TrafficGeometryDirection.Unknown,
            Assert.Single(proxyWithoutOptIn.Events).Geometry.Direction);
        Assert.False(Evaluate(directDefault, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(proxyWithoutOptIn, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.Equal(
            TrafficGeometryDirection.AlongCoordinates,
            Assert.Single(proxyWithOptIn.Events).Geometry.Direction);
        Assert.True(Evaluate(proxyWithOptIn, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(proxyWithOptIn, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task NormalizedProxyDirectionExtension_OneWayClosureHardDeniesOnlyMatchingCarriageway(
        string providerId)
    {
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Closure,
            ClosureJson(providerId, "alongCoordinates"),
            TrafficPolicy.Disabled);

        RouteTrafficEvaluation forward = Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled);
        RouteTrafficEvaluation reverse = Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled);

        Assert.True(forward.HasClosureHardDeny);
        Assert.True(forward.HasHardDeny);
        Assert.False(reverse.HasHardDeny);
        ValhallaTrafficEdgeUpdate update = Assert.Single(snapshot.ValhallaEdgeUpdates);
        Assert.Equal(ForwardEdgeId, update.CanonicalDirectedEdgeId);
        Assert.True(update.DirectionResolved);
        Assert.True(update.Closed);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task NormalizedProxyDirectionExtension_BothWayClosureHardDeniesBothCarriageways(
        string providerId)
    {
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Closure,
            ClosureJson(providerId, "bothDirections"),
            TrafficPolicy.Disabled);

        Assert.True(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled).HasClosureHardDeny);
        Assert.True(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasClosureHardDeny);
        Assert.Equal(2, snapshot.ValhallaEdgeUpdates.Count);
        Assert.All(snapshot.ValhallaEdgeUpdates, static update =>
        {
            Assert.True(update.DirectionResolved);
            Assert.True(update.Closed);
        });
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task UnknownDirectionClosure_RemainsAdvisoryForBothCarriageways(
        string providerId)
    {
        NormalizedTrafficSnapshot snapshot = await CreateSnapshotAsync(
            providerId,
            TrafficFeedKind.Closure,
            ClosureJson(providerId, direction: null),
            TrafficPolicy.Disabled);

        Assert.False(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.All(snapshot.ValhallaEdgeUpdates, static update =>
        {
            Assert.False(update.DirectionResolved);
            Assert.False(update.Closed);
        });
    }

    [Fact]
    public async Task TomTomNativeTmcDirection_RemainsUnknownAndCannotHardDeny()
    {
        string json = TomTomIncidentJson(
            "closure-native-tmc",
            "closure",
            direction: null,
            applicability: null,
            delay: 0)
            .Replace(
                "\"description\": \"closure\"",
                "\"tmc\": { \"direction\": \"positive\" }, \"description\": \"closure\"",
                StringComparison.Ordinal);
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            "tomtom",
            TrafficFeedKind.Closure,
            json,
            TrafficPolicy.Disabled);

        Assert.Equal(
            TrafficGeometryDirection.Unknown,
            Assert.Single(snapshot.Events).Geometry.Direction);
        Assert.False(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.All(snapshot.ValhallaEdgeUpdates, static update =>
        {
            Assert.False(update.DirectionResolved);
            Assert.False(update.Closed);
        });
    }

    [Fact]
    public async Task HereNativeLocationShapeWithoutProvenDirectionDecoder_RemainsUnknownAndCannotHardDeny()
    {
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            "here",
            TrafficFeedKind.Closure,
            HereIncidentJson(
                "closure-native-location",
                "roadClosure",
                direction: null,
                applicability: null,
                delay: 0),
            TrafficPolicy.Disabled);

        Assert.Equal(
            TrafficGeometryDirection.Unknown,
            Assert.Single(snapshot.Events).Geometry.Direction);
        Assert.False(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task UnnamespacedGeometryDirectionField_IsIgnoredAndCannotHardDeny(
        string providerId)
    {
        string json = ClosureJson(providerId, direction: null);
        json = providerId == "tomtom"
            ? json.Replace(
                "\"description\": \"closure\"",
                "\"geometryDirection\": \"alongCoordinates\", \"description\": \"closure\"",
                StringComparison.Ordinal)
            : json.Replace(
                "\"length\": 111",
                "\"geometryDirection\": \"alongCoordinates\", \"length\": 111",
                StringComparison.Ordinal);
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Closure,
            json,
            TrafficPolicy.Disabled);

        Assert.Equal(
            TrafficGeometryDirection.Unknown,
            Assert.Single(snapshot.Events).Geometry.Direction);
        Assert.False(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task UnnamespacedRestrictionApplicabilityField_IsIgnoredAndCannotHardDeny(
        string providerId)
    {
        string json = RestrictionJson(
            providerId,
            direction: "alongCoordinates",
            applicability: null);
        json = json.Replace(
            "\"description\":",
            "\"restrictionApplicability\": \"allVehicles\", \"description\":",
            StringComparison.Ordinal);
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Restriction,
            json,
            TrafficPolicy.Disabled);

        NormalizedTrafficEvent trafficEvent = Assert.Single(snapshot.Events);
        Assert.Equal(
            TrafficRestrictionApplicability.Unknown,
            trafficEvent.RestrictionApplicability);
        Assert.False(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task NormalizedProxyApplicabilityExtension_AllVehicleRestrictionHardDeniesExactEdgeWhenTrafficDisabled(
        string providerId)
    {
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Restriction,
            RestrictionJson(providerId, "alongCoordinates", "allVehicles"),
            TrafficPolicy.Disabled);

        RouteTrafficEvaluation evaluation =
            Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled);

        Assert.True(evaluation.HasRestrictionHardDeny);
        Assert.True(evaluation.HasHardDeny);
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task NormalizedProxyApplicabilityExtension_AllVehicleRestrictionHardDeniesExactEdgeWhenTrafficEnabled(
        string providerId)
    {
        string json = RestrictionJson(
            providerId,
            "alongCoordinates",
            "allVehicles");
        NormalizedTrafficSnapshot trusted = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Restriction,
            json,
            TrafficPolicy.Enabled);
        NormalizedTrafficSnapshot untrusted = await CreateSnapshotAsync(
            providerId,
            TrafficFeedKind.Restriction,
            json,
            TrafficPolicy.Enabled,
            sourceKind: TrafficSourceKind.Proxy,
            allowNormalizedProxyExtensions: false);

        Assert.True(
            Evaluate(trusted, ForwardEdgeId, TrafficPolicy.Enabled)
                .HasRestrictionHardDeny);
        Assert.False(
            Evaluate(trusted, ReverseEdgeId, TrafficPolicy.Enabled)
                .HasHardDeny);
        Assert.False(
            Evaluate(untrusted, ForwardEdgeId, TrafficPolicy.Enabled)
                .HasHardDeny);
        Assert.False(
            Evaluate(untrusted, ReverseEdgeId, TrafficPolicy.Enabled)
                .HasHardDeny);
    }

    [Theory]
    [InlineData("tomtom", "conditional")]
    [InlineData("tomtom", "truck")]
    [InlineData("tomtom", null)]
    [InlineData("here", "conditional")]
    [InlineData("here", "truck")]
    [InlineData("here", null)]
    public async Task NonUniversalRestriction_RemainsAdvisoryWithoutVehicleContext(
        string providerId,
        string? applicability)
    {
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Restriction,
            RestrictionJson(providerId, "alongCoordinates", applicability),
            TrafficPolicy.Disabled);

        Assert.False(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Disabled).HasHardDeny);
        Assert.False(Assert.Single(snapshot.RouteModifierImpacts).HardDeny);
    }

    [Theory]
    [InlineData("tomtom", TrafficFeedKind.Closure)]
    [InlineData("tomtom", TrafficFeedKind.Restriction)]
    [InlineData("here", TrafficFeedKind.Closure)]
    [InlineData("here", TrafficFeedKind.Restriction)]
    public async Task EnabledPolicy_UnknownDirectionConstraintDelay_RemainsAdvisoryForBothCarriageways(
        string providerId,
        TrafficFeedKind feedKind)
    {
        const int DelaySeconds = 45;
        string json = providerId == "tomtom"
            ? TomTomIncidentJson(
                "unknown-direction-constraint",
                feedKind == TrafficFeedKind.Closure ? "closure" : "restriction",
                direction: null,
                applicability: null,
                delay: DelaySeconds)
            : HereIncidentJson(
                "unknown-direction-constraint",
                feedKind == TrafficFeedKind.Closure ? "roadClosure" : "restriction",
                direction: null,
                applicability: null,
                delay: DelaySeconds);
        NormalizedTrafficSnapshot snapshot = await CreateSnapshotAsync(
            providerId,
            feedKind,
            json,
            TrafficPolicy.Enabled);

        Assert.Equal(DelaySeconds, Assert.Single(snapshot.RouteModifierSources).DelaySeconds);
        foreach (ulong edgeId in new[] { ForwardEdgeId, ReverseEdgeId })
        {
            RouteCandidateMetrics candidate = Candidate(edgeId);
            RouteTrafficEvaluation evaluation =
                RouteTrafficEvaluator.Evaluate(candidate, snapshot, TrafficPolicy.Enabled);

            Assert.False(evaluation.HasHardDeny);
            Assert.Single(evaluation.Sources);
            Assert.Equal(0, evaluation.ObservedTrafficDelaySeconds);
            Assert.Equal(0, evaluation.TrafficDelaySeconds);
            Assert.Equal(candidate.DurationSeconds, evaluation.AdjustedEtaSeconds(candidate));
            RouteFrictionScore friction =
                FrictionModel.Score(evaluation.ApplyTo(candidate), TrafficPolicy.Enabled);
            Assert.Equal(0, friction.TrafficDelaySeconds);
            Assert.Equal(0, friction.IncidentPenaltySeconds);
        }
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task UnknownDirectionDynamicTraffic_DoesNotAffectEitherCarriageway(
        string providerId)
    {
        NormalizedTrafficSnapshot snapshot = await CreateSnapshotAsync(
            providerId,
            TrafficFeedKind.Composite,
            CompositeFlowIncidentJson(providerId, direction: null),
            TrafficPolicy.Enabled);

        RouteTrafficEvaluation forward = Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Enabled);
        RouteTrafficEvaluation reverse = Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Enabled);

        Assert.Equal(0, forward.TrafficDelaySeconds);
        Assert.Equal(0, forward.IncidentCount);
        Assert.Equal(0, reverse.TrafficDelaySeconds);
        Assert.Equal(0, reverse.IncidentCount);
        Assert.Empty(forward.Sources);
        Assert.Empty(reverse.Sources);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task EnabledPolicy_NativeUnknownDynamicTraffic_IsAdvisoryAndNeverWrittenToTiles(
        string providerId)
    {
        var writer = new RecordingTileWriter();
        NormalizedTrafficSnapshot snapshot = await CreateSnapshotAsync(
            providerId,
            TrafficFeedKind.Composite,
            CompositeFlowIncidentJson(providerId, direction: null),
            TrafficPolicy.Enabled,
            sourceKind: TrafficSourceKind.DirectProvider,
            allowNormalizedProxyExtensions: false,
            tileWriter: writer);

        Assert.NotEmpty(snapshot.ValhallaEdgeUpdates);
        Assert.All(
            snapshot.ValhallaEdgeUpdates,
            static update => Assert.False(update.DirectionResolved));
        IReadOnlyList<ValhallaTrafficEdgeUpdate> written = Assert.Single(writer.Calls);
        Assert.Empty(written);
        Assert.Equal(0, snapshot.ValhallaWriteResult!.UpdateCount);
    }

    [Theory]
    [InlineData("tomtom", TrafficFeedKind.Flow)]
    [InlineData("tomtom", TrafficFeedKind.Incident)]
    [InlineData("tomtom", TrafficFeedKind.Closure)]
    [InlineData("tomtom", TrafficFeedKind.Restriction)]
    [InlineData("here", TrafficFeedKind.Flow)]
    [InlineData("here", TrafficFeedKind.Incident)]
    [InlineData("here", TrafficFeedKind.Closure)]
    [InlineData("here", TrafficFeedKind.Restriction)]
    public async Task NativeUnknownDirection_AllTrafficKindsAreNeverWrittenToTiles(
        string providerId,
        TrafficFeedKind feedKind)
    {
        string json = (providerId, feedKind) switch
        {
            ("tomtom", TrafficFeedKind.Flow) =>
                TomTomFlowDocumentJson(direction: null),
            ("here", TrafficFeedKind.Flow) =>
                HereFlowJson(speed: 10, freeFlow: 20, jamFactor: 2, direction: null),
            ("tomtom", TrafficFeedKind.Incident) =>
                TomTomIncidentJson(
                    "incident-unknown",
                    "incident",
                    direction: null,
                    applicability: null,
                    delay: 30),
            ("here", TrafficFeedKind.Incident) =>
                HereIncidentJson(
                    "incident-unknown",
                    "incident",
                    direction: null,
                    applicability: null,
                    delay: 30),
            (_, TrafficFeedKind.Closure) =>
                ClosureJson(providerId, direction: null),
            (_, TrafficFeedKind.Restriction) =>
                RestrictionJson(providerId, direction: null, applicability: "allVehicles"),
            _ => throw new ArgumentOutOfRangeException(nameof(feedKind)),
        };
        var writer = new RecordingTileWriter();

        NormalizedTrafficSnapshot snapshot = await CreateSnapshotAsync(
            providerId,
            feedKind,
            json,
            TrafficPolicy.Enabled,
            sourceKind: TrafficSourceKind.DirectProvider,
            allowNormalizedProxyExtensions: false,
            tileWriter: writer);

        Assert.NotEmpty(snapshot.Events);
        Assert.NotEmpty(snapshot.ValhallaEdgeUpdates);
        Assert.All(
            snapshot.ValhallaEdgeUpdates,
            static update => Assert.False(update.DirectionResolved));
        Assert.Empty(Assert.Single(writer.Calls));
        Assert.Equal(0, snapshot.ValhallaWriteResult!.UpdateCount);
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task FlowAndIncidentOnSameExactEdge_BothSurviveAndAggregateExactlyOnce(
        string providerId)
    {
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Composite,
            CompositeFlowIncidentJson(providerId, "alongCoordinates"),
            TrafficPolicy.Enabled);

        RouteTrafficEvaluation evaluation =
            Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Enabled);

        Assert.Equal(2, snapshot.Events.Count);
        Assert.Equal(2, snapshot.RouteModifierSources.Count);
        Assert.Equal(50, evaluation.ObservedTrafficDelaySeconds);
        Assert.Equal(50, evaluation.TrafficDelaySeconds);
        Assert.Equal(1, evaluation.ObservedIncidentCount);
        Assert.Equal(1, evaluation.IncidentCount);
        Assert.Equal(150, evaluation.AdjustedEtaSeconds(Candidate(ForwardEdgeId)));
        Assert.False(Evaluate(snapshot, ReverseEdgeId, TrafficPolicy.Enabled).Sources.Any());
    }

    [Theory]
    [InlineData("tomtom")]
    [InlineData("here")]
    public async Task DirectionSafeClosure_StillBeatsFlowOnSameExactEdge(string providerId)
    {
        NormalizedTrafficSnapshot snapshot = await CreateTrustedProxySnapshotAsync(
            providerId,
            TrafficFeedKind.Composite,
            CompositeFlowClosureJson(providerId, "alongCoordinates"),
            TrafficPolicy.Enabled);

        Assert.Single(snapshot.Events);
        Assert.Equal(NormalizedTrafficEventKind.Closure, snapshot.Events[0].Kind);
        Assert.True(Evaluate(snapshot, ForwardEdgeId, TrafficPolicy.Enabled).HasClosureHardDeny);
    }

    [Fact]
    public async Task HereFlowSpeeds_AreConvertedFromMetersPerSecondToKilometersPerHour()
    {
        NormalizedTrafficEvent trafficEvent = await NormalizeSingleAsync(
            new HereTrafficFeedAdapter(),
            "here",
            TrafficFeedKind.Flow,
            HereFlowJson(speed: 10, freeFlow: 20, jamFactor: 2, "alongCoordinates"));

        Assert.Equal(36d, trafficEvent.CurrentSpeedKph);
        Assert.Equal(72d, trafficEvent.FreeFlowSpeedKph);
    }

    [Fact]
    public async Task HereJamFactorTen_NormalizesAsClosure()
    {
        NormalizedTrafficEvent trafficEvent = await NormalizeSingleAsync(
            new HereTrafficFeedAdapter(),
            "here",
            TrafficFeedKind.Flow,
            HereFlowJson(speed: 0, freeFlow: 20, jamFactor: 10, "alongCoordinates"));

        Assert.Equal(NormalizedTrafficEventKind.Closure, trafficEvent.Kind);
        Assert.True(trafficEvent.RoadClosure);
    }

    [Fact]
    public async Task ConfiguredClientToFactory_TomTomMphUnitUsesSafeMetadataWithoutQueryProvenance()
    {
        const string ApiKey = "must-not-survive";
        var handler = new JsonHttpMessageHandler(TomTomFlowDocumentJson("alongCoordinates"));
        using var httpClient = new HttpClient(handler);
        var endpoint = new TrafficFeedEndpoint(
            "tomtom",
            TrafficFeedKind.Flow,
            new Uri(
                $"https://traffic.example.test/flow?unit=mph&key={ApiKey}",
                UriKind.Absolute),
            TrafficFeedCredentialMode.None);
        var source = new TrafficDataSourceRegistration(
            new ConfiguredTrafficFeedClient(
                "tomtom",
                httpClient,
                [endpoint],
                timeProvider: new StaticTimeProvider(EvaluationTime)),
            TrafficSourceKind.DirectProvider,
            [TrafficFeedKind.Flow]);
        var factory = new TrafficDataFactory(
            [source],
            new TrafficFeedAdapterRegistry([new TomTomTrafficFeedAdapter()]),
            new TrafficConflictResolver(["tomtom"]),
            new TrafficDataFactoryOptions
            {
                TrafficPolicy = TrafficPolicy.Enabled,
                TimeProvider = new StaticTimeProvider(EvaluationTime),
            });

        NormalizedTrafficSnapshot snapshot = await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);

        Assert.Empty(snapshot.Diagnostics);
        NormalizedTrafficEvent trafficEvent = Assert.Single(snapshot.Events);
        Assert.Equal(16.09344d, trafficEvent.CurrentSpeedKph!.Value, 5);
        Assert.Equal(32.18688d, trafficEvent.FreeFlowSpeedKph!.Value, 5);
        Assert.Equal(
            new Uri("https://traffic.example.test/redacted-path"),
            trafficEvent.SourceUri);
        string safeProvenance = snapshot.ToString();
        Assert.DoesNotContain(ApiKey, safeProvenance, StringComparison.Ordinal);
        Assert.DoesNotContain("unit=mph", safeProvenance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key=", safeProvenance, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, 10d, 20d)]
    [InlineData("kmph", 10d, 20d)]
    [InlineData("mph", 16.09344d, 32.18688d)]
    public async Task TomTomFlowSpeedUnit_NormalizesToKilometersPerHour(
        string? unit,
        double expectedCurrent,
        double expectedFreeFlow)
    {
        var providerMetadata = new Dictionary<string, string>();
        if (unit is not null)
        {
            providerMetadata["speedUnit"] = unit;
        }

        NormalizedTrafficEvent trafficEvent = await NormalizeSingleAsync(
            new TomTomTrafficFeedAdapter(),
            "tomtom",
            TrafficFeedKind.Flow,
            TomTomFlowDocumentJson("alongCoordinates"),
            new Uri("https://traffic.example.test/credential-path?apiKey=must-not-survive"),
            providerMetadata);

        Assert.Equal(expectedCurrent, trafficEvent.CurrentSpeedKph!.Value, 5);
        Assert.Equal(expectedFreeFlow, trafficEvent.FreeFlowSpeedKph!.Value, 5);
    }

    private static async Task<NormalizedTrafficSnapshot> CreateSnapshotAsync(
        string providerId,
        TrafficFeedKind feedKind,
        string json,
        TrafficPolicy factoryPolicy,
        TrafficSourceKind sourceKind = TrafficSourceKind.DirectProvider,
        bool allowNormalizedProxyExtensions = false,
        IValhallaTrafficTileWriter? tileWriter = null)
    {
        ITrafficFeedAdapter adapter = providerId switch
        {
            "tomtom" => new TomTomTrafficFeedAdapter(),
            "here" => new HereTrafficFeedAdapter(),
            _ => throw new ArgumentOutOfRangeException(nameof(providerId)),
        };
        var payload = new RawTrafficFeedPayload(
            providerId,
            feedKind,
            "application/json",
            Encoding.UTF8.GetBytes(json),
            EvaluationTime.AddMinutes(-1),
            new Uri($"https://traffic.example.test/{providerId}/{feedKind}"),
            new Dictionary<string, string>());
        var source = new TrafficDataSourceRegistration(
            new PayloadClient(providerId, payload),
            sourceKind,
            [feedKind],
            allowNormalizedProxyExtensions);
        var graphSource = new StubGraphSource([
            new TrafficSpatialGraphEdge(
                TileBaseId,
                10,
                ForwardEdgeId,
                TrafficDirection.Forward,
                [new GeoCoordinate(36.0000, -86.7000), new GeoCoordinate(36.0010, -86.7000)]),
            new TrafficSpatialGraphEdge(
                TileBaseId,
                11,
                ReverseEdgeId,
                TrafficDirection.Reverse,
                [new GeoCoordinate(36.0010, -86.7000), new GeoCoordinate(36.0000, -86.7000)]),
        ]);
        using var spatialIndex = new GraphTileTrafficSpatialIndex(
            graphSource,
            matchToleranceMeters: 8);
        var factory = new TrafficDataFactory(
            [source],
            new TrafficFeedAdapterRegistry([adapter]),
            new TrafficConflictResolver([providerId]),
            new TrafficDataFactoryOptions
            {
                TrafficPolicy = factoryPolicy,
                TimeProvider = new StaticTimeProvider(EvaluationTime),
                EdgeMatcher = new ValhallaTrafficEdgeMatcher(spatialIndex),
                GraphContext = new ValhallaGraphTrafficContext("provider-pipeline-test"),
                WriteTrafficTiles = tileWriter is not null,
                TileWriter = tileWriter,
                TileWriteOptions = tileWriter is null
                    ? null
                    : new ValhallaTrafficWriteOptions("test-traffic-tiles"),
            });

        return await factory.CreateSnapshotAsync(
            new TrafficDataRequest(),
            TestContext.Current.CancellationToken);
    }

    private static Task<NormalizedTrafficSnapshot> CreateTrustedProxySnapshotAsync(
        string providerId,
        TrafficFeedKind feedKind,
        string json,
        TrafficPolicy factoryPolicy)
        => CreateSnapshotAsync(
            providerId,
            feedKind,
            json,
            factoryPolicy,
            sourceKind: TrafficSourceKind.Proxy,
            allowNormalizedProxyExtensions: true);

    private static RouteTrafficEvaluation Evaluate(
        NormalizedTrafficSnapshot snapshot,
        ulong edgeId,
        TrafficPolicy policy)
        => RouteTrafficEvaluator.Evaluate(Candidate(edgeId), snapshot, policy);

    private static RouteCandidateMetrics Candidate(ulong edgeId)
        => new(
            ProviderId: "valhalla",
            Index: 0,
            DistanceMeters: 1_000,
            DurationSeconds: 100,
            DirectedEdgeIds: [edgeId]);

    private static async Task<NormalizedTrafficEvent> NormalizeSingleAsync(
        ITrafficFeedAdapter adapter,
        string providerId,
        TrafficFeedKind feedKind,
        string json,
        Uri? sourceUri = null,
        IReadOnlyDictionary<string, string>? providerMetadata = null)
    {
        var payload = new RawTrafficFeedPayload(
            providerId,
            feedKind,
            "application/json",
            Encoding.UTF8.GetBytes(json),
            EvaluationTime.AddMinutes(-1),
            sourceUri,
            providerMetadata ?? new Dictionary<string, string>());
        TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
            payload,
            new TrafficNormalizationContext(EvaluationTime),
            TestContext.Current.CancellationToken);
        Assert.Empty(result.Diagnostics);
        return Assert.Single(result.Events);
    }

    private static string ClosureJson(string providerId, string? direction)
        => providerId == "tomtom"
            ? TomTomIncidentJson("closure-1", "closure", direction, applicability: null, delay: 0)
            : HereIncidentJson("closure-1", "roadClosure", direction, applicability: null, delay: 0);

    private static string RestrictionJson(
        string providerId,
        string? direction,
        string? applicability)
        => providerId == "tomtom"
            ? TomTomIncidentJson("restriction-1", "restriction", direction, applicability, delay: 0)
            : HereIncidentJson("restriction-1", "restriction", direction, applicability, delay: 0);

    private static string CompositeFlowIncidentJson(string providerId, string? direction)
        => providerId == "tomtom"
            ? $$"""
              {
                "flowSegmentData": {{TomTomFlowJson(direction)}},
                "incidents": [
                  {{TomTomIncidentFeature("incident-1", "incident", direction, null, 30)}}
                ]
              }
              """
            : $$"""
              {
                "results": [
                  {{HereFlowRecord("flow-1", 10, 20, 2, direction)}},
                  {{HereIncidentRecord("incident-1", "incident", direction, null, 30)}}
                ]
              }
              """;

    private static string CompositeFlowClosureJson(string providerId, string direction)
        => providerId == "tomtom"
            ? $$"""
              {
                "flowSegmentData": {{TomTomFlowJson(direction)}},
                "incidents": [
                  {{TomTomIncidentFeature("closure-1", "closure", direction, null, 0)}}
                ]
              }
              """
            : $$"""
              {
                "results": [
                  {{HereFlowRecord("flow-1", 10, 20, 2, direction)}},
                  {{HereIncidentRecord("closure-1", "roadClosure", direction, null, 0)}}
                ]
              }
              """;

    private static string TomTomFlowDocumentJson(string? direction)
        => $$"""
           {
             "flowSegmentData": {{TomTomFlowJson(direction)}}
           }
           """;

    private static string TomTomFlowJson(string? direction)
        => $$"""
           {
             "id": "flow-1",
             "currentSpeed": 10,
             "freeFlowSpeed": 20,
             "currentTravelTime": 100,
             "freeFlowTravelTime": 80,
             "confidence": 0.9,
             {{ExtensionProperty(direction, null)}}
             "coordinates": {
               "coordinate": [
                 { "latitude": 36.0000, "longitude": -86.7000 },
                 { "latitude": 36.0010, "longitude": -86.7000 }
               ]
             }
           }
           """;

    private static string TomTomIncidentJson(
        string id,
        string eventKind,
        string? direction,
        string? applicability,
        int delay)
        => $$"""
           {
             "incidents": [
               {{TomTomIncidentFeature(id, eventKind, direction, applicability, delay)}}
             ]
           }
           """;

    private static string TomTomIncidentFeature(
        string id,
        string eventKind,
        string? direction,
        string? applicability,
        int delay)
        => $$"""
           {
             "type": "Feature",
             "geometry": {
               "type": "LineString",
               "coordinates": [[-86.7000, 36.0000], [-86.7000, 36.0010]]
             },
             "properties": {
               "id": "{{id}}",
               "eventKind": "{{eventKind}}",
               "iconCategory": {{(eventKind == "closure" ? 8 : 0)}},
               "delay": {{delay}},
               "confidence": 0.9,
               {{ExtensionProperty(direction, applicability)}}
               "description": "{{eventKind}}"
             }
           }
           """;

    private static string HereFlowJson(
        double speed,
        double freeFlow,
        double jamFactor,
        string? direction)
        => $$"""
           {
             "results": [
               {{HereFlowRecord("flow-1", speed, freeFlow, jamFactor, direction)}}
             ]
           }
           """;

    private static string HereFlowRecord(
        string id,
        double speed,
        double freeFlow,
        double jamFactor,
        string? direction)
        => $$"""
           {
             "id": "{{id}}",
             "location": {
               "shape": {
                 "links": [
                   {
                     "points": [
                       { "lat": 36.0000, "lng": -86.7000 },
                       { "lat": 36.0010, "lng": -86.7000 }
                     ]
                   }
                 ]
               },
               {{ExtensionProperty(direction, null)}}
               "length": 111
             },
             "currentFlow": {
               "speed": {{speed}},
               "freeFlow": {{freeFlow}},
               "traversalTime": 100,
               "freeFlowTravelTime": 80,
               "jamFactor": {{jamFactor}},
               "confidence": 0.9
             }
           }
           """;

    private static string HereIncidentJson(
        string id,
        string type,
        string? direction,
        string? applicability,
        int delay)
        => $$"""
           {
             "results": [
               {{HereIncidentRecord(id, type, direction, applicability, delay)}}
             ]
           }
           """;

    private static string HereIncidentRecord(
        string id,
        string type,
        string? direction,
        string? applicability,
        int delay)
        => $$"""
           {
             "location": {
               "shape": {
                 "links": [
                   {
                     "points": [
                       { "lat": 36.0000, "lng": -86.7000 },
                       { "lat": 36.0010, "lng": -86.7000 }
                     ]
                   }
                 ]
               },
               {{ExtensionProperty(direction, null)}}
               "length": 111
             },
             "incidentDetails": {
               "id": "{{id}}",
               "type": "{{type}}",
               "delay": {{delay}},
               "confidence": 0.9,
               {{ExtensionProperty(null, applicability)}}
               "description": { "value": "{{type}}" }
             }
           }
           """;

    private static string ExtensionProperty(
        string? direction,
        string? applicability)
    {
        if (direction is null && applicability is null)
        {
            return string.Empty;
        }

        if (direction is not null && applicability is not null)
        {
            return $$"""
                "sharpNinjaTraffic": {
                  "geometryDirection": "{{direction}}",
                  "restrictionApplicability": "{{applicability}}"
                },
               """;
        }

        return direction is not null
            ? $$"""
                "sharpNinjaTraffic": { "geometryDirection": "{{direction}}" },
               """
            : $$"""
                "sharpNinjaTraffic": { "restrictionApplicability": "{{applicability}}" },
               """;
    }

    private sealed class RecordingTileWriter : IValhallaTrafficTileWriter
    {
        public List<IReadOnlyList<ValhallaTrafficEdgeUpdate>> Calls { get; } = [];

        public Task<ValhallaTrafficWriteResult> WriteAsync(
            IReadOnlyList<ValhallaTrafficEdgeUpdate> updates,
            ValhallaTrafficWriteOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValhallaTrafficEdgeUpdate[] captured = updates.ToArray();
            Calls.Add(Array.AsReadOnly(captured));
            return Task.FromResult(
                new ValhallaTrafficWriteResult(true, captured.Length, []));
        }
    }

    private sealed class JsonHttpMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class PayloadClient(
        string providerId,
        RawTrafficFeedPayload payload) : ITrafficFeedClient
    {
        public string ProviderId { get; } = providerId;

        public Task<TrafficFeedFetchResult> FetchAsync(
            TrafficDataRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TrafficFeedFetchResult([payload], []));
        }
    }

    private sealed class StubGraphSource(
        IReadOnlyList<TrafficSpatialGraphEdge> edges) : IValhallaTrafficSpatialGraphSource
    {
        public Task<IReadOnlyList<TrafficSpatialGraphEdge>> ReadAsync(
            ValhallaGraphTrafficContext context,
            TrafficSpatialQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(edges);
        }
    }

    private sealed class StaticTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
