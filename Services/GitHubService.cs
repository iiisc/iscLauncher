using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// it, and creates the private repos used for sync. The token is kept in Windows Credential Manager.
/// </summary>
public class GitHubService
{
    // Identifies iscLauncher's GitHub OAuth app. It's public by design: the device flow has no client secret.
    private const string ClientId = "Ov23ct29hdcgr6z30b5s";
    private const string TokenTarget = "iscLauncher_github";

    private static readonly HttpClient Http = CreateClient();

    private readonly CredentialService _credentials;

    public GitHubService(CredentialService credentials) => _credentials = credentials;

    public string? Token => _credentials.GetCredential(TokenTarget);

    /// <summary>
    /// GitHub turned down the saved sign-in: it was revoked, or pushed out by newer sign-ins, since GitHub keeps only
    /// the latest ten per app. Sync can't work until the user signs in again.
    /// </summary>
    public bool SignInTurnedDown
    {
        get => _signInTurnedDown;
        private set
        {
            if (_signInTurnedDown == value) return;
            _signInTurnedDown = value;
            SignInTurnedDownChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private bool _signInTurnedDown;

    public event EventHandler? SignInTurnedDownChanged;

    public async Task<DeviceSignIn> StartSignInAsync(CancellationToken ct)
    {
        // "repo" is GitHub's only scope that covers private repos
        var json = await PostFormAsync("https://github.com/login/device/code",
            new() { ["client_id"] = ClientId, ["scope"] = "repo" }, ct);
        return new DeviceSignIn(
            json.GetProperty("user_code").GetString()!,
            json.GetProperty("verification_uri").GetString()!,
            json.GetProperty("device_code").GetString()!,
            json.GetProperty("interval").GetInt32(),
            DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32()));
    }

    /// <summary>Waits for the user to approve the code on github.com, then stores the token. Returns the GitHub username.</summary>
    public async Task<string> CompleteSignInAsync(DeviceSignIn signIn, CancellationToken ct)
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
                SignInTurnedDown = false;
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
                default:
                    throw new AppException("The sign-in code expired. Try again.");
            }
        }
        throw new AppException("The sign-in code expired. Try again.");
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
        SignInTurnedDown = false;
    }

    /// <summary>The user's own private repos, most recently updated first, as name and clone URL.</summary>
    public async Task<List<(string Name, string CloneUrl)>> ListPrivateReposAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get,
            "https://api.github.com/user/repos?visibility=private&affiliation=owner&sort=updated&per_page=100", null, ct);
        if (response?.IsSuccessStatusCode != true)
            return [];
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.EnumerateArray()
            .Select(repo => (repo.GetProperty("name").GetString()!, repo.GetProperty("clone_url").GetString()!))
            .ToList();
    }

    /// <summary>
    /// Creates a private repo, or reuses the user's private repo of that name, such as the one this game already syncs
    /// with on another PC. Returns its clone URL.
    /// </summary>
    public async Task<string> CreateRepoAsync(string name, CancellationToken ct)
    {
        var content = JsonContent.Create(new { name, @private = true, description = "WoW addons and settings, synced by iscLauncher" });
        using var created = await SendAsync(HttpMethod.Post, "https://api.github.com/user/repos", content, ct)
            ?? throw new AppException("Sign in with GitHub first.");
        if (created.IsSuccessStatusCode)
            return await CloneUrlAsync(created, ct);

        // 422 means the name is taken; an existing private repo of the user's is the one to sync with
        if ((int)created.StatusCode == 422 && await GetLoginAsync(ct) is { } login)
        {
            using var existing = await SendAsync(HttpMethod.Get, $"https://api.github.com/repos/{login}/{Uri.EscapeDataString(name)}", null, ct);
            if (existing?.IsSuccessStatusCode == true)
            {
                using var doc = JsonDocument.Parse(await existing.Content.ReadAsStringAsync(ct));
                // Settings can include account and character names, so they never go to a public repo
                if (!doc.RootElement.GetProperty("private").GetBoolean())
                    throw new AppException($"You already have a public repo named {name}. Choose another name.");
                return doc.RootElement.GetProperty("clone_url").GetString()!;
            }
        }
        throw new AppException($"GitHub couldn't create the repo ({(int)created.StatusCode} {created.ReasonPhrase}).");
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
    // gets 401 even for a public repo, so that's asked again without it, and it isn't sent again after that.
    private async Task<HttpResponseMessage> GetAsync(string url, string? accept, CancellationToken ct, bool signedIn = true)
    {
        var token = signedIn && !SignInTurnedDown ? Token : null;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept != null)
            request.Headers.Accept.ParseAdd(accept);
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (token == null || !IsTurnedDown(response))
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
        SignInTurnedDown = true;
        return true;
    }

    private static async Task<string> CloneUrlAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("clone_url").GetString()!;
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
