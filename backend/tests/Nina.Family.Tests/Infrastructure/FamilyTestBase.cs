using System.Text.Json.Nodes;
using Npgsql;

namespace Nina.Family.Tests.Infrastructure;

/// <summary>Um cuidador que entrou no bebê pelo fluxo real de convite/aceite.</summary>
public sealed record Member(Session Session, Guid MembershipId);

/// <summary>Cenário comum: um Owner com um bebê (Caregiver e ReadOnly opcionais) e um usuário sem vínculo.</summary>
public abstract class FamilyTestBase(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    protected static string BabiesPath(Guid babyId) => $"/v1/babies/{babyId}";

    protected static string TodayUtc(int daysAgo = 0) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-daysAgo)).ToString("yyyy-MM-dd");

    protected Task<Session> NewUserAsync(string name = "Teste") => Api.RegisterAndVerifyAsync(name: name);

    /// <summary>Registra a declaração de responsável legal (versão vigente) como o Identity faria; a API do Identity ainda não a aceita antes do bebê.</summary>
    protected Task GrantGuardianConsentAsync(Guid userId) =>
        AdminExecAsync(
            $"""
            INSERT INTO nina.consent_record (user_id, subject_baby_id, purpose_key, policy_version, text_hash, locale, status, source)
            VALUES ('{userId}', NULL, 'child_data_guardian', '1.0.0', '{new string('a', 64)}', 'pt-BR', 'GRANTED', 'ONBOARDING')
            """);

    protected async Task<Session> NewOwnerAsync(string name = "Dona")
    {
        var owner = await NewUserAsync(name);
        await GrantGuardianConsentAsync(owner.UserId);
        return owner;
    }

    protected async Task<Guid> CreateBabyAsync(Session owner, string name = "Nina", string? birth = null, string? due = null, string timezone = "UTC")
    {
        var body = new JsonObject { ["display_name"] = name, ["birth_date"] = birth ?? TodayUtc(90), ["timezone"] = timezone };
        if (due is not null)
        {
            body["due_date"] = due;
        }

        var created = await Api.PostAsync("/v1/babies", body, owner.AccessToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, created.Status);
        return Guid.Parse(created.Json!["id"]!.GetValue<string>());
    }

    protected async Task<ApiResponse> InviteAsync(Session owner, Guid babyId, string email, string role = "CAREGIVER") =>
        await Api.PostAsync($"/v1/babies/{babyId}/invitations", new JsonObject { ["email"] = email, ["role"] = role }, owner.AccessToken);

    /// <summary>Último token enviado (e-mail fake) para o endereço.</summary>
    protected string LastInviteToken(string email) => Factory.FamilyMailer.Sent.Last(m => m.To == email).Token;

    protected async Task<Member> JoinAsync(Session owner, Guid babyId, string role = "CAREGIVER", string name = "Cuidador")
    {
        var user = await NewUserAsync(name);
        var invited = await InviteAsync(owner, babyId, user.Email, role);
        Assert.Equal(System.Net.HttpStatusCode.Created, invited.Status);
        var accepted = await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = LastInviteToken(user.Email) }, user.AccessToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.Status);
        return new Member(user, Guid.Parse(invited.Json!["id"]!.GetValue<string>()));
    }

    protected Task<ApiResponse> ChangeBabyAsync(Session s, Guid babyId, JsonObject patch, string? ifMatch = null) =>
        Api.PatchAsync(BabiesPath(babyId), patch, s.AccessToken, ifMatch is null ? null : ApiClient.Header("If-Match", ifMatch));

    protected async Task<long> AdminCountAsync(string sql, params NpgsqlParameter[] parameters) =>
        await AdminScalarAsync<long>(sql, parameters);

    /// <summary>Altera um vínculo ignorando a máquina de estados (cenário de teste: convite vencido), com a ficha do dono do schema.</summary>
    protected Task AdminMembershipAsync(string sql) => AdminExecAsync("SELECT nina.guard_arm('membership'); " + sql);
}
