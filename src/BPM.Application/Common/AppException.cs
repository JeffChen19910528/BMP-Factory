namespace BPM.Application.Common;

// Base for expected, business-meaningful failures (not-found, conflict, forbidden, validation).
// A middleware in BPM.Api maps these to the uniform error response shape (Skill.md §36):
// { "code", "message", "traceId" }. Never throw this for truly unexpected failures — let those
// surface as 500s so they get logged loudly instead of silently mapped to a "nice" response.
public abstract class AppException : Exception
{
    protected AppException(int statusCode, string code, string message) : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}

public class NotFoundAppException : AppException
{
    public NotFoundAppException(string code, string message) : base(404, code, message)
    {
    }
}

public class ConflictAppException : AppException
{
    public ConflictAppException(string code, string message) : base(409, code, message)
    {
    }
}

public class ForbiddenAppException : AppException
{
    public ForbiddenAppException(string code, string message) : base(403, code, message)
    {
    }
}

public class BadRequestAppException : AppException
{
    public BadRequestAppException(string code, string message) : base(400, code, message)
    {
    }
}

// Carries structured per-field/per-rule errors (Skill.md §12 example: { code, errors: [...] }).
public class ValidationAppException : AppException
{
    public ValidationAppException(string code, string message, IReadOnlyList<(string Code, string Message)> errors)
        : base(400, code, message)
    {
        Errors = errors;
    }

    public IReadOnlyList<(string Code, string Message)> Errors { get; }
}
