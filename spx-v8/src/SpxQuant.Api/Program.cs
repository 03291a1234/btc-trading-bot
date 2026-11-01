using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SpxQuant.Api;
using SpxQuant.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});
builder.Services.AddProblemDetails();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_048_576);
var apiKey = builder.Configuration["Api:Key"];
if (string.IsNullOrWhiteSpace(apiKey) || Encoding.UTF8.GetByteCount(apiKey) < 32)
    throw new InvalidOperationException("Api:Key must be configured (environment Api__Key) with at least 32 bytes");
builder.Services.AddSpxQuantInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["Cache-Control"] = "no-store";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Execution-Mode"] = "PAPER_ONLY";
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString());
        var expected = Encoding.UTF8.GetBytes(apiKey);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});
app.MapPlatformEndpoints();
app.Run();

public partial class Program { }
