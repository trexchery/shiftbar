using System.Text.Json;

namespace Shiftbar;

/// JSON lines are private stdio between the native menu and the shared account upkeep.
/// Requests run concurrently, so a long one (a prime) never blocks the input reader.
internal sealed class BackendHost
{
    readonly Engine engine = Engine.Locate();
    readonly DockSettings settings = DockSettings.Load();
    readonly CancellationTokenSource stopping = new();
    readonly List<Task> requests = [];
    bool refreshing;
    Snapshot? accounts;
    BackendUpkeep upkeep = null!;

    public async Task RunAsync()
    {
        upkeep = new BackendUpkeep(engine, settings, () => accounts,
            (title, body) => Write(new { @event = "notify", title, body }), async () => { await RefreshAsync(); });
        Write(new { @event = "ready", schemaVersion = 1 });
        var poll = PollAsync();
        var maintenance = MaintainAsync();
        // Console.In is a synchronized TextReader whose async methods may block
        // synchronously. Read the actual pipe so request continuations keep
        // running while stdin is idle.
        using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(stopping.Token);
                if (line == null) break;
                requests.RemoveAll(t => t.IsCompleted);
                requests.Add(DispatchAsync(line));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            stopping.Cancel();
            upkeep.Dispose();
            try { await poll; } catch (OperationCanceledException) { }
            try { await maintenance; } catch (OperationCanceledException) { }
        }
    }

    async Task DispatchAsync(string line)
    {
        JsonElement? id = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var value)) id = value.Clone();
            var method = root.GetProperty("method").GetString();
            var args = root.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            object? result = method switch
            {
                "status" => Status(),
                "refresh" => await RefreshAsync(),
                "settings" => SaveSettings(args),
                "prime" => await upkeep.PrimeNowAsync(),
                "browserProfiles" => BrowserInfo(settings, accounts),
                "quit" => Quit(),
                _ => throw new EngineException("Unknown backend method: " + method),
            };
            Write(new { id, result });
        }
        catch (Exception e)
        {
            Program.Log(e);
            Write(new { id, error = new { message = Engine.Redact(e.Message) } });
        }
    }

    object Status() => new { schemaVersion = 1, config = settings, accounts, primeStatus = upkeep.PrimeStatus() };

    object SaveSettings(JsonElement args)
    {
        Apply(settings, args.TryGetProperty("values", out var v) ? v : args);
        settings.Save();
        upkeep.SettingsChanged();
        return settings;
    }

    internal static void Apply(DockSettings settings, JsonElement values)
    {
        foreach (var property in values.EnumerateObject())
        {
            switch (property.Name)
            {
                case "preserveRemoteControl": settings.PreserveRemoteControl = property.Value.GetBoolean(); break;
                case "quotaNotifications": settings.QuotaNotifications = property.Value.GetBoolean(); break;
                case "autoSwitch": settings.AutoSwitch = property.Value.GetBoolean(); break;
                case "autoPrime": settings.AutoPrime = property.Value.GetBoolean(); break;
                case "autoProjectAccount": settings.AutoProjectAccount = property.Value.GetBoolean(); break;
                case "terminalFolder": settings.TerminalFolder = property.Value.ValueKind == JsonValueKind.Null ? null : Path.GetFullPath(property.Value.GetString()!); break;
                case "refreshSeconds": settings.RefreshSeconds = Math.Clamp(property.Value.GetInt32(), 60, 3600); break;
                case "browserFollowsAccount": settings.BrowserFollowsAccount = property.Value.GetBoolean(); break;
                case "closeOldBrowserWindows": settings.CloseOldBrowserWindows = property.Value.GetBoolean(); break;
                case "browser":
                    settings.Browser = BrowserProfiles.Find(property.Value.GetString())?.Id ?? throw new EngineException("Unknown browser: " + property.Value.GetString());
                    break;
                case "browserProfile": // {email, dir}; a null dir goes back to matching by email
                    var email = property.Value.GetProperty("email").GetString();
                    if (string.IsNullOrWhiteSpace(email)) throw new EngineException("browserProfile needs an email");
                    // A new dictionary, never edited in place: the browser step reads it on the thread pool.
                    var picks = new Dictionary<string, string>(settings.BrowserProfiles, StringComparer.OrdinalIgnoreCase);
                    if (property.Value.TryGetProperty("dir", out var dir) && dir.GetString() is { Length: > 0 } d) picks[email] = d;
                    else picks.Remove(email);
                    settings.BrowserProfiles = picks;
                    break;
                default: throw new EngineException("Unsupported desktop setting: " + property.Name);
            }
        }
    }

    /// The menu's browser choices: installed browsers, the chosen one's profiles, and which profile each account resolves to.
    internal static object BrowserInfo(DockSettings settings, Snapshot? accounts,
        Func<string, (IReadOnlyList<BrowserProfile> Profiles, IReadOnlySet<string> LastActive)>? readState = null)
    {
        var (profiles, _) = (readState ?? BrowserProfiles.ReadState)(settings.Browser);
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts?.Accounts ?? [])
            resolved[account.Email] = BrowserProfiles.Resolve(account.Email, settings.BrowserProfiles, profiles)?.Dir;
        return new
        {
            browser = settings.Browser,
            installed = BrowserProfiles.Installed().Select(b => new { id = b.Id, name = b.Name }).ToList(),
            profiles = profiles.Select(p => new { dir = p.Dir, name = p.Name, userName = p.UserName }).ToList(),
            resolved,
        };
    }

    async Task<bool> RefreshAsync()
    {
        if (refreshing) return false;
        refreshing = true;
        try
        {
            upkeep.ApplyEngineConfig(await engine.ConfigAsync());
            accounts = await engine.ListAsync();
            engine.StatsSyncTick(Program.Log);
            return true;
        }
        catch (Exception e)
        {
            Program.Log(e);
            Write(new { @event = "notify", title = "Account refresh failed", body = Engine.Redact(e.Message) });
            return false;
        }
        finally { refreshing = false; }
    }

    async Task PollAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            await RefreshAsync();
            await Task.Delay(TimeSpan.FromSeconds(settings.RefreshSeconds), stopping.Token);
        }
    }

    async Task MaintainAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try { await upkeep.TickAsync(); }
            catch (Exception e) { Program.Log(e); }
            await Task.Delay(TimeSpan.FromSeconds(3), stopping.Token);
        }
    }

    object Quit() { stopping.Cancel(); return new { quitting = true }; }
    static void Write(object value) => Console.WriteLine(Engine.Redact(JsonSerializer.Serialize(value, Json.Options)));
}
