using System.Reflection;
using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class PooledProductionPathGuardTests
{
    [Fact]
    public void PooledCallGraphTypes_HaveNoGlobalTileByteDictionaryFields()
    {
        Type[] types =
        [
            typeof(PooledRoadEnhanceStage),
            typeof(PooledRoadRestrictionStage),
            typeof(BoundedRoadTileWriter),
            typeof(PooledRoadEdgeBuilder),
            typeof(ManagedRoadGraphBuilder),
        ];

        foreach (Type type in types)
        {
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Static |
                         BindingFlags.Public |
                         BindingFlags.NonPublic |
                         BindingFlags.FlattenHierarchy))
            {
                if (!field.FieldType.IsGenericType)
                {
                    continue;
                }

                Type def = field.FieldType.GetGenericTypeDefinition();
                if (def != typeof(Dictionary<,>) &&
                    def != typeof(IDictionary<,>) &&
                    def != typeof(IReadOnlyDictionary<,>))
                {
                    continue;
                }

                Type[] args = field.FieldType.GenericTypeArguments;
                Assert.False(
                    args.Length == 2 &&
                    args[0] == typeof(GraphId) &&
                    args[1] == typeof(byte[]),
                    $"{type.Name}.{field.Name} must not be Dictionary<GraphId, byte[]>");
            }
        }
    }

    [Fact]
    public void NodeWorkItem_IsUnmanagedReferenceFree()
    {
        Type? nodeWorkItem = typeof(PooledNodeArena).Assembly
            .GetTypes()
            .FirstOrDefault(t => t.Name == "NodeWorkItem");
        Assert.NotNull(nodeWorkItem);
        MethodInfo? isRef = typeof(RuntimeHelpers)
            .GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
            .MakeGenericMethod(nodeWorkItem!);
        bool containsRefs = (bool)isRef.Invoke(null, null)!;
        Assert.False(containsRefs);
    }

    [Fact]
    public void PooledBuilder_HasNoLegacyOsmWayNodeListFields()
    {
        foreach (FieldInfo field in typeof(PooledRoadEdgeBuilder).GetFields(
                     BindingFlags.Instance |
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.NonPublic))
        {
            string name = field.Name;
            Assert.DoesNotContain("OsmWay", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WayNodeList", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BuildPooledFrontierAsync_DoesNotReturnCompleteInMemoryTileDictionary()
    {
        MethodInfo? method = typeof(ManagedRoadGraphBuilder)
            .GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.NonPublic |
                BindingFlags.Public)
            .FirstOrDefault(m => m.Name.Contains("BuildPooledFrontier", StringComparison.Ordinal));
        Assert.NotNull(method);
        // Return type is ValueTask<ManagedRoadGraphBuildResult> (or Task equivalent), never a tile dictionary.
        Assert.Contains(
            "ManagedRoadGraphBuildResult",
            method!.ReturnType.FullName ?? method.ReturnType.Name,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Dictionary",
            method.ReturnType.FullName ?? method.ReturnType.Name,
            StringComparison.Ordinal);
    }
}
