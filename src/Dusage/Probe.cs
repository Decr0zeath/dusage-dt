using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dusage;

/// <summary>
/// <c>dusage --probe</c> fetches once and prints what the widget would show.
/// <c>dusage --probe-raw</c> also prints each response's layout for troubleshooting: numbers, dates and short
/// enum-like words are kept, any other text (ids, emails) is redacted.
/// </summary>
static partial class Probe
{
    public static int Run(bool raw)
    {
        if (!Console.IsOutputRedirected) Native.AttachConsole(-1);
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console to configure; the caller decides how to decode.
        }
        return Task.Run(async () =>
        {
            var failures = 0;
            var now = DateTimeOffset.Now;
            foreach (var source in new IUsageSource[] { new ClaudeSource(), new CodexSource() })
            {
                Console.WriteLine();
                try
                {
                    var snapshot = await source.FetchAsync(CancellationToken.None, raw ? Dump : null);
                    Console.WriteLine($"{source.Name} ({Fmt.Title(snapshot.Plan ?? "unknown plan")})");
                    Line("5-hour", snapshot.Session, now);
                    Line("Weekly", snapshot.Weekly, now);
                    foreach (var extra in snapshot.Extra)
                    {
                        if (extra.Session is { } s) Line($"{extra.Label} 5-hour", s, now);
                        if (extra.Weekly is { } w) Line($"{extra.Label} weekly", w, now);
                    }
                }
                catch (UsageException e)
                {
                    failures++;
                    Console.WriteLine($"{source.Name}: {e.Message}");
                }
            }
            return failures;
        }).GetAwaiter().GetResult();
    }

    static void Line(string label, UsageWindow? w, DateTimeOffset now) =>
        Console.WriteLine(w is null
            ? $"  {label,-14} no data"
            : $"  {label,-14} {Fmt.Pct(w.PercentAt(now)),3}%  {Fmt.Reset(w, now)}");

    static void Dump(JsonElement root)
    {
        Console.WriteLine("  response:");
        Dump(root, "    ");
    }

    static void Dump(JsonElement e, string indent)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            Console.WriteLine($"{indent}[{e.GetArrayLength()} items]");
            var shown = 0;
            foreach (var item in e.EnumerateArray())
            {
                if (shown++ == 8) break;
                Console.WriteLine($"{indent}  -");
                Dump(item, indent + "    ");
            }
            return;
        }
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var property in e.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                Console.WriteLine($"{indent}{property.Name}:");
                Dump(property.Value, indent + "  ");
            }
            else
            {
                Console.WriteLine($"{indent}{property.Name}: {Leaf(property.Value)}");
            }
        }
    }

    static string Leaf(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) return v.GetRawText();
        var s = v.GetString()!;
        return EnumWord().IsMatch(s) || DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? $"\"{s}\""
            : $"<text, {s.Length} chars>";
    }

    [GeneratedRegex("^[a-z_]{1,20}$")]
    private static partial Regex EnumWord();
}
