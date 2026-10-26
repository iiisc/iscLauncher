using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace iscLauncher.Services;

public record DeviceSignIn(string UserCode, string VerificationUri, string DeviceCode, int IntervalSeconds, DateTime ExpiresAt);

/// <param name="Id">The release's tag, or the commit's SHA.</param>
/// <param name="Release">The release's tag, or null when the newest version is a commit.</param>
/// <param name="Url">Where to download it.</param>
/// <param name="Accept">The content type to ask for when downloading, if GitHub needs one.</param>
public record GitHubVersion(string Id, string? Release, string Url, string? Accept);

/// <summary>
/// Signs in to GitHub with the device flow, so the user approves the app on github.com and no password passes through
/// it. The token is kept in Windows Credential Manager.
/// </summary>
public class GitHubService
{
    // Identifies iscLauncher's GitHub App, which can only read and write the files of the repos each user gives it.
    // It's public by design: the device flow has no client secret.
    private const string ClientId = "Iv23libPQJqBpdQ3TEFK";
    private const string AppSlug = "isclauncher";
    private const string TokenTarget = "iscLauncher_github";

    public const string TurnedDown = "GitHub turned down this PC's sign-in";
    public const string Outdated = "iscLauncher now asks GitHub only for the repos you choose";

    /// <summary>Where the user installs iscLauncher on their account, or changes which repos it can use.</summary>
    public static string InstallUrl => $"https://github.com/apps/{AppSlug}/installations/new";

    private static readonly HttpClient Http = CreateClient();

    private readonly CredentialService _credentials;

    public GitHubService(CredentialService credentials)
    {
        _credentials = credentials;
        // Sign-ins from before the GitHub App could reach every repo on the account, so they're dropped
        if (Token?.StartsWith("gho_", StringComparison.Ordinal) == true)
        {
            _credentials.DeleteCredential(TokenTarget);
            SignInProblem = Outdated;
        }
    }

    public string? Token => _credentials.GetCredential(TokenTarget);

    /// <summary>
    /// Why the user needs to sign in again, or null: <see cref="TurnedDown"/> when GitHub refused the saved sign-in
    /// (revoked, or pushed out by newer ones, since GitHub keeps only the latest ten per app), or <see cref="Outdated"/>.
    /// Sync can't work until they do.
    /// </summary>
    public string? SignInProblem
    {
        get => _signInProblem;
        private set
        {
            if (_signInProblem == value) return;
            _signInProblem = value;
            SignInProblemChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private string? _signInProblem;

    public event EventHandler? SignInProblemChanged;

    public async Task<DeviceSignIn> StartSignInAsync(CancellationToken ct)
    {
        // A GitHub App's permissions are set on GitHub, so there's no scope to ask for
        var json = await PostFormAsync("https://github.com/login/device/code", new() { ["client_id"] = ClientId }, ct);
        return new DeviceSignIn(
            json.GetProperty("user_code").GetString()!,
            json.GetProperty("verification_uri").GetString()!,
            json.GetProperty("device_code").GetString()!,
            json.GetProperty("interval").GetInt32(),
            DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32()));
    }

    /// <summary>
    /// Waits for the user to approve the code on github.com, then stores the token. Returns the GitHub username, or
    /// null if the code expired first.
    /// </summary>
    public async Task<string?> CompleteSignInAsync(DeviceSignIn signIn, CancellationToken ct)
    {
        var interval = signIn.IntervalSeconds;
        while (DateTime.UtcNow < signIn.ExpiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var json = await PostFormAsync("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = ClientId,
                ["device_code"] = signIn.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }, ct);

            if (json.TryGetProperty("access_token", out var token))
            {
                _credentials.SaveCredential(TokenTarget, token.GetString()!);
                SignInProblem = null;
                return await GetLoginAsync(ct) ?? "GitHub";
            }

            switch (json.GetProperty("error").GetString())
            {
                case "authorization_pending":
                    break;
                // GitHub asks for a longer wait when it's polled too often
                case "slow_down":
                    interval += 5;
                    break;
                case "access_denied":
                    throw new AppException("Sign-in was cancelled on GitHub.");
                case "expired_token":
                    return null;
                default:
                    throw new AppException($"GitHub refused the sign-in ({json.GetProperty("error").GetString()}).");
            }
        }
        return null;
    }

    /// <summary>The signed-in GitHub username, or null if not signed in or the sign-in no longer works.</summary>
    public async Task<string?> GetLoginAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "https://api.github.com/user", null, ct);
        if (response?.IsSuccessStatusCode != true)
            return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("login").GetString();
    }

    public void SignOut()
    {
        _credentials.DeleteCredential(TokenTarget);
        SignInProblem = null;
    }

    /// <summary>
    /// The private repos the user has given iscLauncher on GitHub, as name ("owner/repo") and clone URL, or null if they
    /// haven't installed it on their account yet. Public repos are left out: settings can include account and character names.
    /// </summary>
    public async Task<List<(string Name, string CloneUrl)>?> ListSyncReposAsync(CancellationToken ct)
    {
        var installations = await GetJsonAsync("https://api.github.com/user/installations", ct);
        var ids = installations.GetProperty("installations").EnumerateArray().Select(i => i.GetProperty("id").GetInt64()).ToList();
        if (ids.Count == 0)
            return null;

        var repos = new List<(string, string)>();
        foreach (var id in ids)
        {
            var json = await GetJsonAsync($"https://api.github.com/user/installations/{id}/repositories?per_page=100", ct);
            repos.AddRange(json.GetProperty("repositories").EnumerateArray()
                .Where(r => r.GetProperty("private").GetBoolean())
                .Select(r => (r.GetProperty("full_name").GetString()!, r.GetProperty("clone_url").GetString()!)));
        }
        return repos;
    }

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, ct) ?? throw new AppException("Sign in with GitHub first.");
        if (!response.IsSuccessStatusCode)
            throw new AppException($"GitHub couldn't list iscLauncher's repos ({(int)response.StatusCode} {response.ReasonPhrase}).");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// What a repo's newest version is: its newest release with a zip, or else its newest commit, since some addons only
    /// ever publish code. Costs a request or two and downloads nothing.
    /// </summary>
    public async Task<GitHubVersion> GetLatestAsync(string repo, CancellationToken ct)
    {
        // Not found means no releases, or no such repo, which the commit lookup then reports
        using (var release = await GetAsync($"https://api.github.com/repos/{repo}/releases/latest", null, ct))
        {
            if (release.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await release.Content.ReadAsStringAsync(ct));
                var zip = doc.RootElement.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(a => a.GetProperty("name").GetString()!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (zip.ValueKind == JsonValueKind.Object)
                {
                    var tag = doc.RootElement.GetProperty("tag_name").GetString()!;
                    // GitHub hands out a release file only when asked for bytes
                    return new GitHubVersion(tag, tag, zip.GetProperty("url").GetString()!, "application/octet-stream");
                }
            }
            else if (release.StatusCode != HttpStatusCode.NotFound)
                throw Refused(release);
        }

        using var commit = await GetAsync($"https://api.github.com/repos/{repo}/commits/HEAD", "application/vnd.github.sha", ct);
        if (!commit.IsSuccessStatusCode)
            throw Refused(commit);
        var sha = (await commit.Content.ReadAsStringAsync(ct)).Trim();
        // Pinned to the commit, so the download is exactly the version that was looked up
        return new GitHubVersion(sha, null, $"https://api.github.com/repos/{repo}/zipball/{sha}", null);
    }

    public async Task DownloadAsync(GitHubVersion version, string zipPath, CancellationToken ct)
    {
        using var response = await GetAsync(version.Url, version.Accept, ct);
        if (!response.IsSuccessStatusCode)
            throw Refused(response);
        await using var file = File.Create(zipPath);
        await response.Content.CopyToAsync(file, ct);
    }

    /// <summary>Repos matching a search, best match first, as name ("owner/repo"), description and stars.</summary>
    public async Task<List<(string Repo, string Description, int Stars)>> SearchReposAsync(string query, CancellationToken ct)
    {
        using var response = await GetAsync($"https://api.github.com/search/repositories?per_page=5&q={Uri.EscapeDataString(query)}", null, ct);
        if (!response.IsSuccessStatusCode)
            throw Refused(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(r => (r.GetProperty("full_name").GetString()!, r.GetProperty("description").GetString() ?? "", r.GetProperty("stargazers_count").GetInt32()))
            .ToList();
    }

    private static AppException Refused(HttpResponseMessage response) => new(response.StatusCode switch
    {
        HttpStatusCode.NotFound => "not found on GitHub",
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
            "GitHub limits how often it's asked without a working sign-in. Sign in under Options, or try again in an hour",
        _ => $"GitHub refused ({(int)response.StatusCode} {response.ReasonPhrase})"
    });

    // Public repos work signed out too, but signed in, GitHub allows far more requests an hour. A turned-down sign-in
    // gets 401 even for a public repo, and the app's sign-in may not reach a public repo the user didn't give it, so
    // both are asked again without it. A turned-down one isn't sent again after that.
    private async Task<HttpResponseMessage> GetAsync(string url, string? accept, CancellationToken ct, bool signedIn = true)
    {
        var token = signedIn && SignInProblem == null ? Token : null;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept != null)
            request.Headers.Accept.ParseAdd(accept);
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (token == null || !(IsTurnedDown(response) || response.StatusCode == HttpStatusCode.NotFound))
            return response;
        response.Dispose();
        return await GetAsync(url, accept, ct, signedIn: false);
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        if (Token is not { } token)
            return null;
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request, ct);
        IsTurnedDown(response);
        return response;
    }

    private bool IsTurnedDown(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return false;
        SignInProblem = TurnedDown;
        return true;
    }

    private static async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // GitHub explains a refusal in error_description, e.g. when the app's device flow is switched off
            string? reason = null;
            try { reason = JsonDocument.Parse(body).RootElement.GetProperty("error_description").GetString(); } catch { }
            throw new AppException($"GitHub refused the sign-in: {reason ?? $"{(int)response.StatusCode} {response.ReasonPhrase}"}.");
        }
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("iscLauncher");
        // github.com's sign-in endpoints answer with form data unless JSON is asked for
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}
