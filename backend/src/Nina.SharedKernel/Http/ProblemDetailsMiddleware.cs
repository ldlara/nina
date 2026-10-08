using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Nina.SharedKernel.Http;

/// <summary>
/// Correlação (<c>X-Request-Id</c>) e conversão de erros em RFC 7807. Nunca expõe stack trace, SQL ou IDs internos (AZ-19).
/// </summary>
public sealed partial class ProblemDetailsMiddleware(RequestDelegate next, ILogger<ProblemDetailsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = ResolveRequestId(context);
        context.Items[HttpRequestContext.RequestIdItemKey] = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Request-Id"] = requestId;
            return Task.CompletedTask;
        });

        try
        {
            await next(context);

            if (!context.Response.HasStarted && context.Response.StatusCode >= 400
                && context.Response.ContentLength is null && context.Response.ContentType is null)
            {
                await ProblemWriter.WriteAsync(context, FromStatus(context.Response.StatusCode));
            }
        }
        catch (ProblemException ex) when (!context.Response.HasStarted)
        {
            await ProblemWriter.WriteAsync(context, ex);
        }
        catch (BadHttpRequestException) when (!context.Response.HasStarted)
        {
            await ProblemWriter.WriteAsync(context, ProblemException.Validation(new FieldError("body", "INVALID_BODY")));
        }
        catch (PostgresException ex) when (!context.Response.HasStarted && ex.SqlState is PostgresErrorCodes.CheckViolation)
        {
            // CHECK do schema (ex.: fuso IANA, formato de locale) que a validação da aplicação não cobriu.
            await ProblemWriter.WriteAsync(context, ProblemException.Validation(new FieldError(ex.ConstraintName ?? "body", "INVALID_VALUE")));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Cliente desistiu; nada a escrever.
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            LogUnhandled(logger, ex.GetType().Name, requestId);
            await ProblemWriter.WriteAsync(context, new ProblemException(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR", "Internal error"));
        }
    }

    private static ProblemException FromStatus(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => new ProblemException(status, "AUTHENTICATION_REQUIRED", "Authentication required"),
        StatusCodes.Status403Forbidden => new ProblemException(status, "FORBIDDEN_ROLE", "Role not allowed"),
        StatusCodes.Status404NotFound => ProblemException.NotFound(),
        StatusCodes.Status405MethodNotAllowed => new ProblemException(status, "METHOD_NOT_ALLOWED", "Method not allowed"),
        StatusCodes.Status413PayloadTooLarge => new ProblemException(status, "PAYLOAD_TOO_LARGE", "Payload too large"),
        StatusCodes.Status415UnsupportedMediaType => new ProblemException(status, "UNSUPPORTED_MEDIA_TYPE", "Unsupported media type"),
        _ => new ProblemException(status, status >= 500 ? "INTERNAL_ERROR" : "REQUEST_REJECTED", "Request rejected"),
    };

    private static string ResolveRequestId(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Request-Id"].ToString();
        if (incoming.Length is > 0 and <= 64 && incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return incoming;
        }

        return Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled {ExceptionType} (request {RequestId})")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType, string requestId);
}
