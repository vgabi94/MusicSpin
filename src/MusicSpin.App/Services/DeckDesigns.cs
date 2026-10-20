namespace MusicSpin.App.Services;

/// <summary>
/// A turntable design for Now Playing (Settings › Turntable › Design). Each design is its own type, so views pick its
/// preview and its deck by data template.
/// </summary>
public abstract record DeckDesign(string Id, string Name);

/// <summary>The mockup's own deck: a plinth, a platter, the arm on the right and "Music Spin" engraved. The default.</summary>
public sealed record ClassicDeck() : DeckDesign("classic", "Classic");

/// <summary>Oiled walnut, a brass inlay line, a brass S-arm and a screwed brass name plate (#25).</summary>
public sealed record WalnutDeck() : DeckDesign("walnut", "Walnut & Brass");

/// <summary>A brushed aluminium slab, a black tube arm and one lamp in the album colour (#26).</summary>
public sealed record AluminiumDeck() : DeckDesign("aluminium", "Brushed Aluminium");

/// <summary>Chamfered carbon fibre, strobe ticks, a speed readout and a signal-lit blade (#27).</summary>
public sealed record CarbonDeck() : DeckDesign("carbon", "Carbon Signal");

/// <summary>Gloss black lacquer, a mirror-chrome rim and a chrome J-arm (#28).</summary>
public sealed record PianoDeck() : DeckDesign("piano", "Piano Black & Chrome");

/// <summary>Champagne aluminium between rosewood ends; a carriage glides in on rails instead of an arm (#29).</summary>
public sealed record LinearDeck() : DeckDesign("linear", "Linear Tracking");

/// <summary>Smoked acrylic the album's glow shows through, a clear platter and an edge light (#30).</summary>
public sealed record AcrylicDeck() : DeckDesign("acrylic", "Smoked Acrylic");

/// <summary>The designs, in Settings' order: the Classic deck first (the default), then the studies in their order.</summary>
public static class DeckDesigns
{
    public static IReadOnlyList<DeckDesign> All { get; } =
        [new ClassicDeck(), new WalnutDeck(), new AluminiumDeck(), new CarbonDeck(), new PianoDeck(), new LinearDeck(), new AcrylicDeck()];

    public static DeckDesign Default => All[0];

    /// <summary>The design with the <c>settings.json</c> id; unknown ids are the default.</summary>
    public static DeckDesign Parse(string? id)
        => All.FirstOrDefault(d => string.Equals(d.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}
