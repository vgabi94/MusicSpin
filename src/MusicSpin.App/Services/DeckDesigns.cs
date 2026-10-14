namespace MusicSpin.App.Services;

/// <summary>
/// A turntable design for Now Playing (Settings › Turntable › Design). Each design is its own type, so views pick its
/// preview and its deck by data template.
/// </summary>
public abstract record DeckDesign(string Id, string Name);

/// <summary>The mockup's own deck: a plinth, a platter, the arm on the right and "Music Spin" engraved. The default.</summary>
public sealed record ClassicDeck() : DeckDesign("classic", "Classic");

/// <summary>The designs available so far; each deck issue adds its own.</summary>
public static class DeckDesigns
{
    public static IReadOnlyList<DeckDesign> All { get; } = [new ClassicDeck()];

    public static DeckDesign Default => All[0];

    /// <summary>The design with the <c>settings.json</c> id; unknown ids are the default.</summary>
    public static DeckDesign Parse(string? id)
        => All.FirstOrDefault(d => string.Equals(d.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}
