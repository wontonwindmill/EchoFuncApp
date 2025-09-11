using System.Net;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public class EnvCheck
{
    [Function("EnvCheck")]
    public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        // Build the response first, then write once (to avoid partial writes)
        var sb = new StringBuilder();
        try
        {
            string secret   = System.Environment.GetEnvironmentVariable("JWT_SIGNING_SECRET") ?? "";
            string issuer   = System.Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "";
            string audience = System.Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "";
            string minutes  = System.Environment.GetEnvironmentVariable("JWT_MINUTES") ?? "";

            string fwr      = System.Environment.GetEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME") ?? "";
            string fev      = System.Environment.GetEnvironmentVariable("FUNCTIONS_EXTENSION_VERSION") ?? "";
            string storage  = System.Environment.GetEnvironmentVariable("AzureWebJobsStorage") ?? "";

            sb.AppendLine("ok=true");
            sb.AppendLine($"secretLen={secret.Length}");
            sb.AppendLine($"issuer='{issuer}'");
            sb.AppendLine($"audience='{audience}'");
            sb.AppendLine($"minutes='{minutes}'");
            sb.AppendLine($"FUNCTIONS_WORKER_RUNTIME='{fwr}'");
            sb.AppendLine($"FUNCTIONS_EXTENSION_VERSION='{fev}'");
            sb.AppendLine($"AzureWebJobsStorageSet={(string.IsNullOrEmpty(storage) ? "false" : "true")}");
        }
        catch (System.Exception ex)
        {
            sb.Clear();
            sb.Append("EnvCheck exception: ").Append(ex.ToString());
        }

        var res = req.CreateResponse(HttpStatusCode.OK);
        res.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        res.WriteString(sb.ToString());
        return res;
    }
}
