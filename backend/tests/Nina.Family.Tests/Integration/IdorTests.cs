using System.Net;
using System.Text.Json.Nodes;
using Nina.Family.Tests.Infrastructure;

namespace Nina.Family.Tests.Integration;

/// <summary>
/// IDOR/BOLA (Anexo B 9 da estratégia de testes): CADA endpoint com <c>baby_id</c> e <c>membership_id</c> é chamado por quem não tem vínculo,
/// com vínculo revogado, com papel insuficiente, com id de outro bebê e sem autenticação. Falhas são acumuladas para listar todos os endpoints.
/// </summary>
public sealed class IdorTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    private sealed record Endpoint(string Name, HttpMethod Method, string Template, Func<Guid, JsonObject?> Body, bool OwnerOnly, bool HasMembership);

    private static readonly Endpoint[] Endpoints =
    [
        new("getBaby", HttpMethod.Get, "/v1/babies/{b}", _ => null, false, false),
        new("updateBaby", HttpMethod.Patch, "/v1/babies/{b}", _ => new JsonObject { ["display_name"] = "Hack" }, true, false),
        new("deleteBaby", HttpMethod.Delete, "/v1/babies/{b}", _ => null, true, false),
        new("listCaregivers", HttpMethod.Get, "/v1/babies/{b}/caregivers", _ => null, false, false),
        new("updateCaregiverRole", HttpMethod.Patch, "/v1/babies/{b}/caregivers/{m}", _ => new JsonObject { ["role"] = "READ_ONLY" }, true, true),
        new("removeCaregiver", HttpMethod.Delete, "/v1/babies/{b}/caregivers/{m}", _ => null, true, true),
        new("createInvitation", HttpMethod.Post, "/v1/babies/{b}/invitations", _ => new JsonObject { ["email"] = "idor-target@example.org", ["role"] = "CAREGIVER" }, true, false),
        new("resendInvitation", HttpMethod.Post, "/v1/babies/{b}/invitations/{m}/resend", _ => null, true, true),
        new("transferOwnership", HttpMethod.Post, "/v1/babies/{b}/ownership-transfer", m => new JsonObject { ["new_owner_membership_id"] = m.ToString() }, true, true),
    ];

    private sealed record World(
        Session OwnerA, Guid BabyA, Member Caregiver, Member ReadOnly, Guid PendingA, Member Revoked,
        Session OwnerB, Guid BabyB, Session Stranger);

    private async Task<World> BuildAsync()
    {
        var ownerA = await NewOwnerAsync("Dona A");
        var babyA = await CreateBabyAsync(ownerA, "Bebe A");
        var caregiver = await JoinAsync(ownerA, babyA, "CAREGIVER", "Cuidador");
        var readOnly = await JoinAsync(ownerA, babyA, "READ_ONLY", "Leitor");
        var revoked = await JoinAsync(ownerA, babyA, "CAREGIVER", "Revogado");
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"/v1/babies/{babyA}/caregivers/{revoked.MembershipId}", ownerA.AccessToken)).Status);
        var pending = await InviteAsync(ownerA, babyA, ApiClient.NewEmail());
        var ownerB = await NewOwnerAsync("Dona B");
        var babyB = await CreateBabyAsync(ownerB, "Bebe B");
        return new World(ownerA, babyA, caregiver, readOnly, Guid.Parse(pending.Json!["id"]!.GetValue<string>()), revoked, ownerB, babyB, await NewUserAsync("Estranho"));
    }

    private async Task<ApiResponse> CallAsync(Endpoint e, string? bearer, Guid baby, Guid membership, string? reauth = null)
    {
        var path = e.Template.Replace("{b}", baby.ToString(), StringComparison.Ordinal).Replace("{m}", membership.ToString(), StringComparison.Ordinal);
        return await Api.SendAsync(e.Method, path, e.Body(membership), bearer, reauth is null ? null : ApiClient.Header("X-Reauth-Token", reauth),
            e.Method == HttpMethod.Patch ? "application/merge-patch+json" : "application/json");
    }

    private async Task<string> StateAsync(World w) =>
        await AdminScalarAsync<string>(
            $"""
            SELECT (SELECT coalesce(string_agg(id || status || role || coalesce(user_id::text, '') || coalesce(invited_email, ''), ',' ORDER BY id), '') FROM nina.caregiver_membership)
                   || '|' || (SELECT coalesce(string_agg(id || version::text || coalesce(display_name, '') || coalesce(deleted_at::text, ''), ',' ORDER BY id), '') FROM nina.baby)
                   || '|' || (SELECT count(*) FROM nina.outbox_message WHERE event_type IN ('BabyDeleted', 'BabyOwnershipTransferred', 'CaregiverRemoved'))
            """) ?? string.Empty;

    private static string Describe(ApiResponse r) => $"{(int)r.Status} {r.Code} {r.Json?["title"]}";

    [Fact]
    public async Task Stranger_gets_the_same_404_as_for_a_nonexistent_baby_on_every_endpoint_and_changes_nothing()
    {
        var w = await BuildAsync();
        var before = await StateAsync(w);
        var mailsBefore = Factory.FamilyMailer.Sent.Count;
        var failures = new List<string>();
        foreach (var e in Endpoints)
        {
            var reauth = e.Name is "deleteBaby" ? await Api.ReauthAsync(w.Stranger, "BABY_DELETE") : e.Name is "transferOwnership" ? await Api.ReauthAsync(w.Stranger, "OWNERSHIP_TRANSFER") : null;
            var real = await CallAsync(e, w.Stranger.AccessToken, w.BabyA, w.Caregiver.MembershipId, reauth);
            var ghost = await CallAsync(e, w.Stranger.AccessToken, Guid.NewGuid(), Guid.NewGuid(), reauth);
            if (real.Status != HttpStatusCode.NotFound || Describe(real) != Describe(ghost) || real.Json?["type"]?.ToString() != ghost.Json?["type"]?.ToString())
            {
                failures.Add($"{e.Name}: real={Describe(real)} ghost={Describe(ghost)}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(before, await StateAsync(w));
        Assert.Equal(mailsBefore, Factory.FamilyMailer.Sent.Count);
        // o estranho continua sem acesso e sem nenhum bebê na lista
        Assert.Empty((await Api.GetAsync("/v1/babies", w.Stranger.AccessToken)).Json!["items"]!.AsArray());
    }

    [Fact]
    public async Task A_membership_id_from_another_baby_is_a_404_even_for_the_owner_of_the_path_baby()
    {
        var w = await BuildAsync();
        var before = await StateAsync(w);
        var failures = new List<string>();
        foreach (var e in Endpoints.Where(x => x.HasMembership))
        {
            foreach (var foreign in new[] { w.Caregiver.MembershipId, w.PendingA })
            {
                var reauth = e.Name == "transferOwnership" ? await Api.ReauthAsync(w.OwnerB, "OWNERSHIP_TRANSFER") : null;
                var response = await CallAsync(e, w.OwnerB.AccessToken, w.BabyB, foreign, reauth);
                if (response.Status != HttpStatusCode.NotFound)
                {
                    failures.Add($"{e.Name}: {Describe(response)}");
                }
            }
        }

        Assert.Empty(failures);
        Assert.Equal(before, await StateAsync(w));
    }

    [Fact]
    public async Task A_revoked_member_gets_403_ACCESS_REVOKED_on_every_endpoint()
    {
        var w = await BuildAsync();
        var before = await StateAsync(w);
        var failures = new List<string>();
        foreach (var e in Endpoints)
        {
            var reauth = e.Name is "deleteBaby" ? await Api.ReauthAsync(w.Revoked.Session, "BABY_DELETE") : e.Name is "transferOwnership" ? await Api.ReauthAsync(w.Revoked.Session, "OWNERSHIP_TRANSFER") : null;
            var response = await CallAsync(e, w.Revoked.Session.AccessToken, w.BabyA, w.ReadOnly.MembershipId, reauth);
            if (response.Status != HttpStatusCode.Forbidden || response.Code != "ACCESS_REVOKED")
            {
                failures.Add($"{e.Name}: {Describe(response)}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(before, await StateAsync(w));
    }

    [Fact]
    public async Task Caregiver_and_read_only_get_403_FORBIDDEN_ROLE_on_owner_only_endpoints_but_can_read()
    {
        var w = await BuildAsync();
        var before = await StateAsync(w);
        var failures = new List<string>();
        foreach (var (who, target) in new[] { (w.Caregiver.Session, w.ReadOnly.MembershipId), (w.ReadOnly.Session, w.Caregiver.MembershipId) })
        {
            foreach (var e in Endpoints)
            {
                var reauth = e.Name is "deleteBaby" ? await Api.ReauthAsync(who, "BABY_DELETE") : e.Name is "transferOwnership" ? await Api.ReauthAsync(who, "OWNERSHIP_TRANSFER") : null;
                var response = await CallAsync(e, who.AccessToken, w.BabyA, target, reauth);
                var ok = e.OwnerOnly
                    ? response is { Status: HttpStatusCode.Forbidden, Code: "FORBIDDEN_ROLE" }
                    : response.Status == HttpStatusCode.OK;
                if (!ok)
                {
                    failures.Add($"{e.Name} as {who.Email}: {Describe(response)}");
                }
            }
        }

        Assert.Empty(failures);
        Assert.Equal(before, await StateAsync(w));
    }

    [Fact]
    public async Task Owner_of_one_baby_cannot_act_on_another_babys_resources()
    {
        var w = await BuildAsync();
        var before = await StateAsync(w);
        var failures = new List<string>();
        foreach (var e in Endpoints)
        {
            var reauth = e.Name is "deleteBaby" ? await Api.ReauthAsync(w.OwnerB, "BABY_DELETE") : e.Name is "transferOwnership" ? await Api.ReauthAsync(w.OwnerB, "OWNERSHIP_TRANSFER") : null;
            var response = await CallAsync(e, w.OwnerB.AccessToken, w.BabyA, w.Caregiver.MembershipId, reauth);
            if (response.Status != HttpStatusCode.NotFound)
            {
                failures.Add($"{e.Name}: {Describe(response)}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(before, await StateAsync(w));
    }

    [Fact]
    public async Task Every_endpoint_requires_authentication()
    {
        var w = await BuildAsync();
        var failures = new List<string>();
        foreach (var e in Endpoints)
        {
            var response = await CallAsync(e, null, w.BabyA, w.Caregiver.MembershipId);
            if (response.Status != HttpStatusCode.Unauthorized || !response.IsProblem)
            {
                failures.Add($"{e.Name}: {Describe(response)}");
            }
        }

        foreach (var path in new[] { "/v1/invitations/inspect", "/v1/invitations/accept", "/v1/invitations/decline", "/v1/babies" })
        {
            var response = await Api.PostAsync(path, new JsonObject { ["token"] = new string('a', 32) });
            if (response.Status != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{path}: {Describe(response)}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.GetAsync("/v1/babies", "not-a-jwt")).Status);
    }

    [Fact]
    public async Task A_former_member_loses_access_to_the_baby_after_it_is_deleted_without_learning_anything_else()
    {
        var w = await BuildAsync();
        var token = await Api.ReauthAsync(w.OwnerA, "BABY_DELETE");
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"/v1/babies/{w.BabyA}?acknowledge_other_caregivers=true", w.OwnerA.AccessToken, ApiClient.Header("X-Reauth-Token", token))).Status);

        foreach (var who in new[] { w.OwnerA, w.Caregiver.Session, w.ReadOnly.Session, w.Revoked.Session })
        {
            foreach (var e in Endpoints.Where(x => x.Method == HttpMethod.Get))
            {
                Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(e, who.AccessToken, w.BabyA, w.PendingA)).Status);
            }
        }
    }
}
