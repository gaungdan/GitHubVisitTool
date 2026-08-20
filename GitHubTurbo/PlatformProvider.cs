using System.Collections.ObjectModel;
using Color = System.Windows.Media.Color;

namespace GitHubTurbo;

public sealed record PlatformDomain(string Label, string Domain);

public sealed record PlatformDescriptor(
    string Id,
    string Title,
    string Subtitle,
    string Abbreviation,
    Color BrandColor);

public interface IPlatformProvider
{
    PlatformDescriptor Descriptor { get; }
    IReadOnlyList<PlatformDomain> Domains { get; }

    string MarkerStart => $"# {Descriptor.Id} Start";
    string MarkerEnd => $"# {Descriptor.Id} End";

    /// <summary>
    /// Whether this platform has legacy hosts entries from an older version that should be migrated.
    /// </summary>
    bool HasLegacyEntries => false;
}

public static class PlatformProviders
{
    private static readonly List<IPlatformProvider> _providers = new();
    private static bool _initialized;

    public static void Register(IPlatformProvider provider) => _providers.Add(provider);

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        Register(GitHubPlatformProvider.Instance);
        Register(GitLabPlatformProvider.Instance);
    }

    public static IReadOnlyList<IPlatformProvider> All
    {
        get
        {
            EnsureInitialized();
            return _providers.AsReadOnly();
        }
    }

    public static IPlatformProvider? GetById(string id)
    {
        EnsureInitialized();
        return _providers.FirstOrDefault(p => p.Descriptor.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}
