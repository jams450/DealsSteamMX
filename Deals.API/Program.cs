using Deals.API;
using Deals.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ExceptionHandler>();
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services
    .AddApiMvc()
    .AddApiOpenApi()
    .AddApiHttpContext()
    .AddApiCors(builder.Configuration)
    .AddApiDatabase(builder.Configuration)
    .AddApiAuthentication()
    .AddApiAuthorization()
    .AddApiRateLimiting()
    .AddApiApplicationServices();

var app = builder.Build();

await app.SeedStartupAdminAsync();

app.UseApiOpenApiIfDevelopment();

app.UseExceptionHandler();
app.UseCors("Production");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
