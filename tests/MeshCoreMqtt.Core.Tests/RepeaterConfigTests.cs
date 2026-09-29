using MeshCoreMqtt.Core;

namespace MeshCoreMqtt.Core.Tests;

public class RepeaterConfigTests
{
    [Fact]
    public void Roundtrip_keeps_connection_and_certificate()
    {
        const string certificate = """
            -----BEGIN CERTIFICATE-----
            MIIB
            -----END CERTIFICATE-----
            """;

        var text = RepeaterConfig.Encode("mqtt.example", 8883, "user", "pass-word", certificate);
        Assert.DoesNotContain('\n', text);

        var payload = RepeaterConfig.Decode(text);
        Assert.Equal(1, payload.V);
        Assert.Equal("mqtt.example", payload.Host);
        Assert.Equal(8883, payload.Port);
        Assert.Equal("user", payload.User);
        Assert.Equal("pass-word", payload.Pass);
        Assert.Equal(certificate, payload.Ca);
    }
}
