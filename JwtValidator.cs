using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

public sealed class JwtValidator
{
    private readonly TokenValidationParameters _parameters;
    private readonly JwtSecurityTokenHandler _handler = new();

    public JwtValidator(IConfiguration cfg)
    {
        var secret   = cfg["JWT_SIGNING_SECRET"] ?? throw new InvalidOperationException("JWT_SIGNING_SECRET missing");
        var issuer   = cfg["JWT_ISSUER"]          ?? "echo-backend";
        var audience = cfg["JWT_AUDIENCE"]        ?? "echo-api";

        _parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),

            ValidateIssuer = true,
            ValidIssuer = issuer,

            ValidateAudience = true,
            ValidAudience = audience,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2) // tolerate small clock drift
        };
    }

    public ClaimsPrincipal? Validate(string token, out string? reason)
    {
        try
        {
            var principal = _handler.ValidateToken(token, _parameters, out _);
            reason = null;
            return principal;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
