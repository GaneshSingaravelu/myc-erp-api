using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ERP.Api.Swagger;

/// <summary>
/// Adds an <c>X-Tenant-Id</c> input to Swagger UI.
///
/// <para><b>Documentation only.</b> <c>TenantMiddleware</c> already requires this header on
/// every route it does not skip, and validates it — unknown tenant 404, inactive tenant 403,
/// header/JWT mismatch 403. Nothing here changes what is enforced; without it the UI simply
/// had no box to type the value into, so every call arrived headerless and was refused with
/// 400 TENANT_HEADER_MISSING.</para>
///
/// <para>Operations that declare the header themselves are left alone — today that is
/// <c>POST /auth/login</c> and <c>POST /auth/refresh</c>, both of which take it as a
/// <c>[FromHeader]</c> parameter. That single rule also keeps the box off the one API route
/// the middleware skips (<c>/api/v1/auth/login</c>), so the middleware's skip list is not
/// duplicated here where it could drift out of sync.</para>
///
/// <para><b>Deliberately optional.</b> The middleware does require the header, so
/// <c>Required = true</c> would read as more accurate — but it would stop the UI from
/// omitting the header, and omitting it is itself a case worth exercising (the
/// TENANT_HEADER_MISSING refusal). The description carries the real expectation instead.</para>
/// </summary>
public sealed class TenantHeaderOperationFilter : IOperationFilter
{
    private const string HeaderName = "X-Tenant-Id";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= [];

        if (operation.Parameters.Any(p =>
                string.Equals(p.Name, HeaderName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description = "Tenant code, e.g. dev-tenant.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }
}
