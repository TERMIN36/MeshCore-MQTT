using MeshCoreMqtt.Core;

namespace MeshCoreMqtt.Core.Tests;

public class RepeaterCardsTests
{
    static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    static readonly Guid Third = Guid.Parse("00000000-0000-0000-0000-000000000003");
    static readonly DateTime Seen = new(2026, 10, 1, 18, 1, 9, DateTimeKind.Utc);

    [Fact]
    public void Stored_key_updates_its_card_among_several_empty_ones()
    {
        var echo = new string('a', 64);
        var cards = new[]
        {
            new RepeaterCard(First, "", ""),
            new RepeaterCard(Second, "", echo),
            new RepeaterCard(Third, "", "")
        };
        var heard = new[] { new HeardRepeater(echo, "KLF Echo", null, true, Seen) };

        var matched = RepeaterCards.Match(cards, heard);

        Assert.Null(matched[First]);
        Assert.Equal(echo, matched[Second]);
        Assert.Null(matched[Third]);
    }

    [Fact]
    public void Named_cards_keep_their_nodes_and_leave_an_unbound_hello_alone()
    {
        var echo = new string('b', 64);
        var known = new string('c', 64);
        var cards = new[]
        {
            new RepeaterCard(First, "KLF Termin36 R1", ""),
            new RepeaterCard(Second, "", ""),
            new RepeaterCard(Third, "", "")
        };
        var heard = new[]
        {
            new HeardRepeater(known, "KLF Termin36 R1", "KLF Termin36 R1", true, Seen),
            new HeardRepeater(echo, "KLF Echo", null, true, Seen)
        };

        var matched = RepeaterCards.Match(cards, heard);

        Assert.Equal(known, matched[First]);
        Assert.Null(matched[Second]);
        Assert.Null(matched[Third]);
    }

    [Fact]
    public void Single_empty_card_still_takes_the_only_announcement()
    {
        var echo = new string('d', 64);
        var matched = RepeaterCards.Match(
            [new RepeaterCard(First, "", "")],
            [new HeardRepeater(echo, "KLF Echo", null, true, Seen)]);

        Assert.Equal(echo, matched[First]);
    }

    [Fact]
    public void Keyed_card_does_not_take_another_node_with_the_same_name()
    {
        var mine = new string('e', 64);
        var other = new string('f', 64);
        var matched = RepeaterCards.Match(
            [new RepeaterCard(First, "Old", mine)],
            [new HeardRepeater(other, "Old", null, true, Seen)]);

        Assert.Null(matched[First]);
    }
}
