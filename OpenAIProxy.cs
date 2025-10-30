using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public class OpenAIResponsesProxy
{
    private readonly ILogger<OpenAIResponsesProxy> _log;
    private readonly IConfiguration _cfg;
    private readonly JwtValidator _jwt;
    private readonly IHttpClientFactory _httpFactory;
    private readonly PartitionedRateLimiter<string> _limiter;

    public OpenAIResponsesProxy(
        ILogger<OpenAIResponsesProxy> log,
        IConfiguration cfg,
        JwtValidator jwt,
        IHttpClientFactory httpFactory,
        PartitionedRateLimiter<string> limiter)
    {
        _log = log;
        _cfg = cfg;
        _jwt = jwt;
        _httpFactory = httpFactory;
        _limiter = limiter;
    }

    [Function("OpenAIResponsesProxy")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        // ---- 1) Auth: Bearer JWT required
        if (!req.Headers.TryGetValues("Authorization", out var vals) ||
            vals.FirstOrDefault() is not string raw ||
            !raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return await Text(req, HttpStatusCode.Unauthorized, "Missing Bearer token.");
        }

        var token = raw["Bearer ".Length..];
        var principal = _jwt.Validate(token, out var why);
        if (principal is null)
        {
            return await Text(req, HttpStatusCode.Unauthorized, "Invalid or expired token: " + why);
        }

        var sub = principal.FindFirst("sub")?.Value
               ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? "unknown";

        // ---- 2) Per-user rate limit
        using var lease = await _limiter.AcquireAsync(sub, 1);
        if (!lease.IsAcquired)
        {
            return await Text(req, (HttpStatusCode)429, "Rate limit exceeded. Try again soon.");
        }

        // ---- 3) Read & parse JSON body
        var body = await new StreamReader(req.Body, Encoding.UTF8).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return await Text(req, HttpStatusCode.BadRequest, "Empty body.");
        }

        JsonObject node;
        try
        {
            node = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return await Text(req, HttpStatusCode.BadRequest, "Invalid JSON.");
        }

        // ---- 4) Strict validation for Responses API (no shims)
        // model (required)
        if (!node.TryGetPropertyValue("model", out var modelNode) ||
            string.IsNullOrWhiteSpace(modelNode?.ToString()))
        {
            return await Text(req, HttpStatusCode.BadRequest,
                "Missing 'model' (e.g., \"gpt-4.1\"). See https://platform.openai.com/docs/api-reference/responses/create");
        }

        // reject legacy/incorrect params
        if (node.TryGetPropertyValue("messages", out _))
        {
            return await Text(req, HttpStatusCode.BadRequest,
                "Unsupported parameter: 'messages'. Use 'input' (Responses API). See https://platform.openai.com/docs/api-reference/responses/create");
        }
        if (node.TryGetPropertyValue("response_format", out _))
        {
            return await Text(req, HttpStatusCode.BadRequest,
                "Unsupported parameter: 'response_format'. Use 'text.format' (Responses API). See https://platform.openai.com/docs/api-reference/responses/create");
        }
        if (node.TryGetPropertyValue("max_tokens", out _))
        {
            return await Text(req, HttpStatusCode.BadRequest,
                "Unsupported parameter: 'max_tokens'. Use 'max_output_tokens' (Responses API).");
        }

        // input (required, must be array)
        if (!node.TryGetPropertyValue("input", out var inputNode) || inputNode is not JsonArray)
        {
            return await Text(req, HttpStatusCode.BadRequest,
                "Missing or invalid 'input'. Provide an array of {role, content} items per the Responses API.");
        }

        // Optional: if 'text.format' exists, ensure it's an object
        if (node.TryGetPropertyValue("text", out var textNode) && textNode is not null)
        {
            if (textNode is not JsonObject textObj)
                return await Text(req, HttpStatusCode.BadRequest, "'text' must be an object.");

            if (textObj.TryGetPropertyValue("format", out var fmtNode) && fmtNode is not null && fmtNode is not JsonObject)
                return await Text(req, HttpStatusCode.BadRequest, "'text.format' must be an object.");
        }

        // ---- 5) Config: OpenAI endpoint + key
        var apiKey  = _cfg["OPENAI_API_KEY"];
        var apiBase = _cfg["OPENAI_API_BASE"]?.TrimEnd('/') ?? "https://api.openai.com";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return await Text(req, HttpStatusCode.InternalServerError, "Missing OPENAI_API_KEY.");
        }

        // ---- 6) Streaming?
        var wantsStream = node.TryGetPropertyValue("stream", out var s) && s?.GetValue<bool>() == true;

        var json = node.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false
        });

        // ---- 7) Forward to OpenAI Responses API
        var http = _httpFactory.CreateClient();
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/v1/responses");
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        msg.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage upstream;
        try
        {
            upstream = await http.SendAsync(
                msg,
                wantsStream ? HttpCompletionOption.ResponseHeadersRead
                            : HttpCompletionOption.ResponseContentRead);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "OpenAI upstream call failed.");
            return await Text(req, HttpStatusCode.BadGateway, $"Upstream error: {ex.Message}");
        }

        // ---- 8) Relay response (JSON or SSE stream)
        var clientResp = req.CreateResponse((HttpStatusCode)upstream.StatusCode);
        clientResp.Headers.Add("x-upstream", "openai-v1-responses");
        clientResp.Headers.Add("x-upstream-status", ((int)upstream.StatusCode).ToString());

        if (wantsStream && upstream.IsSuccessStatusCode)
        {
            clientResp.Headers.Add("Cache-Control", "no-cache");
            clientResp.Headers.Add("Connection", "keep-alive");
            clientResp.Headers.Add("Content-Type", "text/event-stream");

            await using var inStream = await upstream.Content.ReadAsStreamAsync();
            await inStream.CopyToAsync(clientResp.Body); // SSE passthrough
            return clientResp;
        }
        else
        {
            var respBody = await upstream.Content.ReadAsStringAsync();
            // If upstream returns empty body, make it explicit to callers
            await clientResp.WriteStringAsync(string.IsNullOrWhiteSpace(respBody) ? "(empty upstream body)" : respBody);
            return clientResp;
        }
    }

    // ----------------- helpers -----------------

    private static async Task<HttpResponseData> Text(HttpRequestData req, HttpStatusCode code, string msg)
    {
        var r = req.CreateResponse(code);
        r.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await r.WriteStringAsync(msg);
        return r;
    }
}
