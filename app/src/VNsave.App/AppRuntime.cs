using System.Diagnostics;
using VNsave.Core;

namespace VNsave.App;

public sealed class AppRuntime : IDisposable
{
    public LocalRepository Repository { get; }
    public KnownFolders Known { get; }
    public GameDetector Detector { get; }
    public IGameProcess Process { get; }
    public SecretRedactor Redactor { get; } = new();
    public ActivityLog Log { get; }
    public string? AccountName { get; private set; }
    public Dictionary<string, string> Errors { get; } = [];
    public CloudCatalog Catalog { get; private set; } = null!;
    public SnapshotStore Snapshots { get; private set; } = null!;
    public PlayTimeLog PlayTime { get; private set; } = null!;
    private readonly ICredentialStore credentials;
    private readonly HttpClient http;
    private readonly string defaults;
    private AppSettings settings = new();
    private TokenProvider tokens = null!;
    private DropboxClient client = null!;
    private FileStream? appLock;
    // defaultConfig is the JSON copied to settings.json under root on the first run.
    public AppRuntime(string root, string defaultConfig, KnownFolders? known = null, ICredentialStore? credentials = null,
        HttpClient? http = null, IGameProcess? process = null)
    {
        Repository = new(root);
        defaults = defaultConfig;
        Known = known ?? KnownFolders.Windows();
        Detector = new(Known);
        this.credentials = credentials ?? new WindowsCredentialStore();
        this.http = http ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        Process = process ?? new GameProcess();
        Log = new(root, Redactor);
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        appLock = Repository.AcquireLock();
        string config = Path.Combine(Repository.Root, "settings.json");
        if (!File.Exists(config)) await JsonFormat.SaveAtomicAsync(config, AppSettings.Parse(defaults), ct);
        settings = AppSettings.Load(config);
        await Repository.LoadAsync(ct);
        ResetConnection();
        if (credentials.Read() is not null)
        {
            await CheckConnectionAsync(ct);
            foreach (var game in Repository.Library.Games)
            {
                if (!File.Exists(game.ExecutablePath) || Process.IsRunning(game.ExecutablePath)) continue;
                try { await Service().RetryPendingAsync(game, ct); }
                catch (Exception error) { Errors[game.Definition.GameId] = Redactor.FormatError(error); Log.Write(Errors[game.Definition.GameId]); }
            }
            await Catalog.RefreshAsync(ct);
        }
    }
    private void ResetConnection()
    {
        tokens = new(http, settings, credentials, Redactor);
        client = new(http, tokens, Redactor);
        Catalog = new(client);
        Snapshots = new(client, Repository, settings.AppKey + ":" + Repository.Library.AccountId);
        PlayTime = new(client, Repository);
    }
    public async Task ConnectAsync(CancellationToken ct)
    {
        using var receiver = OAuthLoopback.Start(settings.RedirectHost, settings.RedirectPorts);
        string verifier = Pkce.GenerateVerifier();
        string state = Pkce.GenerateState();
        Redactor.Register(state);
        Redactor.Register(verifier);
        var uri = Pkce.AuthorizationUri(settings, receiver.RedirectUri, state, Pkce.Challenge(verifier));
        System.Diagnostics.Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        string code = await receiver.WaitForCodeAsync(state, ct);
        ResetConnection();
        await tokens.ExchangeCodeAsync(code, verifier, receiver.RedirectUri, ct);
        await CheckConnectionAsync(ct);
        foreach (var game in Repository.Library.Games)
            if (File.Exists(game.ExecutablePath) && !Process.IsRunning(game.ExecutablePath)) await Service().RetryPendingAsync(game, ct);
        await Catalog.RefreshAsync(ct);
        Log.Write("Dropbox に接続しました。");
    }
    public bool SignedIn => credentials.Read() is not null;
    // Set when the last check could not reach or use the account, so the header can tell that apart from never connecting.
    public bool ConnectionFailed { get; private set; }
    public void MarkConnectionFailed() => ConnectionFailed = true;
    public async Task CheckConnectionAsync(CancellationToken ct)
    {
        if (credentials.Read() is null) throw new ConnectionUnavailableException();
        try
        {
            var account = await client.GetCurrentAccountAsync(ct);
            string id = account.GetProperty("account_id").GetString()!;
            if (Repository.Library.AccountId is { } previous && previous != id) throw new UserError("登録済みのゲームとは別の Dropbox アカウントです。元のアカウントで接続してください。");
            Repository.Library.AccountId = id;
            await Repository.SaveAsync(ct);
            AccountName = account.GetProperty("name").GetProperty("display_name").GetString();
            Snapshots = new(client, Repository, settings.AppKey + ":" + id);
            ConnectionFailed = false;
        }
        catch (Exception error) when (error is not OperationCanceledException) { ConnectionFailed = true; throw; }
    }
    public SyncService Service()
    {
        if (Repository.Library.AccountId is null) throw new UserError("先に Dropbox へ接続してください。");
        return new(client, Repository, Catalog, Snapshots, Process, Log, CheckConnectionAsync, PlayTime);
    }
    public async Task DisconnectAsync(CancellationToken ct)
    {
        try { if (credentials.Read() is not null) await client.RevokeAsync(ct); }
        finally { credentials.Delete(); AccountName = null; ConnectionFailed = false; ResetConnection(); }
        Log.Write("Dropbox の接続を解除しました。");
    }
    public async Task RegisterAsync(LocalGame game, bool newCloudGame, CancellationToken ct)
    {
        GameDetector.ValidateExecutable(game.ExecutablePath);
        SaveLocationValidator.Validate(game.Locations, Repository.Library.Games);
        if (Repository.Library.Games.Any(g => g.Definition.GameId == game.Definition.GameId)) throw new UserError("このゲームは登録済みです。");
        var duplicate = Repository.Library.Games.FirstOrDefault(g =>
            g.Definition.ExecutableName.Equals(game.Definition.ExecutableName, StringComparison.OrdinalIgnoreCase) &&
            g.Definition.ProductName.Equals(game.Definition.ProductName, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null) throw new UserError("同じゲームが登録されています。登録先: " + duplicate.ExecutablePath);
        await CheckConnectionAsync(ct);
        if (newCloudGame) await Catalog.RegisterAsync(game.Definition, ct);
        Repository.Library.Games.Add(game);
        try { await Repository.SaveAsync(ct); }
        catch { Repository.Library.Games.Remove(game); throw; }
        await Catalog.RefreshAsync(ct);
        if (!newCloudGame) await Service().BeforeLaunchAsync(game, ct);
        Log.Write(game.Definition.DisplayName + ": この PC に登録しました。");
    }
    public void Dispose()
    {
        appLock?.Dispose();
        http.Dispose();
    }
}
