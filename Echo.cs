using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace EchoFuncApp;

public class EchoRequest
{
    [JsonPropertyName("guestId")] public string? GuestId { get; set; }
    [JsonPropertyName("text")]    public string? Text    { get; set; }
}

public class EchoResponse
{
    [JsonPropertyName("guestId")] public string? GuestId { get; set; }
    [JsonPropertyName("reply")]   public string? Reply   { get; set; }
}

public class Echo
{
    private static readonly Dictionary<string, List<string>> Store = new();
    private readonly ILogger _log;
    private readonly JwtValidator _validator;

    public Echo(ILoggerFactory loggerFactory, JwtValidator validator)
    {
        _log = loggerFactory.CreateLogger<Echo>();
        _validator = validator;
    }

    [Function("Echo")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        // 1) Require Bearer token
        if (!req.Headers.TryGetValues("Authorization", out var vals) ||
            vals.FirstOrDefault() is not string auth ||
            !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var r = req.CreateResponse(HttpStatusCode.Unauthorized);
            await r.WriteStringAsync("Missing Bearer token.");
            return r;
        }

        var token = auth.Substring("Bearer ".Length).Trim();

        if (!_validator.TryValidate(token, out var principal, out var why))
        {
            var r = req.CreateResponse(HttpStatusCode.Unauthorized);
            await r.WriteStringAsync("Invalid or expired token: " + why);
            return r;
        }

        var sub = principal!.FindFirstValue("sub")
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(sub))
        {
            var r = req.CreateResponse(HttpStatusCode.Unauthorized);
            await r.WriteStringAsync("Token missing 'sub' claim.");
            return r;
        }

        // 2) Parse body
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        _log.LogInformation("Echo received: {Body}", body);

        EchoRequest? data;
        try { data = JsonSerializer.Deserialize<EchoRequest>(body); }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON.");
            return bad;
        }

        if (string.IsNullOrWhiteSpace(data?.GuestId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("guestId required.");
            return bad;
        }

        if (!string.Equals(data.GuestId, sub, StringComparison.Ordinal))
        {
            var r = req.CreateResponse(HttpStatusCode.Unauthorized);
            await r.WriteStringAsync("guestId mismatch.");
            return r;
        }

        // 3) Store + reply
        var txt = data.Text ?? "";
        lock (Store)
        {
            if (!Store.TryGetValue(data.GuestId!, out var list))
                Store[data.GuestId!] = list = new();
            list.Add(txt);
        }

        var respObj = new EchoResponse { GuestId = data.GuestId, Reply = $"You said: {txt}" };
        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(respObj);
        return resp;
    }
}
