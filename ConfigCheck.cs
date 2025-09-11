using System;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public class ConfigCheck
{
    [Function("ConfigCheck")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        var resp = req.CreateResponse();

        try
        {
            // Read from environment directly (bypasses DI)
            string secret   = Environment.GetEnvironmentVariable("JWT_SIGNING_SECRET") ?? "";
            string issuer   = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "";
            string audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "";
            string minutes  = Environment.GetEnvironmentVariable("JWT_MINUTES") ?? "";

            resp.StatusCode = HttpStatusCode.OK;
            resp.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            resp.WriteString(
                $"ok=true\n" +
                $"secretLen={secret.Length}\n" +
                $"issuer='{issuer}'\n" +
                $"audience='{audience}'\n" +
                $"minutes='{minutes}'\n"
            );
            return resp;
        }
        catch (Exception ex)
        {
            resp.StatusCode = HttpStatusCode.InternalServerError;
            resp.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            resp.WriteString("ConfigCheck exception: " + ex.ToString());
            return resp;
        }
    }
}
