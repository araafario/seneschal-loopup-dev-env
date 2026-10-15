using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace DevEnvironment.Token;

/// <summary>A LoopUp environment a token can be requested from: the identity server and the OneUI address it redirects back to.</summary>
public sealed record TokenEnvironment(string Id, string Name, string Authority, string AppUrl, string DefaultEmail = "", string DefaultPassword = "")
{
    public const string ClientId = "oneui";

    public string RedirectUri => $"{AppUrl}/oidc-callback";

    public static readonly IReadOnlyList<TokenEnvironment> All =
    [
        new("local", "Local", "https://identity.loopup.dev", "https://one.loopup.dev", "admin@loopup.co", "password"),
        new("lab", "Lab", "https://identity.lab.loopup.com", "https://one.lab.loopup.com"),
        new("production", "Production", "https://identity.loopup.com", "https://one.loopup.com"),
    ];

    public static TokenEnvironment Find(string? id) =>
        All.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

public sealed record TokenResult(string AccessToken, string? RefreshToken, string? IdToken, string TokenType, int ExpiresInSeconds, string Scope, DateTimeOffset IssuedAt)
{
    public DateTimeOffset ExpiresAt => IssuedAt.AddSeconds(ExpiresInSeconds);

    public string AuthorizationHeader => $"{TokenType} {AccessToken}";
}

/// <summary>Thrown with a message that is fit to show as it is.</summary>
public sealed class TokenException(string message) : Exception(message);

/// <summary>
/// Signs in the way OneUI does and returns the bearer token. OneUI uses the authorization code flow with PKCE:
/// 1. GET /connect/authorize sends the browser to the identity login page (302 to /login?returnUrl=...)
/// 2. the login page posts the email and password to /login-password, which sets the session cookie and answers with the address to continue at
/// 3. that address (/connect/authorize/callback) answers 302 to the app's /oidc-callback?code=...
/// 4. POST /connect/token trades the code and the PKCE verifier for the tokens
/// All of it is done here with one cookie jar, so no browser is needed.
/// </summary>
public sealed class TokenClient
{
    private const string Scope = "openid profile offline_access";

    public async Task<TokenResult> GetAsync(TokenEnvironment environment, string email, string password, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(email)) throw new TokenException("Enter the email");
        if (string.IsNullOrEmpty(password)) throw new TokenException("Enter the password");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var authority = new Uri(environment.Authority);

        try
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

            // 1. Ask for a code, which sends us to the login page
            var authorize = $"{environment.Authority}/connect/authorize?client_id={TokenEnvironment.ClientId}"
                + $"&redirect_uri={Uri.EscapeDataString(environment.RedirectUri)}&response_type=code&scope={Uri.EscapeDataString(Scope)}"
                + $"&code_challenge={challenge}&code_challenge_method=S256";
            using var first = await http.GetAsync(authorize, cancel);
            var login = first.Headers.Location ?? throw new TokenException($"{environment.Name} did not send us to a login page ({(int)first.StatusCode})");
            var returnUrl = HttpUtility.ParseQueryString(Absolute(authority, login).Query)["returnUrl"]
                ?? throw new TokenException("The login page did not say where to continue");

            // 2. Sign in the way the login page does
            using var signIn = new HttpRequestMessage(HttpMethod.Post, $"{environment.Authority}/login-password")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { email = email.Trim(), password, returnUrl, rememberMe = false }),
                    Encoding.UTF8, "application/json"),
            };
            signIn.Headers.Add("X-Requested-With", "XMLHttpRequest");
            using var signedIn = await http.SendAsync(signIn, cancel);
            var next = (await signedIn.Content.ReadAsStringAsync(cancel)).Trim().Trim('"');
            if (signedIn.StatusCode == HttpStatusCode.Unauthorized)
                throw new TokenException(next == "RevokedLogin" ? "This login has been revoked" : "The email or password is wrong");
            if (!signedIn.IsSuccessStatusCode || next.Length == 0)
                throw new TokenException($"Sign in failed ({(int)signedIn.StatusCode})");

            // 3. Follow the answer to the redirect that carries the code
            using var callback = await http.GetAsync(Absolute(authority, new Uri(next, UriKind.RelativeOrAbsolute)), cancel);
            var back = callback.Headers.Location ?? throw new TokenException($"No code was returned ({(int)callback.StatusCode})");
            var query = HttpUtility.ParseQueryString(Absolute(authority, back).Query);
            var code = query["code"] ?? throw new TokenException(query["error_description"] ?? query["error"] ?? "No code was returned");

            // 4. Trade the code for the tokens
            using var exchange = await http.PostAsync($"{environment.Authority}/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = environment.RedirectUri,
                ["client_id"] = TokenEnvironment.ClientId,
                ["code_verifier"] = verifier,
            }), cancel);
            var json = await exchange.Content.ReadAsStringAsync(cancel);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!exchange.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var accessToken))
                throw new TokenException(root.TryGetProperty("error_description", out var why) ? why.GetString()! : root.TryGetProperty("error", out var error) ? $"The token request failed: {error.GetString()}" : $"The token request failed ({(int)exchange.StatusCode})");

            return new TokenResult(
                accessToken.GetString()!,
                Text(root, "refresh_token"),
                Text(root, "id_token"),
                Text(root, "token_type") ?? "Bearer",
                root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600,
                Text(root, "scope") ?? Scope,
                DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            throw new TokenException($"Could not reach {authority.Host}: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TokenException($"{authority.Host} did not answer in time");
        }
        catch (JsonException)
        {
            throw new TokenException("The identity server answered with something that is not a token");
        }
    }

    /// <summary>The claims of a token, for showing who it is for and what it allows. The signature is not checked, it is only read.</summary>
    public static IReadOnlyDictionary<string, string> Claims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return new Dictionary<string, string>();
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(padded));
            return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()!,
                JsonValueKind.Array => string.Join(", ", p.Value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())),
                _ => p.Value.ToString(),
            });
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri Absolute(Uri authority, Uri location) => location.IsAbsoluteUri ? location : new Uri(authority, location);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
