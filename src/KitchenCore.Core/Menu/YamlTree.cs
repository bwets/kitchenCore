using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace KitchenCore.Core.Menu;

/// <summary>
/// A deliberately permissive YAML tree.
///
/// YamlDotNet's own node model backs mappings with a dictionary, which rejects a
/// file that repeats a key. Repeated keys are exactly what a person hand-editing
/// a day's slots produces, and the app promises to surface that as an error
/// rather than fall over -- so mappings here keep an ordered *list* of pairs and
/// duplicates simply survive parsing.
/// </summary>
public abstract record YamlTree
{
    public sealed record Scalar(string Value, ScalarStyle Style = ScalarStyle.Any) : YamlTree;

    public sealed record Sequence(IReadOnlyList<YamlTree> Items) : YamlTree;

    public sealed record Mapping(IReadOnlyList<KeyValuePair<string, YamlTree>> Pairs) : YamlTree
    {
        /// <summary>First value for a key, or null. Use <see cref="All"/> when duplicates matter.</summary>
        public YamlTree? Get(string key) =>
            Pairs.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        public IEnumerable<YamlTree> All(string key) =>
            Pairs.Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value);
    }

    /// <summary>Parses the first document. Returns null for an empty file.</summary>
    public static YamlTree? Parse(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));

        parser.Consume<StreamStart>();

        if (parser.Accept<StreamEnd>(out _))
        {
            return null;
        }

        parser.Consume<DocumentStart>();
        var root = ReadNode(parser);
        parser.Consume<DocumentEnd>();

        return root;
    }

    private static YamlTree ReadNode(IParser parser)
    {
        if (parser.TryConsume<YamlDotNet.Core.Events.Scalar>(out var scalar))
        {
            return new Scalar(scalar.Value, scalar.Style);
        }

        if (parser.TryConsume<SequenceStart>(out _))
        {
            var items = new List<YamlTree>();
            while (!parser.TryConsume<SequenceEnd>(out _))
            {
                items.Add(ReadNode(parser));
            }

            return new Sequence(items);
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            var pairs = new List<KeyValuePair<string, YamlTree>>();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                // A non-scalar key is legal YAML but meaningless here; render it
                // so the file still loads and the oddity shows up downstream.
                var key = parser.TryConsume<YamlDotNet.Core.Events.Scalar>(out var keyScalar)
                    ? keyScalar.Value
                    : Describe(ReadNode(parser));

                pairs.Add(new KeyValuePair<string, YamlTree>(key, ReadNode(parser)));
            }

            return new Mapping(pairs);
        }

        // Anchors and aliases are not used by this app's files; consuming the
        // event keeps the parser moving instead of throwing.
        parser.MoveNext();
        return new Scalar(string.Empty);
    }

    private static string Describe(YamlTree node) => node switch
    {
        Scalar s => s.Value,
        Sequence => "(sequence key)",
        Mapping => "(mapping key)",
        _ => "(?)",
    };
}
