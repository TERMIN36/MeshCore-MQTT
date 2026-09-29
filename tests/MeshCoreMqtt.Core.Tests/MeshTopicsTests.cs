using MeshCoreMqtt.Core;

namespace MeshCoreMqtt.Core.Tests;

public class MeshTopicsTests
{
    [Fact]
    public void Prefix_hides_the_tunnel_and_tail_is_the_pubkey()
    {
        var id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        Assert.Equal("meshcore/aaaaaaaabbbb", MeshTopics.Prefix(id));
        Assert.Equal("meshcore/aaaaaaaabbbb/#", MeshTopics.Filter(id));
        Assert.Equal("pk", MeshTopics.Tail(id, "meshcore/aaaaaaaabbbb/pk"));
    }
}
