using System.Text.Json.Nodes;

namespace Augmentor;

public static class JsonExtensions
{
    public static JsonArray GetOrAddArray(this JsonNode node, string name)
    {
        if (node[name] == null)
        {
            node[name] = new JsonArray();
        }

        return node[name].AsArray();
    }

    public static bool TryGetArray(this JsonNode node, string name, out JsonArray result)
    {
        result = node[name] as JsonArray;

        return result != null;
    }

    public static void AddToArray(this JsonNode node, string name, JsonNode value)
    {
        node.GetOrAddArray(name).Add(value);
    }

    public static T To<T>(this JsonNode node, string name)
    {
        return node[name].GetValue<T>();
    }

    public static bool Eq<T>(this JsonNode node, string name, T comparand, IEqualityComparer<T> comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;

        return comparer.Equals(node.To<T>(name), comparand);
    }
}
