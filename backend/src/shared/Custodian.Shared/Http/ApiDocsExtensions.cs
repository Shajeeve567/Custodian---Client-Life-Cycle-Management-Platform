using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Scalar.AspNetCore;

namespace Custodian.Shared.Http;

public static class ApiDocsExtensions
{
    public const string EnabledConfig = "ApiDocs:Enabled";
    public const string OpenApiPath = "/openapi/v1.json";
    public const string SwaggerUiPath = "/swagger";
    public const string ScalarPath = "/scalar/v1";
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// Registers the OpenAPI document (served at /openapi/v1.json) with the JWT Bearer scheme, so the
    /// Swagger UI / Scalar "Authorize" button can send a token to the protected endpoints.
    /// </summary>
    public static IServiceCollection AddCustodianApiDocs(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            // No servers: the UIs call the origin they were loaded from. Azure terminates TLS in front of the
            // app, so the request-derived server would be http:// and the browser would block "Try it out".
            document.Servers = [];

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Workspace token from Identity: POST /api/auth/login, then POST /api/auth/select-workspace/{tenantId}."
            };
            document.SecurityRequirements.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = BearerScheme }
                }] = []
            });
            return Task.CompletedTask;
        }));

    /// <summary>
    /// Serves the OpenAPI document, Swagger UI (/swagger) and Scalar (/scalar/v1).
    /// - Development: always on.
    /// - elsewhere: only when 'ApiDocs:Enabled' is true (env ApiDocs__Enabled), so a deployed service
    ///   publishes its API description only on purpose. The endpoints themselves still require a JWT.
    /// </summary>
    public static WebApplication UseCustodianApiDocs(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>(EnabledConfig))
        {
            return app;
        }

        app.MapOpenApi();
        app.MapScalarApiReference();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint(OpenApiPath, $"{app.Environment.ApplicationName} API");
            options.DocumentTitle = $"Custodian {app.Environment.ApplicationName} API";
        });
        return app;
    }
}
