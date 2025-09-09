using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.Net;

public record GuestRequest(
    [property: JsonPropertyName("guestId")] string guestId,
    [property: JsonPropertyName("device")]  string? device
);

public class AuthGuest
{
    private readonly string _secret;
    private const string Issuer   = "echo-backend";
    private const string Audience = "echo-api";

    public AuthGuest(IConfiguration cfg)
        => _secret = cfg["JWT_SIGNING_SECRET"] 
                    ?? throw new InvalidOperationException("JWT_SIGNING_SECRET missing");

    [Function("AuthGuest")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var payload = await req.ReadFromJsonAsync<GuestRequest>();
        if (payload is null || string.IsNullOrWhiteSpace(payload.guestId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("guestId required");
            return bad;
        }

        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var now = DateTime.UtcNow;
        var exp = now.AddMinutes(30);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, payload.guestId),
            new Claim("device", payload.device ?? "")
        };

        var jwt   = new JwtSecurityToken(Issuer, Audience, claims, notBefore: now, expires: exp, signingCredentials: creds);
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new { token, expiresUnix = new DateTimeOffset(exp).ToUnixTimeSeconds() });
        return ok;
    }
}
