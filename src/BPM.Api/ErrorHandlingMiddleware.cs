using System.Text.Json;
using BPM.Application.Common;
using FluentValidation;

namespace BPM.Api;

// Maps AppException (and FluentValidation's ValidationException) to the uniform error shape
// from Skill.md §36 — never a raw stack trace to the frontend. Anything else is an unexpected
// failure: let it fall through to ASP.NET's default handling (500, logged) rather than hide it.
public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public ErrorHandlingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            context.Response.StatusCode = ex.StatusCode;
            context.Response.ContentType = "application/json";

            object body = ex is ValidationAppException validationEx
                ? new { code = validationEx.Code, message = validationEx.Message, errors = validationEx.Errors.Select(e => new { code = e.Code, message = e.Message }), traceId = context.TraceIdentifier }
                : new { code = ex.Code, message = ex.Message, traceId = context.TraceIdentifier };

            await context.Response.WriteAsync(JsonSerializer.Serialize(body));
        }
        catch (ValidationException ex)
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            var body = new
            {
                code = "VALIDATION_FAILED",
                message = "One or more fields failed validation.",
                errors = ex.Errors.Select(e => new { code = e.ErrorCode, message = e.ErrorMessage }),
                traceId = context.TraceIdentifier,
            };
            await context.Response.WriteAsync(JsonSerializer.Serialize(body));
        }
    }
}
