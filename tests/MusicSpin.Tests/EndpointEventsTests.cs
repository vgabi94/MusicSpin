using MusicSpin.Core.Output.Wasapi;

namespace MusicSpin.Tests;

/// <summary>
/// What WASAPI's endpoint notifications mean for a stream holding one endpoint. The COM plumbing needs Windows and a
/// device; these decisions do not.
/// </summary>
public sealed class EndpointEventsTests
{
    private const string Dac = "{0.0.0.00000000}.{aaaa}";
    private const string Speakers = "{0.0.0.00000000}.{bbbb}";

    [Theory]
    [InlineData(2)] // disabled
    [InlineData(4)] // not present
    [InlineData(8)] // unplugged
    [InlineData(0)]
    public void The_held_endpoint_leaving_the_active_state_is_a_loss(int newState)
        => Assert.Equal("The audio device was disabled or unplugged.", EndpointEvents.LostByState(Dac, Dac, newState));

    [Fact]
    public void The_held_endpoint_staying_active_is_not_a_loss()
        => Assert.Null(EndpointEvents.LostByState(Dac, Dac, EndpointEvents.StateActive));

    [Fact]
    public void Another_endpoints_state_change_is_not_ours()
        => Assert.Null(EndpointEvents.LostByState(Dac, Speakers, 8));

    [Fact]
    public void Endpoint_ids_are_compared_ignoring_case()
        => Assert.NotNull(EndpointEvents.LostByState(Dac, Dac.ToUpperInvariant(), 8));

    [Theory]
    [InlineData(null, Dac)]
    [InlineData(Dac, null)]
    [InlineData(null, null)]
    public void A_missing_id_never_matches_so_nothing_is_reported_for_it(string? held, string? changed)
    {
        Assert.Null(EndpointEvents.LostByState(held, changed, 8));
        Assert.Null(EndpointEvents.LostByRemoval(held, changed));
    }

    [Fact]
    public void The_held_endpoint_being_removed_is_a_loss_and_another_one_is_not()
    {
        Assert.Equal("The audio device was removed.", EndpointEvents.LostByRemoval(Dac, Dac));
        Assert.Null(EndpointEvents.LostByRemoval(Dac, Speakers));
    }

    [Fact]
    public void A_stream_that_follows_the_default_moves_when_the_multimedia_default_becomes_another_endpoint()
        => Assert.True(EndpointEvents.DefaultMovedAway(followsDefault: true, Dac, flow: 0, role: 1, Speakers));

    [Fact]
    public void A_stream_on_a_chosen_endpoint_ignores_default_changes()
        => Assert.False(EndpointEvents.DefaultMovedAway(followsDefault: false, Dac, flow: 0, role: 1, Speakers));

    [Theory]
    [InlineData(1, 1)] // a capture default
    [InlineData(0, 0)] // eConsole: reported together with eMultimedia for the same change
    [InlineData(0, 2)] // eCommunications
    public void Other_flows_and_roles_are_not_the_default_the_stream_opened_with(int flow, int role)
        => Assert.False(EndpointEvents.DefaultMovedAway(true, Dac, flow, role, Speakers));

    [Fact]
    public void The_default_becoming_the_endpoint_already_held_is_not_a_move()
        => Assert.False(EndpointEvents.DefaultMovedAway(true, Dac, 0, 1, Dac.ToUpperInvariant()));

    [Fact]
    public void No_default_at_all_is_left_to_the_streams_own_loss()
        => Assert.False(EndpointEvents.DefaultMovedAway(true, Dac, 0, 1, null));
}
