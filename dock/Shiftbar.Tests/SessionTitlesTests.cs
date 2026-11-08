using System.Text.Json;
using Shiftbar;
using Xunit;

namespace Shiftbar.Tests;

/// Synthetic transcripts and a store in a temp folder: never the owner's sessions or titles.
public class SessionTitlesTests
{
    static string User(string text) => JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });
    static string Ai(string t) => JsonSerializer.Serialize(new { type = "ai-title", aiTitle = t, sessionId = "s1" });
    static string Custom(string t) => JsonSerializer.Serialize(new { type = "custom-title", customTitle = t, sessionId = "s1" });

    [Fact]
    public void TheUsersRenameBeatsShiftbarsTitleWhichBeatsClaudes()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "s1.jsonl");
        var store = new SessionTitles();
        File.WriteAllLines(f, [Ai("Login page fix"), User("fix the login page")]);
        Assert.Equal("Login page fix", RemoteControl.TitleOf(f, store));
        store.Record("s1", "Billing export bug", "b1", DateTime.UtcNow);
        Assert.Equal("Billing export bug", RemoteControl.TitleOf(f, store)); // the fresh title replaces Claude's stale one
        // Typing `/remote-control <title>` may record it as a custom title: still Shiftbar's, so the next fresh title wins.
        File.AppendAllLines(f, [Custom("Billing export bug"), Custom("Login page fix")]);
        store.Record("s1", "Invoice PDF layout", "b2", DateTime.UtcNow);
        Assert.Equal("Invoice PDF layout", RemoteControl.TitleOf(f, store));
        Assert.Null(store.Renamed("s1", RemoteControl.Titles(f).Customs, "Login page fix"));
        File.AppendAllLines(f, [Custom("My billing work")]); // the user's /rename sticks
        Assert.Equal("My billing work", RemoteControl.TitleOf(f, store));
        Assert.Equal("My billing work", store.Renamed("s1", RemoteControl.Titles(f).Customs, "Login page fix"));
    }

    [Fact]
    public void OnlyTypedPromptsAreRead()
    {
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "s1.jsonl");
        var toolResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"toolUseResult":{}}""";
        var meta = """{"type":"user","isMeta":true,"message":{"role":"user","content":"Caveat: the messages below"}}""";
        var parts = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"look at"},{"type":"image"},{"type":"text","text":"this   screenshot"}]}}""";
        File.WriteAllLines(f, [User("first"), toolResult, meta, User("<command-name>/rename</command-name>"), User("[Request interrupted by user]"),
            parts, Ai("x"), User(new string('a', 500)), "{\"type\":\"user\",\"mess"]);
        var got = SessionTitles.RecentPrompts(f);
        Assert.Equal(["first", "look at this screenshot", new string('a', 300)], got);
        File.WriteAllLines(f, Enumerable.Range(1, 12).Select(i => User($"p{i}")));
        Assert.Equal(Enumerable.Range(5, 8).Select(i => $"p{i}"), SessionTitles.RecentPrompts(f)); // the newest eight
        Assert.Empty(SessionTitles.RecentPrompts(Path.Combine(t.Path, "missing.jsonl")));
        Assert.Equal(SessionTitles.Basis(["a", "b"]), SessionTitles.Basis(["a", "b"]));
        Assert.NotEqual(SessionTitles.Basis(["a", "b"]), SessionTitles.Basis(["a", "c"]));
    }

    [Fact]
    public void HaikusReplyIsCleanedToATypeableTitle()
    {
        Assert.Equal("Billing export bug", SessionTitles.Clean("\"Billing export bug.\"\n"));
        Assert.Equal("Invoice PDF layout", SessionTitles.Clean("**Invoice PDF layout**\nBecause the user asked…"));
        Assert.Null(SessionTitles.Clean("🚀🚀"));
        Assert.Null(SessionTitles.Clean(""));
        Assert.Null(SessionTitles.Clean(null));
        var ask = SessionTitles.Ask(["fix login", "now billing"], "Login fix");
        Assert.Contains("\"Login fix\"", ask);
        Assert.EndsWith("1. fix login\n2. now billing", ask);
        Assert.DoesNotContain("current name", SessionTitles.Ask(["x"], null));
        Assert.Contains("2 to 4 words", ask);
        Assert.Null(SessionTitles.TooLong("API Key Setup"));
        Assert.Null(SessionTitles.TooLong("\"Fix the login page.\""));
        Assert.Equal("API Key Setup for Individual Org", SessionTitles.TooLong("API Key Setup for Individual Org\nmore"));
        Assert.Contains("\"API Key Setup for Individual Org\" has more than 4 words", SessionTitles.AskShorter(ask, "API Key Setup for Individual Org"));
    }

    [Fact]
    public void ShiftbarsTitlesAreAtMostFourWordsButARenameIsKeptWhole()
    {
        Assert.Equal("Fixing invoice PDF totals", SessionTitles.Clean("Fixing invoice PDF totals and footer"));
        Assert.Equal("Fix login and billing", SessionTitles.Short("Fix login and billing, then export")); // no dangling comma
        Assert.Equal("Login page fix", SessionTitles.Short("Login page fix"));
        Assert.Equal("Drill desk setup", SessionTitles.Short("Drill desk setup - and more"));
        Assert.Equal("API Key Setup", SessionTitles.Short("API Key Setup for Individual Org")); // no dangling joiner
        Assert.Equal("Fix login", SessionTitles.Short("Fix login and the billing page"));
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "s1.jsonl");
        var store = new SessionTitles();
        File.WriteAllLines(f, [Ai("Drill Desk setup and product realignment")]);
        Assert.Equal("Drill Desk setup", RemoteControl.TitleOf(f, store)); // Claude's own title, cut, no dangling "and"
        store.Record("s1", "An older six word stored title", "b", DateTime.UtcNow);
        Assert.Equal("An older six word", RemoteControl.TitleOf(f, store)); // one stored before the cap, cut
        File.AppendAllLines(f, [Custom("Session auto-rename and PowerShell tab naming")]);
        Assert.Equal("Session auto-rename and PowerShell tab naming", RemoteControl.TitleOf(f, store));
    }

    [Fact]
    public void TheStoreRoundTripsAndForgetsOldSessions()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "sub", "titles.json");
        var now = DateTime.UtcNow;
        var s = new SessionTitles();
        s.Record("old", "Old work", "b0", now.AddDays(-61));
        s.Record("s1", "First", "b1", now);
        s.Record("s1", "Second", "b2", now);
        s.Save(now, path);
        var back = SessionTitles.Load(path);
        Assert.False(back.Sessions.ContainsKey("old"));
        Assert.Equal("Second", back.Current("s1"));
        Assert.Equal(["First", "Second"], back.Sessions["s1"].Set);
        Assert.Equal("b2", back.Sessions["s1"].Basis);
        File.WriteAllText(path, "{not json");
        Assert.Empty(SessionTitles.Load(path).Sessions);
        Assert.Null(new SessionTitles().Current("s1"));
    }

    [Fact]
    public void TheTitleRequestIsHaikuWithNothingElse()
    {
        var args = Engine.TitleArgs(3);
        Assert.Equal(["run", "3", "--", "-p"], args[..4]);
        Assert.Contains("haiku", args);
        Assert.Equal("", args[^1]); // --tools ""
        Assert.Contains("--no-session-persistence", args);
    }
}
