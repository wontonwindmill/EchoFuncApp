// AuthGuest.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

public record GuestRequest(string guestId, string device);
public record TokenResponse(string token, long expiresUnix);

public class AuthGuest
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<AuthGuest> _log;

    public AuthGuest(IConfiguration cfg, ILogger<AuthGuest> log)
    {
        _cfg = cfg;
        _log = log;
    }

    [Function("AuthGuest")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        try
        {
            var input = await req.ReadFromJsonAsync<GuestRequest>();
            if (input is null || string.IsNullOrWhiteSpace(input.guestId))
                return Bad(req, "guestId required.");

            // Read settings at runtime and log minimal diagnostics
            var secret   = _cfg["JWT_SIGNING_SECRET"];
            var issuer   = _cfg["JWT_ISSUER"]    ?? "echo-backend";
            var audience = _cfg["JWT_AUDIENCE"]  ?? "echo-api";
            var minsStr  = _cfg["JWT_MINUTES"]   ?? "30";
            if (string.IsNullOrEmpty(secret))
            {
                _log.LogError("JWT_SIGNING_SECRET missing or empty in App Settings.");
                return ServerError(req, "Server not configured.");
            }
            if (!int.TryParse(minsStr, out var mins) || mins <= 0 || mins > 240)
                mins = 30;

            var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var now = DateTime.UtcNow;
            var exp = now.AddMinutes(mins);

            var jwt = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: new[] {
                    new Claim(JwtRegisteredClaimNames.Sub, input.guestId),
                    new Claim("device", input.device ?? "")
                },
                notBefore: now,
                expires: exp,
                signingCredentials: creds);

            var token = new JwtSecurityTokenHandler().WriteToken(jwt);

            var ok = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(new TokenResponse(
                token,
                new DateTimeOffset(exp).ToUnixTimeSeconds()
            ));
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to issue token");
            return ServerError(req, "Failed to issue token");
        }
    }

    private static HttpResponseData Bad(HttpRequestData req, string msg)
    {
        var r = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
        r.WriteString(msg);
        return r;
    }
    private static HttpResponseData ServerError(HttpRequestData req, string msg)
    {
        var r = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
        r.WriteString(msg);
        return r;
    }
}
