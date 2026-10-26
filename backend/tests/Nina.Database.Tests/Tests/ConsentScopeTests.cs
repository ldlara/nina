using Nina.Database.Tests.Infrastructure;

namespace Nina.Database.Tests.Tests;

/// <summary>Anexo A do reteste, O5: consentimento de bebê exige vínculo ativo com o bebê; origens reservadas ao banco são recusadas ao app.</summary>
[Collection(PgClusterGroup.Name)]
public sealed class ConsentScopeTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static string Consent(Guid user, Guid? baby, string source = "SETTINGS") =>
        $"INSERT INTO nina.consent_record (user_id, subject_baby_id, purpose_key, policy_version, text_hash, locale, status, source) VALUES ('{user}', {(baby is null ? "NULL" : $"'{baby}'")}, 'child_data_guardian', '1.0.0', '{new string('a', 64)}', 'pt-BR', 'GRANTED', '{source}')";

    [Fact]
    public async Task O5_a_baby_consent_requires_an_active_membership_with_that_baby()
    {
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Consent(W.Dave, W.BabyA)));       // sem vinculo: violacao de RLS
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Consent(W.Dave, Guid.NewGuid())));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Consent(W.Alice, W.BabyA)));      // em nome de outro usuario
        Assert.Null(await FailsAsync(Role.App, W.Carol, Consent(W.Carol, W.BabyA)));              // membro (READ_ONLY) do bebe
        Assert.Null(await FailsAsync(Role.App, W.Dave, Consent(W.Dave, null)));                    // consentimento de usuario nao exige bebe
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, Consent(W.Carol, W.BabyA, "SYSTEM")));          // origens reservadas ao banco
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, Consent(W.Carol, W.BabyA, "INVITE_ACCEPT")));
    }
}
