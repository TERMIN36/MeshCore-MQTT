namespace MeshCoreMqtt.Core;

public static class TopicRules
{
    public static bool IsValidFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Length > 512 || filter.Contains('\0'))
            return false;

        var parts = filter.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == "#")
                return i == parts.Length - 1;
            if (part.Contains('#') || part.Contains('+'))
                return part == "+";
        }

        return true;
    }

    public static bool Matches(string filter, string topic)
    {
        if (!IsValidFilter(filter) || topic.Contains('+') || topic.Contains('#'))
            return false;

        var filters = filter.Split('/');
        var topics = topic.Split('/');
        var i = 0;
        for (; i < filters.Length; i++)
        {
            if (filters[i] == "#")
                return i == filters.Length - 1;
            if (i >= topics.Length)
                return false;
            if (filters[i] != "+" && filters[i] != topics[i])
                return false;
        }

        return i == topics.Length;
    }

    public static bool Covers(string allowed, string subscription)
    {
        if (!IsValidFilter(allowed) || !IsValidFilter(subscription))
            return false;

        var allow = allowed.Split('/');
        var sub = subscription.Split('/');
        var i = 0;
        var j = 0;
        while (i < sub.Length && j < allow.Length)
        {
            if (allow[j] == "#")
                return j == allow.Length - 1;
            if (sub[i] == "#")
                return false;
            if (allow[j] == "+")
            {
                i++;
                j++;
                continue;
            }

            if (sub[i] == "+" || sub[i] != allow[j])
                return false;
            i++;
            j++;
        }

        if (j < allow.Length && allow[j] == "#" && j == allow.Length - 1)
            return i == sub.Length;
        return i == sub.Length && j == allow.Length;
    }

    public static bool AllowsPublish(IEnumerable<string> filters, string topic) =>
        filters.Any(filter => Matches(filter, topic));

    public static bool AllowsSubscribe(IEnumerable<string> filters, string requested)
    {
        if (requested.Contains('+') || requested.Contains('#'))
            return filters.Any(filter => Covers(filter, requested));
        return filters.Any(filter => Matches(filter, requested));
    }
}
