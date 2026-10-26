using Nina.Identity.Persistence;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Services;

/// <summary>Consulta o banco a cada requisição para que a revogação valha imediatamente (AZ-17).</summary>
public sealed class IdentitySessionValidator(NinaDb db, TimeProvider time) : ISessionValidator
{
    public Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        db.InTransactionAsync(userId, tx => IdentityStore.IsSessionActiveAsync(tx, sessionId, time.GetUtcNow()), cancellationToken);
}
