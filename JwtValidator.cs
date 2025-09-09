using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

public class JwtValidator
{
    private readonly TokenValidationParameters _parameters;
    private readonly JwtSecurityTokenHandler _handler = new();

    public JwtValidator(IConfiguration cfg)
    {
        var secret = cfg["JWT_SIGNING_SECRET"]
                     ?? throw new InvalidOperationException("JWT_SIGNING_SECRET missing");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

        _parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidIssuer = "echo-backend",
            ValidAudience = "echo-api",
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    public ClaimsPrincipal? Validate(string token, out SecurityToken? validatedToken)
    {
        try
        {
            var principal = _handler.ValidateToken(token, _parameters, out validatedToken);
            return principal;
        }
        catch
        {
            validatedToken = null;
            return null;
        }
    }
    
    
}
