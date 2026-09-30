namespace MeshCoreMqtt.Core;

public static class MeshTopics
{
    public static string Prefix(Guid spaceId) => $"meshcore/{spaceId.ToString("N")[..12]}";

    public static bool TryPrefix(string topic, out string prefix)
    {
        prefix = "";
        const string head = "meshcore/";
        if (!topic.StartsWith(head, StringComparison.Ordinal))
            return false;
        var slash = topic.IndexOf('/', head.Length);
        if (slash <= head.Length)
            return false;
        prefix = topic[..slash];
        return true;
    }

    public static string Filter(Guid spaceId) => $"{Prefix(spaceId)}/#";

    public static string Tail(Guid spaceId, string topic)
    {
        var head = Prefix(spaceId) + "/";
        return topic.StartsWith(head, StringComparison.Ordinal) ? topic[head.Length..] : topic;
    }
}
