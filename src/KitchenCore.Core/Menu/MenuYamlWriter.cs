using System.Globalization;
using System.Text;
using KitchenCore.Shared;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace KitchenCore.Core.Menu;

/// <summary>
/// Writes a shard file back out.
///
/// Two properties matter more than they might elsewhere, because this folder is
/// synced to git and read by people:
///
/// 1. <b>Determinism.</b> Days ascend, slots follow the configured order, and
///    keys within an entry are always written in the same sequence. Re-saving an
///    unchanged file produces an identical file, so a diff only ever shows what
///    actually changed.
/// 2. <b>Validity.</b> The reader tolerates a repeated slot key, but the writer
///    never produces one: a slot holding several entries is written as a
///    sequence, which every YAML parser accepts.
/// </summary>
public sealed class MenuYamlWriter(IReadOnlyList<SlotDefinition> slots)
{
    private readonly Dictionary<string, int> _slotOrder = slots
        .OrderBy(s => s.Order)
        .Select((s, index) => (s.Key, index))
        .ToDictionary(x => x.Key, x => x.index, StringComparer.OrdinalIgnoreCase);

    public string Write(int year, IReadOnlyList<MenuDayRecord> days)
    {
        var output = new StringWriter { NewLine = "\n" };
        var emitter = new Emitter(output, new EmitterSettings().WithBestIndent(2).WithIndentedSequences());

        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, isImplicit: true));
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Block));

        emitter.Emit(Key("year"));
        emitter.Emit(new Scalar(null, null, year.ToString(CultureInfo.InvariantCulture), ScalarStyle.Plain, true, false));

        emitter.Emit(Key("days"));
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Block));

        foreach (var day in days.Where(d => d.Slots.Count > 0).OrderBy(d => d.Date))
        {
            emitter.Emit(Key(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            WriteDay(emitter, day);
        }

        emitter.Emit(new MappingEnd());
        emitter.Emit(new MappingEnd());
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());

        return output.ToString();
    }

    private void WriteDay(Emitter emitter, MenuDayRecord day)
    {
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Block));

        // Group so several entries in one slot become a sequence under a single
        // key, rather than a repeated key that a strict parser would reject.
        var groups = day.Slots
            .GroupBy(s => s.Slot, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => _slotOrder.TryGetValue(g.Key, out var order) ? order : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            emitter.Emit(Key(group.Key));

            var entries = group.Select(g => g.Entry).ToList();

            if (entries.Count == 1)
            {
                WriteEntry(emitter, entries[0]);
                continue;
            }

            emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Block));
            foreach (var entry in entries)
            {
                WriteEntry(emitter, entry);
            }

            emitter.Emit(new SequenceEnd());
        }

        emitter.Emit(new MappingEnd());
    }

    private static void WriteEntry(Emitter emitter, MenuEntry entry)
    {
        emitter.Emit(new MappingStart(null, null, true, MappingStyle.Block));

        emitter.Emit(Key("title"));
        emitter.Emit(Value(entry.Title));

        if (!string.IsNullOrWhiteSpace(entry.Notes))
        {
            emitter.Emit(Key("notes"));

            // Multi-line notes read far better as a literal block than as an
            // escaped one-liner, and diff line-by-line when edited. A one-liner
            // stays a plain scalar.
            var multiline = entry.Notes.Contains('\n') || entry.Notes.Contains('\r');
            var style = multiline ? ScalarStyle.Literal : ScalarStyle.Any;
            emitter.Emit(new Scalar(null, null, Normalize(entry.Notes, multiline), style, true, false));
        }

        if (entry.Status != EntryStatus.Planned)
        {
            // Planned is the default; writing it would be noise in every entry.
            emitter.Emit(Key("status"));
            emitter.Emit(Value(entry.Status.ToString().ToLowerInvariant()));
        }

        if (!string.IsNullOrWhiteSpace(entry.RequestedBy))
        {
            emitter.Emit(Key("requestedBy"));
            emitter.Emit(Value(entry.RequestedBy));
        }

        if (entry.Links.Count > 0)
        {
            emitter.Emit(Key("links"));
            emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Block));

            foreach (var link in entry.Links)
            {
                emitter.Emit(Value(link));
            }

            emitter.Emit(new SequenceEnd());
        }

        emitter.Emit(new MappingEnd());
    }

    private static Scalar Key(string key) => new(null, null, key, ScalarStyle.Plain, true, false);

    private static Scalar Value(string value) => new(null, null, value, ScalarStyle.Any, true, false);

    /// <summary>
    /// A literal block scalar cannot carry CRLF, and trailing blank lines round-trip
    /// badly, so notes are normalised on the way out.
    ///
    /// The trailing newline belongs only to the literal form. Adding it to a
    /// one-line note forces the emitter out of a plain scalar and into a folded
    /// '>' block -- a lot of ceremony for three words, and it made hand-written
    /// files look mangled after their first save from the app.
    /// </summary>
    private static string Normalize(string text, bool multiline)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

        if (normalized.Length == 0)
        {
            return text.Trim();
        }

        return multiline ? normalized + "\n" : normalized;
    }
}
