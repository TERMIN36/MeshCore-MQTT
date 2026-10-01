namespace MeshCoreMqtt.Core;

public sealed record RepeaterCard(Guid Id, string Name, string PublicKey);

public sealed record HeardRepeater(string PublicKey, string Name, string? AdvertName, bool Announced, DateTime LastSeen);

public static class RepeaterCards
{
    public static Dictionary<Guid, string?> Match(IReadOnlyList<RepeaterCard> cards, IReadOnlyList<HeardRepeater> heard)
    {
        var result = cards.ToDictionary(card => card.Id, _ => (string?)null);
        var remaining = heard.ToList();

        foreach (var card in cards)
        {
            if (card.PublicKey.Length == 0)
                continue;
            var bound = Take(remaining, node => string.Equals(node.PublicKey, card.PublicKey, StringComparison.OrdinalIgnoreCase));
            if (bound is not null)
                result[card.Id] = bound.PublicKey;
        }

        foreach (var card in cards)
        {
            if (result[card.Id] is not null || card.PublicKey.Length > 0 || card.Name.Length == 0)
                continue;
            var named = Take(remaining, node => NamesMatch(card.Name, node));
            if (named is not null)
                result[card.Id] = named.PublicKey;
        }

        // Приветствие и пульс шлёт сам репитер. Остальные ключи — соседи, которых он уже слышал.
        var unmatched = cards.Where(card => result[card.Id] is null && card.PublicKey.Length == 0).ToList();
        var announced = remaining.Where(node => node.Announced).ToList();
        if (unmatched.Count == 1 && announced.Count == 1)
            result[unmatched[0].Id] = announced[0].PublicKey;
        else if (unmatched.Count == 1 && remaining.Count == 1)
            result[unmatched[0].Id] = remaining[0].PublicKey;
        return result;
    }

    static HeardRepeater? Take(List<HeardRepeater> remaining, Func<HeardRepeater, bool> match)
    {
        var chosen = remaining.Where(match).OrderByDescending(node => node.LastSeen).FirstOrDefault();
        if (chosen is null)
            return null;
        remaining.Remove(chosen);
        return chosen;
    }

    static bool NamesMatch(string name, HeardRepeater node) =>
        (node.Name.Length > 0 && string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) ||
        (node.AdvertName is { Length: > 0 } advert && string.Equals(advert, name, StringComparison.OrdinalIgnoreCase));
}
