using MeshCoreMqtt.Core;

namespace MeshCoreMqtt.Core.Tests;

public class TopicRulesTests
{
    [Theory]
    [InlineData("meshcore/LED/ABC/#", "meshcore/LED/ABC/packets", true)]
    [InlineData("meshcore/LED/ABC/#", "meshcore/LED/ABC", true)]
    [InlineData("meshcore/LED/ABC/#", "meshcore/LED/OTHER", false)]
    [InlineData("a/+/c", "a/b/c", true)]
    [InlineData("#", "any/topic", true)]
    public void Matches_filters(string filter, string topic, bool expected) =>
        Assert.Equal(expected, TopicRules.Matches(filter, topic));

    [Theory]
    [InlineData("meshcore/LED/ABC/#", "meshcore/LED/ABC/#", true)]
    [InlineData("meshcore/LED/ABC/#", "meshcore/LED/ABC/packets", true)]
    [InlineData("meshcore/LED/ABC/#", "meshcore/#", false)]
    [InlineData("meshcore/LED/ABC/#", "#", false)]
    [InlineData("a/+/c", "a/b/c", true)]
    [InlineData("a/b/c", "a/+/c", false)]
    public void Covers_subscriptions(string allowed, string subscription, bool expected) =>
        Assert.Equal(expected, TopicRules.Covers(allowed, subscription));

    [Fact]
    public void Hash_must_be_the_last_level() =>
        Assert.False(TopicRules.IsValidFilter("a/#/b"));
}
