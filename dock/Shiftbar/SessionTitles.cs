using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shiftbar;

/// Fresh session names. Claude Code titles a session once, from its first messages, and never again (its `ai-title` records
/// repeat that one title), so a window that moved on to other work keeps a stale name. At each switch the tray asks Haiku for
/// a title from the session's latest prompts and keeps it here with every title it ever set for that session: a
/// `custom-title` record that is none of them (and not Claude's own title) is the user's `/rename`, which always wins and is
/// never replaced. Written by the tray only; the Remote Control helper reads it.
public sealed class SessionTitles
{
    public sealed class Entry
    {
        public string Title { get; set; } = "";
        /// A hash of the prompts the title was made from: unchanged prompts are not titled again.
        public string Basis { get; set; } = "";
        public List<string> Set { get; set; } = [];
        public DateTime At { get; set; }
    }

    public Dictionary<string, Entry> Sessions { get; set; } = [];

    public static string DefaultPath => Path.Combine(DockSettings.Dir, "titles.json");
    static readonly SemaphoreSlim Gate = new(1);
    const int Prompts = 8, PromptChars = 300;

    public static SessionTitles Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize<SessionTitles>(File.ReadAllText(path ?? DefaultPath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// Drops sessions untitled for 60 days, then replaces the file in one move, so the helper never reads half of it.
    public void Save(DateTime nowUtc, string? path = null)
    {
        path ??= DefaultPath;
        foreach (var k in Sessions.Where(s => nowUtc - s.Value.At > TimeSpan.FromDays(60)).Select(s => s.Key).ToList()) Sessions.Remove(k);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, overwrite: true);
    }

    public string? Current(string sessionId) => Sessions.TryGetValue(sessionId, out var e) && e.Title.Length > 0 ? e.Title : null;

    public void Record(string sessionId, string title, string basis, DateTime nowUtc)
    {
        if (!Sessions.TryGetValue(sessionId, out var e)) Sessions[sessionId] = e = new();
        (e.Title, e.Basis, e.At) = (title, basis, nowUtc);
        if (!e.Set.Contains(title)) e.Set.Add(title);
    }

    /// The user's own name for the session: the newest `custom-title` that is neither Claude's title nor one Shiftbar set
    /// (typing `/remote-control <title>` may record it as a custom title too).
    public string? Renamed(string sessionId, IReadOnlyList<string> customs, string? ai) =>
        customs.LastOrDefault(c => c != ai && !(Sessions.TryGetValue(sessionId, out var e) && e.Set.Contains(c)));

    /// The user's latest requests in the transcript, oldest first, each cut to 300 characters. Typed prompts only: no tool
    /// results, meta records, slash-command echoes or interruptions.
    public static List<string> RecentPrompts(string transcript)
    {
        var found = new List<string>();
        try
        {
            foreach (var line in ResumeStopped.Tail(transcript, 2 * 1024 * 1024))
            {
                if (!line.Contains("\"user\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object || Str(r, "type") != "user" || Flag(r, "isMeta") || Flag(r, "isCompactSummary")
                        || r.TryGetProperty("toolUseResult", out _) || !r.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object
                        || !m.TryGetProperty("content", out var c)) continue;
                    var text = c.ValueKind == JsonValueKind.String ? c.GetString()
                        : c.ValueKind == JsonValueKind.Array && c.EnumerateArray().All(p => Str(p, "type") is "text" or "image")
                            ? string.Join(" ", c.EnumerateArray().Select(p => Str(p, "text")).Where(t => t != null)) : null;
                    text = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (text.Length == 0 || text[0] == '<' || text.StartsWith("[Request interrupted", StringComparison.Ordinal)) continue;
                    found.Add(text.Length > PromptChars ? text[..PromptChars] : text);
                }
                catch (JsonException) { } // a line cut by the tail window, or one being written
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found.TakeLast(Prompts).ToList();
    }

    public static string Basis(IEnumerable<string> prompts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", prompts))))[..16];

    /// What Haiku is asked. The current name goes with it, so a session still on the same work keeps its name.
    public static string Ask(IReadOnlyList<string> prompts, string? current) =>
        "Name this Claude Code session. Below are the user's most recent requests in it, oldest first."
        + (current != null ? $" Its current name is \"{current}\"; keep it if it still fits." : "")
        + $" Reply with only a title of 2 to {MaxWords} words for what the session is about now: no quotes, no ending punctuation.\n\n"
        + string.Join("\n", prompts.Select((p, i) => $"{i + 1}. {p}"));

    /// Haiku's reply as a title: its first line without quotes, markdown or ending punctuation, then `Typeable`.
    public static string? Clean(string? reply)
    {
        var first = reply?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return Short(RemoteControl.Typeable(first?.Trim('"', '\'', '*', '#', '`', '“', '”', ' ').TrimEnd('.', '!', ':', ' ')));
    }

    /// Titles Shiftbar picks (Haiku's, or Claude's own as a fallback) are at most four words; the user's /rename is kept whole.
    /// A cut never ends on a joining word ("API Key Setup for"), nor on a dash or comma.
    public const int MaxWords = 4;
    static readonly HashSet<string> Joiners = new(StringComparer.OrdinalIgnoreCase)
        { "a", "an", "the", "and", "or", "of", "for", "to", "in", "on", "at", "by", "with", "from", "into", "via", "vs", "&", "-", "–", "—" };
    public static string? Short(string? title)
    {
        if (title == null) return null;
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(MaxWords).ToList();
        while (words.Count > 1 && Joiners.Contains(words[^1].TrimEnd(',', ';', ':'))) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words).TrimEnd(',', ';', ':', '-', '–', '—', ' ');
    }

    /// Titles the session again from its latest prompts, on `account` (the default login). Nothing happens when the user
    /// renamed it, it has no typed prompts, or they are the ones it was last titled from. Never throws.
    public static async Task RefreshAsync(Engine engine, int account, string sessionId, string transcript)
    {
        try
        {
            var (customs, ai) = await Task.Run(() => RemoteControl.Titles(transcript));
            var prompts = await Task.Run(() => RecentPrompts(transcript));
            var titles = Load();
            if (prompts.Count == 0 || titles.Renamed(sessionId, customs, ai) != null) return;
            var basis = Basis(prompts);
            if (titles.Sessions.TryGetValue(sessionId, out var was) && was.Basis == basis) return;
            if (Clean(await engine.TitleAsync(account, Ask(prompts, titles.Current(sessionId) ?? Short(RemoteControl.Typeable(ai))))) is not { } title) return;
            await Gate.WaitAsync();
            try
            {
                titles = Load(); // another session's title may have been saved meanwhile
                titles.Record(sessionId, title, basis, DateTime.UtcNow);
                titles.Save(DateTime.UtcNow);
            }
            finally { Gate.Release(); }
            Program.Log($"session title: {sessionId} is now \"{title}\"");
        }
        catch (Exception e) { Program.Log(e); }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

public sealed partial class Engine
{
    /// One title request: Haiku, low effort, one turn, no tools, MCP servers or customizations, nothing saved, the prompt on
    /// stdin (a user's text never rides in a command line). On the default login: `run N` with the active account is plain claude.
    internal static string[] TitleArgs(int account) =>
    [
        "run", account.ToString(), "--",
        "-p", "--model", "haiku", "--effort", "low", "--output-format", "json",
        "--no-session-persistence", "--safe-mode", "--strict-mcp-config", "--disable-slash-commands",
        "--setting-sources", "project", "--tools", "",
    ];

    /// Claude's answer text, or null on any failure. Not behind the engine gate: `run` on the default login changes nothing
    /// the engine owns, and a switch or listing must not wait on Haiku.
    public async Task<string?> TitleAsync(int account, string prompt)
    {
        var dir = Path.Combine(DockSettings.Dir, "prime");
        Directory.CreateDirectory(dir); // an empty folder: no project instructions to read
        EngineResult r;
        try { r = await RunAsync(TitleArgs(account), TimeSpan.FromSeconds(60), input: prompt, unset: PrimeUnset(Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList()), workDir: dir, gated: false); }
        catch (EngineException) { return null; }
        var json = r.Stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) && t.GetString() == "result"
                && e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.False
                && e.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
