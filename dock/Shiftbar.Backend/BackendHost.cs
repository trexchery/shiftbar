using System.Text.Json;

namespace PatchGoblins;

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
        var values = args.TryGetProperty("values", out var v) ? v : args;
        foreach (var property in values.EnumerateObject())
        {
            switch (property.Name)
            {
                case "preserveRemoteControl": settings.PreserveRemoteControl = property.Value.GetBoolean(); break;
                case "quotaNotifications": settings.QuotaNotifications = property.Value.GetBoolean(); break;
                case "autoSwitch": settings.AutoSwitch = property.Value.GetBoolean(); break;
                case "autoPrime": settings.AutoPrime = property.Value.GetBoolean(); break;
                case "autoLine": settings.AutoLine = property.Value.GetBoolean(); break;
                case "autoProjectAccount": settings.AutoProjectAccount = property.Value.GetBoolean(); break;
                case "terminalFolder": settings.TerminalFolder = property.Value.ValueKind == JsonValueKind.Null ? null : Path.GetFullPath(property.Value.GetString()!); break;
                case "refreshSeconds": settings.RefreshSeconds = Math.Clamp(property.Value.GetInt32(), 60, 3600); break;
                default: throw new EngineException("Unsupported desktop setting: " + property.Name);
            }
        }
        settings.Save();
        upkeep.SettingsChanged();
        return settings;
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
