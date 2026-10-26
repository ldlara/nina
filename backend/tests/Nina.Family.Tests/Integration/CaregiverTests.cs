using System.Net;
using System.Text.Json.Nodes;
using Nina.Family.Tests.Infrastructure;

namespace Nina.Family.Tests.Integration;

/// <summary>RF-006/RF-007/RF-055: listar cuidadores, papéis, remover/sair e transferir a propriedade.</summary>
public sealed class CaregiverTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    private sealed record Family(Session Owner, Guid Baby, Member Caregiver, Member ReadOnly, Session Stranger);

    private async Task<Family> NewFamilyAsync()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        return new Family(owner, baby, await JoinAsync(owner, baby, "CAREGIVER", "Tia"), await JoinAsync(owner, baby, "READ_ONLY", "Vovô"), await NewUserAsync("Estranho"));
    }

    private static string Members(Guid baby) => $"/v1/babies/{baby}/caregivers";

    // ---------------------------------------------------------------- listar

    [Fact]
    public async Task Owner_sees_everyone_including_pending_invitations_with_the_invited_email()
    {
        var f = await NewFamilyAsync();
        var pendingEmail = ApiClient.NewEmail();
        await InviteAsync(f.Owner, f.Baby, pendingEmail, "READ_ONLY");

        var list = await Api.GetAsync(Members(f.Baby), f.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var items = list.Json!["items"]!.AsArray();
        Assert.Equal(4, items.Count);
        var owner = items.Single(i => i!["role"]!.GetValue<string>() == "OWNER")!;
        Assert.Equal("ACTIVE", owner["status"]!.GetValue<string>());
        Assert.Equal("Dona", owner["user"]!["display_name"]!.GetValue<string>());
        Assert.Equal(f.Owner.UserId.ToString(), owner["user"]!["id"]!.GetValue<string>());
        var pending = items.Single(i => i!["status"]!.GetValue<string>() == "PENDING")!;
        Assert.Equal(pendingEmail, pending["invited_email"]!.GetValue<string>());
        Assert.Null(pending["user"]);
        Assert.Equal("Tia", items.Single(i => i!["id"]!.GetValue<string>() == f.Caregiver.MembershipId.ToString())!["user"]!["display_name"]!.GetValue<string>());
        Assert.All(items, i => Assert.Equal(f.Baby.ToString(), i!["baby_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Caregiver_and_read_only_see_active_members_by_display_name_but_never_emails_or_pending_invitations()
    {
        var f = await NewFamilyAsync();
        var pendingEmail = ApiClient.NewEmail();
        await InviteAsync(f.Owner, f.Baby, pendingEmail);

        foreach (var viewer in new[] { f.Caregiver.Session, f.ReadOnly.Session })
        {
            var list = await Api.GetAsync(Members(f.Baby), viewer.AccessToken);
            Assert.Equal(HttpStatusCode.OK, list.Status);
            var items = list.Json!["items"]!.AsArray();
            Assert.Equal(3, items.Count);
            Assert.All(items, i =>
            {
                Assert.Equal("ACTIVE", i!["status"]!.GetValue<string>());
                Assert.Null(i["invited_email"]);
                Assert.False(string.IsNullOrEmpty(i["user"]!["display_name"]!.GetValue<string>()));
            });
            var text = list.Json.ToJsonString();
            Assert.DoesNotContain(pendingEmail, text, StringComparison.Ordinal);
            Assert.DoesNotContain("@example.org", text, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- papel

    [Fact]
    public async Task Owner_changes_a_role_and_the_new_permission_takes_effect_immediately()
    {
        var f = await NewFamilyAsync();
        var changed = await Api.PatchAsync($"{Members(f.Baby)}/{f.Caregiver.MembershipId}", new JsonObject { ["role"] = "READ_ONLY" }, f.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        Assert.Equal("READ_ONLY", changed.Json!["role"]!.GetValue<string>());
        Assert.Equal("ACTIVE", changed.Json["status"]!.GetValue<string>());
        Assert.Equal("Tia", changed.Json["user"]!["display_name"]!.GetValue<string>());
        Assert.Equal("READ_ONLY", (await Api.GetAsync(BabiesPath(f.Baby), f.Caregiver.Session.AccessToken)).Json!["my_role"]!.GetValue<string>());
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.member_role_changed' AND entity_id = '{f.Caregiver.MembershipId}'"));

        var back = await Api.PatchAsync($"{Members(f.Baby)}/{f.Caregiver.MembershipId}", new JsonObject { ["role"] = "CAREGIVER" }, f.Owner.AccessToken);
        Assert.Equal("CAREGIVER", back.Json!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Role_change_rejects_owner_role_self_demotion_non_owners_and_unknown_memberships()
    {
        var f = await NewFamilyAsync();
        var ownerMembership = (await Api.GetAsync(Members(f.Baby), f.Owner.AccessToken)).Json!["items"]!.AsArray()
            .Single(i => i!["role"]!.GetValue<string>() == "OWNER")!["id"]!.GetValue<string>();

        Assert.Equal("UNSUPPORTED_VALUE", (await Api.PatchAsync($"{Members(f.Baby)}/{f.Caregiver.MembershipId}", new JsonObject { ["role"] = "OWNER" }, f.Owner.AccessToken)).FieldErrorCode("role"));
        Assert.Equal("REQUIRED", (await Api.PatchAsync($"{Members(f.Baby)}/{f.Caregiver.MembershipId}", new JsonObject(), f.Owner.AccessToken)).FieldErrorCode("role"));

        // RF-006-A7: o Owner não rebaixa a si mesmo
        var self = await Api.PatchAsync($"{Members(f.Baby)}/{ownerMembership}", new JsonObject { ["role"] = "READ_ONLY" }, f.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, self.Status);
        Assert.Equal("OWNER_MUST_TRANSFER", self.Code);

        foreach (var member in new[] { f.Caregiver, f.ReadOnly })
        {
            var denied = await Api.PatchAsync($"{Members(f.Baby)}/{f.ReadOnly.MembershipId}", new JsonObject { ["role"] = "CAREGIVER" }, member.Session.AccessToken);
            Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
            Assert.Equal("FORBIDDEN_ROLE", denied.Code);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Api.PatchAsync($"{Members(f.Baby)}/{Guid.NewGuid()}", new JsonObject { ["role"] = "READ_ONLY" }, f.Owner.AccessToken)).Status);
        var pending = await InviteAsync(f.Owner, f.Baby, ApiClient.NewEmail());
        var onPending = await Api.PatchAsync($"{Members(f.Baby)}/{pending.Json!["id"]!.GetValue<string>()}", new JsonObject { ["role"] = "READ_ONLY" }, f.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, onPending.Status);
        Assert.Equal("READ_ONLY", await AdminScalarAsync<string>($"SELECT role FROM nina.caregiver_membership WHERE id = '{f.ReadOnly.MembershipId}'"));
    }

    // ---------------------------------------------------------------- remover / sair

    [Fact]
    public async Task Owner_removes_a_caregiver_and_the_next_call_is_403_ACCESS_REVOKED_on_every_baby_endpoint()
    {
        var f = await NewFamilyAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"{Members(f.Baby)}/{f.Caregiver.MembershipId}", f.Owner.AccessToken)).Status);

        foreach (var response in new[]
        {
            await Api.GetAsync(BabiesPath(f.Baby), f.Caregiver.Session.AccessToken),
            await Api.GetAsync(Members(f.Baby), f.Caregiver.Session.AccessToken),
            await ChangeBabyAsync(f.Caregiver.Session, f.Baby, new JsonObject { ["display_name"] = "x" }),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.Status);
            Assert.Equal("ACCESS_REVOKED", response.Code);
        }

        Assert.Empty((await Api.GetAsync("/v1/babies", f.Caregiver.Session.AccessToken)).Json!["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(f.Baby), f.ReadOnly.Session.AccessToken)).Status);
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.member_removed' AND entity_id = '{f.Caregiver.MembershipId}'"));
        Assert.Equal("OWNER_REMOVED", await AdminScalarAsync<string>($"SELECT revoked_reason FROM nina.caregiver_membership WHERE id = '{f.Caregiver.MembershipId}'"));

        // repetir é idempotente para o Owner
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"{Members(f.Baby)}/{f.Caregiver.MembershipId}", f.Owner.AccessToken)).Status);
    }

    [Fact]
    public async Task A_member_can_leave_by_removing_their_own_membership_but_not_someone_elses()
    {
        var f = await NewFamilyAsync();
        var other = await Api.DeleteAsync($"{Members(f.Baby)}/{f.ReadOnly.MembershipId}", f.Caregiver.Session.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, other.Status);
        Assert.Equal("FORBIDDEN_ROLE", other.Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(f.Baby), f.ReadOnly.Session.AccessToken)).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"{Members(f.Baby)}/{f.ReadOnly.MembershipId}", f.ReadOnly.Session.AccessToken)).Status);
        Assert.Equal("ACCESS_REVOKED", (await Api.GetAsync(BabiesPath(f.Baby), f.ReadOnly.Session.AccessToken)).Code);
        Assert.Equal("LEFT", await AdminScalarAsync<string>($"SELECT revoked_reason FROM nina.caregiver_membership WHERE id = '{f.ReadOnly.MembershipId}'"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.member_left' AND actor_user_id = '{f.ReadOnly.Session.UserId}'"));
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(f.Baby), f.Caregiver.Session.AccessToken)).Status);
    }

    [Fact]
    public async Task The_owner_cannot_leave_or_be_removed_without_transferring_the_ownership()
    {
        var f = await NewFamilyAsync();
        var ownerMembership = (await Api.GetAsync(Members(f.Baby), f.Owner.AccessToken)).Json!["items"]!.AsArray()
            .Single(i => i!["role"]!.GetValue<string>() == "OWNER")!["id"]!.GetValue<string>();

        var leave = await Api.DeleteAsync($"{Members(f.Baby)}/{ownerMembership}", f.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, leave.Status);
        Assert.Equal("OWNER_MUST_TRANSFER", leave.Code);

        // um Caregiver tampouco consegue tirar o Owner
        Assert.Equal(HttpStatusCode.Forbidden, (await Api.DeleteAsync($"{Members(f.Baby)}/{ownerMembership}", f.Caregiver.Session.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(f.Baby), f.Owner.AccessToken)).Status);
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{f.Baby}' AND role = 'OWNER' AND status = 'ACTIVE'"));
    }

    // ---------------------------------------------------------------- transferência

    [Fact]
    public async Task Transfer_ownership_needs_a_scoped_reauth_and_swaps_the_roles()
    {
        var f = await NewFamilyAsync();
        var path = $"/v1/babies/{f.Baby}/ownership-transfer";
        var body = new JsonObject { ["new_owner_membership_id"] = f.Caregiver.MembershipId.ToString() };

        var noToken = await Api.PostAsync(path, body, f.Owner.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.Status);
        Assert.Equal("REAUTH_REQUIRED", noToken.Code);
        var wrongScope = await Api.PostAsync(path, body, f.Owner.AccessToken, ApiClient.Header("X-Reauth-Token", await Api.ReauthAsync(f.Owner, "BABY_DELETE")));
        Assert.Equal("REAUTH_REQUIRED", wrongScope.Code);
        Assert.Equal("OWNER", (await Api.GetAsync(BabiesPath(f.Baby), f.Owner.AccessToken)).Json!["my_role"]!.GetValue<string>());

        var token = await Api.ReauthAsync(f.Owner, "OWNERSHIP_TRANSFER");
        var transferred = await Api.PostAsync(path, body, f.Owner.AccessToken, ApiClient.Header("X-Reauth-Token", token));
        Assert.Equal(HttpStatusCode.OK, transferred.Status);
        Assert.Equal("CAREGIVER", transferred.Json!["my_role"]!.GetValue<string>()); // o Owner anterior vira Caregiver

        Assert.Equal("OWNER", (await Api.GetAsync(BabiesPath(f.Baby), f.Caregiver.Session.AccessToken)).Json!["my_role"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await ChangeBabyAsync(f.Caregiver.Session, f.Baby, new JsonObject { ["display_name"] = "Do novo dono" })).Status);
        var oldOwnerEdit = await ChangeBabyAsync(f.Owner, f.Baby, new JsonObject { ["display_name"] = "Do antigo" });
        Assert.Equal("FORBIDDEN_ROLE", oldOwnerEdit.Code);
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.ownership_transferred' AND baby_id = '{f.Baby}' AND actor_user_id = '{f.Owner.UserId}'"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.outbox_message WHERE event_type = 'BabyOwnershipTransferred' AND aggregate_id = '{f.Baby}'"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{f.Baby}' AND role = 'OWNER' AND status = 'ACTIVE' AND user_id = '{f.Caregiver.Session.UserId}'"));

        // o token é de uso único
        var reuse = await Api.PostAsync(path, new JsonObject { ["new_owner_membership_id"] = f.ReadOnly.MembershipId.ToString() }, f.Caregiver.Session.AccessToken, ApiClient.Header("X-Reauth-Token", token));
        Assert.Equal("REAUTH_REQUIRED", reuse.Code);
    }

    [Fact]
    public async Task Transfer_validates_the_target_and_the_caller()
    {
        var f = await NewFamilyAsync();
        var path = $"/v1/babies/{f.Baby}/ownership-transfer";
        async Task<ApiResponse> Transfer(Session who, JsonObject body) =>
            await Api.PostAsync(path, body, who.AccessToken, ApiClient.Header("X-Reauth-Token", await Api.ReauthAsync(who, "OWNERSHIP_TRANSFER")));

        Assert.Equal("REQUIRED", (await Transfer(f.Owner, new JsonObject())).FieldErrorCode("new_owner_membership_id"));
        Assert.Equal(HttpStatusCode.NotFound, (await Transfer(f.Owner, new JsonObject { ["new_owner_membership_id"] = Guid.NewGuid().ToString() })).Status);

        var ownerMembership = (await Api.GetAsync(Members(f.Baby), f.Owner.AccessToken)).Json!["items"]!.AsArray()
            .Single(i => i!["role"]!.GetValue<string>() == "OWNER")!["id"]!.GetValue<string>();
        var toSelf = await Transfer(f.Owner, new JsonObject { ["new_owner_membership_id"] = ownerMembership });
        Assert.Equal(HttpStatusCode.Conflict, toSelf.Status);

        var pending = await InviteAsync(f.Owner, f.Baby, ApiClient.NewEmail());
        var toPending = await Transfer(f.Owner, new JsonObject { ["new_owner_membership_id"] = pending.Json!["id"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, toPending.Status);

        var asCaregiver = await Transfer(f.Caregiver.Session, new JsonObject { ["new_owner_membership_id"] = f.ReadOnly.MembershipId.ToString() });
        Assert.Equal(HttpStatusCode.Forbidden, asCaregiver.Status);
        Assert.Equal("FORBIDDEN_ROLE", asCaregiver.Code);
        Assert.Equal(HttpStatusCode.NotFound, (await Transfer(f.Stranger, new JsonObject { ["new_owner_membership_id"] = f.ReadOnly.MembershipId.ToString() })).Status);
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{f.Baby}' AND role = 'OWNER' AND status = 'ACTIVE' AND user_id = '{f.Owner.UserId}'"));
    }

    [Fact]
    public async Task After_a_transfer_the_new_owner_can_leave_only_after_transferring_again_and_the_old_owner_can_leave()
    {
        var f = await NewFamilyAsync();
        var token = await Api.ReauthAsync(f.Owner, "OWNERSHIP_TRANSFER");
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync($"/v1/babies/{f.Baby}/ownership-transfer",
            new JsonObject { ["new_owner_membership_id"] = f.Caregiver.MembershipId.ToString() }, f.Owner.AccessToken, ApiClient.Header("X-Reauth-Token", token))).Status);

        var ownerMembership = (await Api.GetAsync(Members(f.Baby), f.Owner.AccessToken)).Json!["items"]!.AsArray()
            .Single(i => i!["user"]?["id"]?.GetValue<string>() == f.Owner.UserId.ToString())!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"{Members(f.Baby)}/{ownerMembership}", f.Owner.AccessToken)).Status);
        Assert.Equal("ACCESS_REVOKED", (await Api.GetAsync(BabiesPath(f.Baby), f.Owner.AccessToken)).Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(f.Baby), f.Caregiver.Session.AccessToken)).Status);
    }
}
