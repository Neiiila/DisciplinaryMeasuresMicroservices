using BuildingBlocks.Web;
using BuildingBlocks.Web.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddServiceDefaults("gateway");

// The gateway validates the token so an unauthenticated request is rejected at
// the edge rather than forwarded. Services validate it again on arrival: the
// gateway is a filter, never the only check, because anything that reaches a
// service directly must still be authorised.
builder.Services.AddPlatformJwtAuthentication(builder.Configuration);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// CORS lives here alone. The browser only ever talks to the gateway, so putting
// the policy on each service would be three places to keep in step for no gain.
const string FrontEndPolicy = "FrontEnd";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddPolicy(FrontEndPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()));

var app = builder.Build();

app.UseExceptionHandler();

app.UseRouting();
app.UseCors(FrontEndPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapServiceDefaults();
app.MapReverseProxy();

await app.RunAsync();
