using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HiloScheduler.Hilo;

public record HiloEvent(DateTimeOffset Start);

public class HiloClient(HttpClient http, IOptions<SchedulerOptions> options, ILogger<HiloClient> logger)
{
    private const string AuthBase        = "https://connexion.hiloenergie.com/HiloDirectoryB2C.onmicrosoft.com/B2C_1A_Sign_In";
    private const string ApiBase         = "https://api.hiloenergie.com/Automation/v1/api";
    private const string ChallengeBase   = "https://api.hiloenergie.com/challenge/v1/api";
    private const string ClientId        = "1ca9f585-4a55-4085-8e30-9746a65fa561";
    private const string Scope           = "openid https://HiloDirectoryB2C.onmicrosoft.com/hiloapis/user_impersonation offline_access";
    private const string RedirectUri     = "https://my.home-assistant.io/redirect/oauth/";
    private const string SubscriptionKey = "20eeaedcb86945afa3fe792cea89b8bf";

    private readonly HttpClient        _http = http;
    private readonly SchedulerOptions  _opts = options.Value;

    // -----------------------------------------------------------------------
    // Interactive first-time login (run with --login)
    // -----------------------------------------------------------------------

    public async Task InteractiveLoginAsync(CancellationToken ct = default)
    {
        var (verifier, challenge) = PkceUtils.GeneratePair();
        var state = PkceUtils.GenerateState();

        var url = $"{AuthBase}/oauth2/v2.0/authorize" +
            $"?client_id={ClientId}" +
            "&response_type=code" +
            $"&scope={Uri.EscapeDataString(Scope)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&state={state}" +
            $"&code_challenge={challenge}" +
            "&code_challenge_method=S256";

        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("Hilo login required. Opening your browser...");
        Console.WriteLine("Log in, then copy the FULL URL from the browser address bar.");
        Console.WriteLine("(The URL will contain 'redirect/_change' or 'redirect/oauth')");
        Console.WriteLine(new string('=', 60));

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

        Console.Write("\nPaste the full browser URL here: ");
        var callbackUrl = Console.ReadLine()?.Trim() ?? "";

        var code = ExtractCode(callbackUrl)
            ?? throw new InvalidOperationException("No 'code' found in the URL. Copy the full address bar URL after login.");

        var tokens = await ExchangeCodeAsync(code, verifier, ct);
        SaveTokens(tokens);
        logger.LogInformation("Hilo login successful. Tokens saved to {File}.", _opts.ResolvePath(_opts.TokenFile));
    }

    // -----------------------------------------------------------------------
    // Token management
    // -----------------------------------------------------------------------

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Return cached token if it has more than 5 minutes left
        if (_cachedToken is not null && _tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _cachedToken;
        }

        var tokens = LoadTokens();
        if (tokens?.RefreshToken is null)
        {
            throw new InvalidOperationException(
                "No Hilo tokens found. Run the program with --login first.");
        }

        var refreshed = await RefreshAsync(tokens.RefreshToken, ct);
        SaveTokens(refreshed);

        // Hilo tokens expire in 3600s — cache for 50 minutes to be safe
        _cachedToken = refreshed.AccessToken;
        _tokenExpiry = DateTimeOffset.UtcNow.AddMinutes(50);

        logger.LogInformation("Hilo authentication successful (refresh token).");
        return _cachedToken;
    }

    // -----------------------------------------------------------------------
    // API calls
    // -----------------------------------------------------------------------

    public async Task<int> GetLocationIdAsync(string token, CancellationToken ct = default)
    {
        using var req = ApiRequest(HttpMethod.Get, $"{ApiBase}/Locations", token);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var locations = await resp.Content.ReadFromJsonAsync<JsonElement[]>(ct) ?? [];
        if (locations.Length == 0)
        {
            throw new InvalidOperationException("No Hilo locations found on this account.");
        }
        var id = locations[0].GetProperty("id").GetInt32();
        logger.LogInformation("Using Hilo location ID: {Id}", id);
        return id;
    }

    public async Task<HiloEvent?> GetNextEventAsync(string token, int locationId, CancellationToken ct = default)
    {
        using var req = ApiRequest(HttpMethod.Get, $"{ChallengeBase}/Locations/{locationId}/Seasons", token);
        var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var seasons = await resp.Content.ReadFromJsonAsync<JsonElement[]>(ct) ?? [];

        var now      = DateTimeOffset.UtcNow;
        var upcoming = new List<DateTimeOffset>();

        foreach (var season in seasons)
        {
            foreach (var ev in season.GetProperty("events").EnumerateArray())
            {
                if (ev.GetProperty("status").GetString() != "Upcoming")
                {
                    continue;
                }
                var startStr = ev.TryGetProperty("startDateUtc", out var s1) ? s1.GetString()
                             : ev.TryGetProperty("startDateUTC", out var s2) ? s2.GetString()
                             : null;
                if (startStr is null)
                {
                    continue;
                }
                var start = DateTimeOffset.Parse(startStr, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal);
                if (start.AddMinutes(_opts.EventDurationMin) > now)
                {
                    upcoming.Add(start);
                }
            }
        }

        if (upcoming.Count == 0)
        {
            return null;
        }
        upcoming.Sort();
        return new HiloEvent(upcoming[0]);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private HttpRequestMessage ApiRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new("Bearer", token);
        req.Headers.Add("Ocp-Apim-Subscription-Key", SubscriptionKey);
        return req;
    }

    private async Task<TokenData> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var resp = await _http.PostAsync($"{AuthBase}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = ClientId,
                ["code"]          = code,
                ["redirect_uri"]  = RedirectUri,
                ["code_verifier"] = verifier,
            }), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TokenData>(ct)
            ?? throw new InvalidOperationException("Empty token response.");
    }

    private async Task<TokenData> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var resp = await _http.PostAsync($"{AuthBase}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = ClientId,
                ["refresh_token"] = refreshToken,
            }), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TokenData>(ct)
            ?? throw new InvalidOperationException("Empty token response.");
    }

    private TokenData? LoadTokens()
    {
        var path = _opts.ResolvePath(_opts.TokenFile);
        if (!File.Exists(path))
        {
            return null;
        }
        return JsonSerializer.Deserialize<TokenData>(File.ReadAllText(path));
    }

    private void SaveTokens(TokenData tokens) =>
        File.WriteAllText(_opts.ResolvePath(_opts.TokenFile),
            JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));

    private static string? ExtractCode(string callbackUrl)
    {
        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            return null;
        }
        var qs = ParseQueryString(new Uri(callbackUrl).Query);
        if (qs.TryGetValue("code", out var code))
        {
            return code;
        }
        // my.home-assistant.io _change URL: code is inside the 'redirect' param
        if (qs.TryGetValue("redirect", out var redirect))
        {
            var inner = ParseQueryString(new Uri("https://x/" + Uri.UnescapeDataString(redirect)).Query);
            if (inner.TryGetValue("code", out var innerCode))
            {
                return innerCode;
            }
        }
        return null;
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        return query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Contains('='))
            .ToDictionary(
                p => Uri.UnescapeDataString(p[..p.IndexOf('=')]),
                p => Uri.UnescapeDataString(p[(p.IndexOf('=') + 1)..])
            );
    }
}

internal record TokenData(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken
);
