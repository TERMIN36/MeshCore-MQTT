using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshCoreMqtt.Core;

public static class RepeaterConfig
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Encode(string host, int port, string username, string password, string certificate)
    {
        var json = JsonSerializer.Serialize(new Payload(1, host, port, username, password, certificate), Json);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static Payload Decode(string text)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(text));
        return JsonSerializer.Deserialize<Payload>(json, Json)
            ?? throw new InvalidOperationException("Пустая настройка репитера");
    }

    public sealed record Payload(
        int V,
        string Host,
        int Port,
        string User,
        string Pass,
        string Ca);
}
