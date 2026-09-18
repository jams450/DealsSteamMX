using Deals.API;
using Deals.API.Extensions;

var builder = WebApplication.CreateBuilder(args);

// La API de gg.deals solo acepta la credencial como query param, así que la key viaja en la URL.
// HttpClient registra la URI completa en nivel Information, y eso la filtraría a los logs.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ExceptionHandler>();
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<SteamOptions>(builder.Configuration.GetSection(SteamOptions.SectionName));
builder.Services.Configure<ItadOptions>(builder.Configuration.GetSection(ItadOptions.SectionName));
builder.Services.Configure<GgDealsOptions>(builder.Configuration.GetSection(GgDealsOptions.SectionName));
builder.Services.Configure<FxOptions>(builder.Configuration.GetSection(FxOptions.SectionName));
builder.Services.Configure<WishlistOptions>(builder.Configuration.GetSection(WishlistOptions.SectionName));

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
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();

app.Run();
