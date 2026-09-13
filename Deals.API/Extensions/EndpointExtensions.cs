namespace Deals.API.Extensions;

public static class EndpointExtensions
{
    public static WebApplication UseApiOpenApiIfDevelopment(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        return app;
    }
}
