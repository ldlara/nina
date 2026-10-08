namespace Nina.SharedKernel.Http;

/// <summary>Erro de campo (RFC 7807 estendido, ver <c>FieldError</c> no contrato).</summary>
public sealed record FieldError(string Field, string Code);

/// <summary>Exceção de domínio/transporte convertida em <c>application/problem+json</c> pelo middleware.</summary>
public sealed class ProblemException : Exception
{
    public ProblemException(int status, string code, string title)
        : base(title)
    {
        Status = status;
        Code = code;
        Title = title;
    }

    public int Status { get; }

    public string Code { get; }

    public string Title { get; }

    public IReadOnlyList<FieldError> Errors { get; init; } = [];

    public int? RetryAfterSeconds { get; init; }

    /// <summary>Extensões adicionais (chaves já em snake_case), ex.: <c>required_consents</c>.</summary>
    public IReadOnlyDictionary<string, object?> Extensions { get; init; } = new Dictionary<string, object?>();

    public static ProblemException Validation(params FieldError[] errors) =>
        new(StatusCodes.Status400BadRequest, "VALIDATION_FAILED", "Validation failed") { Errors = errors };

    public static ProblemException Validation(IEnumerable<FieldError> errors) =>
        new(StatusCodes.Status400BadRequest, "VALIDATION_FAILED", "Validation failed") { Errors = [.. errors] };

    public static ProblemException Unauthorized(string code, string title) =>
        new(StatusCodes.Status401Unauthorized, code, title);

    public static ProblemException Forbidden(string code, string title) =>
        new(StatusCodes.Status403Forbidden, code, title);

    public static ProblemException NotFound() =>
        new(StatusCodes.Status404NotFound, "NOT_FOUND", "Not found");

    public static ProblemException Conflict(string code, string title) =>
        new(StatusCodes.Status409Conflict, code, title);

    public static ProblemException RateLimited(int retryAfterSeconds) =>
        new(StatusCodes.Status429TooManyRequests, "RATE_LIMITED", "Too many requests") { RetryAfterSeconds = retryAfterSeconds };
}
