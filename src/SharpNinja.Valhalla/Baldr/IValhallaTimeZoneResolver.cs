namespace SharpNinja.Valhalla.Baldr;

/// <summary>Resolves Valhalla graph timezone indexes without provider dependencies.</summary>
public interface IValhallaTimeZoneResolver
{
    /// <summary>Attempts to resolve a graph timezone index.</summary>
    bool TryResolve(uint index, out TimeZoneInfo? timeZone);
}

/// <summary>Resolves the timezone identities pinned by Valhalla 3.8.3.</summary>
public sealed class ValhallaTimeZoneResolver : IValhallaTimeZoneResolver
{
    public static ValhallaTimeZoneResolver Instance { get; } = new();

    private ValhallaTimeZoneResolver()
    {
    }

    public bool TryResolve(uint index, out TimeZoneInfo? timeZone)
    {
        timeZone = null;
        if (!ValhallaTimeZoneCatalog.TryGetName(index, out string? name) ||
            string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(name);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
