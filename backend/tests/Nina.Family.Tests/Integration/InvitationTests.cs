using System.Net;
using System.Text.Json.Nodes;
using Nina.Family.Tests.Infrastructure;

namespace Nina.Family.Tests.Integration;

/// <summary>RF-006/RF-007: convidar, prévia, aceitar, recusar, reenviar e cancelar (e-mail fake; nenhum envio real).</summary>
public sealed class InvitationTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    private async Task<(Session Owner, Guid Baby)> OwnerWithBabyAsync()
    {
        var owner = await NewOwnerAsync();
        return (owner, await CreateBabyAsync(owner, "Nina Secreta"));
    }

    [Fact]
    public async Task Owner_invites_by_email_and_the_invitee_sees_only_a_reduced_preview_before_accepting()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync("Vovó");
        var created = await InviteAsync(owner, baby, invitee.Email.ToUpperInvariant(), "READ_ONLY");

        Assert.Equal(HttpStatusCode.Created, created.Status);
        var membership = created.Json!;
        Assert.Equal("PENDING", membership["status"]!.GetValue<string>());
        Assert.Equal("READ_ONLY", membership["role"]!.GetValue<string>());
        Assert.Equal(invitee.Email, membership["invited_email"]!.GetValue<string>());
        Assert.Null(membership["user"]);
        Assert.NotNull(membership["invitation_expires_at"]);
        Assert.Null(membership["accepted_at"]);
        Assert.DoesNotContain("token", membership.ToJsonString(), StringComparison.Ordinal);

        // e-mail fake: um único envio, com token de >= 128 bits e sem o nome do bebê
        var sent = Factory.FamilyMailer.Sent.Single(m => m.To == invitee.Email);
        Assert.True(sent.Token.Length >= 43);
        Assert.Equal("Dona", sent.InviterDisplayName);
        Assert.DoesNotContain("Secreta", sent.ToString(), StringComparison.Ordinal);

        var preview = await Api.PostAsync("/v1/invitations/inspect", new JsonObject { ["token"] = sent.Token }, invitee.AccessToken);
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        Assert.Equal("Dona", preview.Json!["inviter_display_name"]!.GetValue<string>());
        Assert.Equal("N", preview.Json["baby_label"]!.GetValue<string>()); // só a inicial, nunca o nome completo (UX 4.7)
        Assert.Equal("READ_ONLY", preview.Json["role"]!.GetValue<string>());
        Assert.NotNull(preview.Json["expires_at"]);
        Assert.NotEmpty(preview.Json["visible_data"]!.AsArray());
        Assert.DoesNotContain("Secreta", preview.Json.ToJsonString(), StringComparison.Ordinal);

        // antes do aceite o convidado não enxerga o bebê (RB-006)
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync(BabiesPath(baby), invitee.AccessToken)).Status);
        Assert.Empty((await Api.GetAsync("/v1/babies", invitee.AccessToken)).Json!["items"]!.AsArray());
        // 7 dias (D-17)
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{baby}' AND status = 'PENDING' AND invite_expires_at BETWEEN now() + interval '6 days 23 hours' AND now() + interval '7 days 1 hour'"));
    }

    [Fact]
    public async Task Accept_activates_the_membership_returns_the_baby_and_burns_the_token()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var created = await InviteAsync(owner, baby, invitee.Email, "CAREGIVER");
        var token = LastInviteToken(invitee.Email);

        var accepted = await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken);
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal(baby.ToString(), accepted.Json!["id"]!.GetValue<string>());
        Assert.Equal("Nina Secreta", accepted.Json["display_name"]!.GetValue<string>());
        Assert.Equal("CAREGIVER", accepted.Json["my_role"]!.GetValue<string>());

        var membershipId = created.Json!["id"]!.GetValue<string>();
        Assert.Equal(1, await AdminCountAsync(
            $"SELECT count(*) FROM nina.caregiver_membership WHERE id = '{membershipId}' AND status = 'ACTIVE' AND user_id = '{invitee.UserId}' AND invite_token_hash IS NULL AND invited_email IS NULL"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.invitation_accepted' AND baby_id = '{baby}' AND actor_user_id = '{invitee.UserId}'"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.consent_record WHERE user_id = '{invitee.UserId}' AND purpose_key = 'caregiver_data_ack' AND subject_baby_id = '{baby}'"));

        // uso único
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/inspect", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);

        var list = await Api.GetAsync($"/v1/babies/{baby}/caregivers", owner.AccessToken);
        var active = list.Json!["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == membershipId)!;
        Assert.Equal("ACTIVE", active["status"]!.GetValue<string>());
        Assert.Equal(invitee.UserId.ToString(), active["user"]!["id"]!.GetValue<string>());
        Assert.NotNull(active["accepted_at"]);
    }

    [Fact]
    public async Task Token_of_someone_elses_invitation_is_a_uniform_404_and_the_legitimate_invitee_still_accepts()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var intruder = await NewUserAsync();
        await InviteAsync(owner, baby, invitee.Email);
        var token = LastInviteToken(invitee.Email);

        var wrong = await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, intruder.AccessToken);
        var unknown = await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = new string('z', 43) }, intruder.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, wrong.Status);
        Assert.Equal(unknown.Status, wrong.Status);
        Assert.Equal(unknown.Code, wrong.Code);
        Assert.Equal(unknown.Json!["title"]!.GetValue<string>(), wrong.Json!["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/inspect", new JsonObject { ["token"] = token }, intruder.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/decline", new JsonObject { ["token"] = token }, intruder.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync(BabiesPath(baby), intruder.AccessToken)).Status);

        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
    }

    [Fact]
    public async Task Repeated_attempts_by_the_wrong_account_expire_the_invitation()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var intruder = await NewUserAsync();
        await InviteAsync(owner, baby, invitee.Email);
        var token = LastInviteToken(invitee.Email);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, intruder.AccessToken)).Status);
        }

        // SEC-004: depois de 5 tentativas de destinatário errado o convite deixa de valer, até para o legítimo
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        var listed = await Api.GetAsync($"/v1/babies/{baby}/caregivers", owner.AccessToken);
        Assert.Contains(listed.Json!["items"]!.AsArray(), i => i!["status"]!.GetValue<string>() == "EXPIRED");
    }

    [Fact]
    public async Task Accept_without_the_current_terms_is_403_CONSENT_REQUIRED_and_keeps_the_invitation()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        await InviteAsync(owner, baby, invitee.Email);
        var token = LastInviteToken(invitee.Email);
        await AdminExecAsync(
            $"INSERT INTO nina.consent_record (user_id, purpose_key, policy_version, text_hash, locale, status, source) VALUES ('{invitee.UserId}', 'terms_of_use', '1.0.0', '{new string('b', 64)}', 'pt-BR', 'REVOKED', 'SETTINGS')");

        var denied = await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("CONSENT_REQUIRED", denied.Code);
        Assert.Equal("TERMS_OF_USE", denied.Json!["required_consents"]![0]!["purpose_key"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync(BabiesPath(baby), invitee.AccessToken)).Status);

        await AdminExecAsync(
            $"INSERT INTO nina.consent_record (user_id, purpose_key, policy_version, text_hash, locale, status, source) VALUES ('{invitee.UserId}', 'terms_of_use', '1.0.0', '{new string('c', 64)}', 'pt-BR', 'GRANTED', 'SETTINGS')");
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
    }

    [Fact]
    public async Task Decline_ends_the_invitation_for_good()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var created = await InviteAsync(owner, baby, invitee.Email);
        var token = LastInviteToken(invitee.Email);

        Assert.Equal(HttpStatusCode.NoContent, (await Api.PostAsync("/v1/invitations/decline", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/decline", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync(BabiesPath(baby), invitee.AccessToken)).Status);
        Assert.Equal("DECLINED", await AdminScalarAsync<string>($"SELECT status FROM nina.caregiver_membership WHERE id = '{created.Json!["id"]!.GetValue<string>()}'"));
        var listed = await Api.GetAsync($"/v1/babies/{baby}/caregivers", owner.AccessToken);
        Assert.DoesNotContain(listed.Json!["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == created.Json["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_expired_invitation_is_derived_EXPIRED_and_cannot_be_inspected_or_accepted()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var created = await InviteAsync(owner, baby, invitee.Email);
        var token = LastInviteToken(invitee.Email);
        await AdminMembershipAsync($"UPDATE nina.caregiver_membership SET invite_expires_at = now() - interval '1 minute' WHERE id = '{created.Json!["id"]!.GetValue<string>()}'");

        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/inspect", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        var listed = await Api.GetAsync($"/v1/babies/{baby}/caregivers", owner.AccessToken);
        Assert.Equal("EXPIRED", listed.Json!["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == created.Json["id"]!.GetValue<string>())!["status"]!.GetValue<string>());

        // convidar de novo o mesmo e-mail substitui o convite vencido
        var again = await InviteAsync(owner, baby, invitee.Email);
        Assert.Equal(HttpStatusCode.Created, again.Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = LastInviteToken(invitee.Email) }, invitee.AccessToken)).Status);
    }

    [Fact]
    public async Task Resend_rotates_the_token_and_the_previous_one_stops_working()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var created = await InviteAsync(owner, baby, invitee.Email, "READ_ONLY");
        var oldToken = LastInviteToken(invitee.Email);

        var resent = await Api.PostAsync($"/v1/babies/{baby}/invitations/{created.Json!["id"]!.GetValue<string>()}/resend", null, owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, resent.Status);
        Assert.Equal("PENDING", resent.Json!["status"]!.GetValue<string>());
        Assert.Equal("READ_ONLY", resent.Json["role"]!.GetValue<string>());
        Assert.Equal(invitee.Email, resent.Json["invited_email"]!.GetValue<string>());

        var newToken = LastInviteToken(invitee.Email);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(2, Factory.FamilyMailer.Sent.Count(m => m.To == invitee.Email));
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = oldToken }, invitee.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = newToken }, invitee.AccessToken)).Status);

        // só convite pendente pode ser reenviado
        var active = await Api.PostAsync($"/v1/babies/{baby}/invitations/{resent.Json["id"]!.GetValue<string>()}/resend", null, owner.AccessToken);
        Assert.Equal(HttpStatusCode.Conflict, active.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync($"/v1/babies/{baby}/invitations/{Guid.NewGuid()}/resend", null, owner.AccessToken)).Status);
    }

    [Fact]
    public async Task Resend_renews_the_expiration_of_an_expired_invitation()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var created = await InviteAsync(owner, baby, invitee.Email);
        await AdminMembershipAsync($"UPDATE nina.caregiver_membership SET invite_expires_at = now() - interval '1 day' WHERE id = '{created.Json!["id"]!.GetValue<string>()}'");

        var resent = await Api.PostAsync($"/v1/babies/{baby}/invitations/{created.Json!["id"]!.GetValue<string>()}/resend", null, owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, resent.Status);
        Assert.Equal("PENDING", resent.Json!["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = LastInviteToken(invitee.Email) }, invitee.AccessToken)).Status);
    }

    [Fact]
    public async Task Cancelling_a_pending_invitation_invalidates_its_token()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var invitee = await NewUserAsync();
        var created = await InviteAsync(owner, baby, invitee.Email);
        var token = LastInviteToken(invitee.Email);

        var cancelled = await Api.DeleteAsync($"/v1/babies/{baby}/caregivers/{created.Json!["id"]!.GetValue<string>()}", owner.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/inspect", new JsonObject { ["token"] = token }, invitee.AccessToken)).Status);
        Assert.DoesNotContain(
            (await Api.GetAsync($"/v1/babies/{baby}/caregivers", owner.AccessToken)).Json!["items"]!.AsArray(),
            i => i!["role"]!.GetValue<string>() != "OWNER");
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"/v1/babies/{baby}/caregivers/{created.Json["id"]!.GetValue<string>()}", owner.AccessToken)).Status); // idempotente
    }

    [Fact]
    public async Task Inviting_someone_already_linked_or_already_invited_is_409_ALREADY_MEMBER_without_a_second_link()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var member = await JoinAsync(owner, baby);
        var pending = await NewUserAsync();
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, baby, pending.Email)).Status);

        foreach (var email in new[] { member.Session.Email, pending.Email, owner.Email })
        {
            var again = await InviteAsync(owner, baby, email);
            Assert.Equal(HttpStatusCode.Conflict, again.Status);
            Assert.Equal("ALREADY_MEMBER", again.Code);
        }

        Assert.Equal(3, await AdminCountAsync($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{baby}' AND status IN ('ACTIVE', 'PENDING')"));
    }

    [Fact]
    public async Task Inviting_an_email_without_an_account_gives_the_same_response_as_with_one()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var registered = await NewUserAsync();
        var withAccount = await InviteAsync(owner, baby, registered.Email);
        var withoutAccount = await InviteAsync(owner, baby, ApiClient.NewEmail());

        Assert.Equal(HttpStatusCode.Created, withAccount.Status);
        Assert.Equal(withAccount.Status, withoutAccount.Status);
        Assert.Equal(
            withAccount.Json!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal),
            withoutAccount.Json!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Null(withAccount.Json["user"]);
        Assert.Null(withoutAccount.Json["user"]);
    }

    [Fact]
    public async Task Invitation_input_is_validated()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var path = $"/v1/babies/{baby}/invitations";
        Assert.Equal("INVALID_FORMAT", (await Api.PostAsync(path, new JsonObject { ["email"] = "not-an-email", ["role"] = "CAREGIVER" }, owner.AccessToken)).FieldErrorCode("email"));
        Assert.Equal("REQUIRED", (await Api.PostAsync(path, new JsonObject { ["role"] = "CAREGIVER" }, owner.AccessToken)).FieldErrorCode("email"));
        Assert.Equal("UNSUPPORTED_VALUE", (await Api.PostAsync(path, new JsonObject { ["email"] = "a@example.org", ["role"] = "OWNER" }, owner.AccessToken)).FieldErrorCode("role"));
        Assert.Equal("REQUIRED", (await Api.PostAsync(path, new JsonObject { ["email"] = "a@example.org" }, owner.AccessToken)).FieldErrorCode("role"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.PostAsync(path, new JsonObject { ["email"] = "a@example.org", ["role"] = "CAREGIVER", ["extra"] = 1 }, owner.AccessToken)).Status);
        Assert.Equal("TOO_SHORT", (await Api.PostAsync("/v1/invitations/inspect", new JsonObject { ["token"] = "curto" }, owner.AccessToken)).FieldErrorCode("token"));
        Assert.Equal("TOO_LONG", (await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = new string('a', 513) }, owner.AccessToken)).FieldErrorCode("token"));
        Assert.Equal("REQUIRED", (await Api.PostAsync("/v1/invitations/decline", new JsonObject(), owner.AccessToken)).FieldErrorCode("token"));
        Assert.Empty(Factory.FamilyMailer.Sent);
    }

    [Fact]
    public async Task Only_the_owner_invites_or_resends()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var caregiver = await JoinAsync(owner, baby, "CAREGIVER");
        var readOnly = await JoinAsync(owner, baby, "READ_ONLY");
        var pending = await InviteAsync(owner, baby, ApiClient.NewEmail());

        foreach (var member in new[] { caregiver, readOnly })
        {
            var invite = await InviteAsync(member.Session, baby, ApiClient.NewEmail());
            Assert.Equal(HttpStatusCode.Forbidden, invite.Status);
            Assert.Equal("FORBIDDEN_ROLE", invite.Code);
            var resend = await Api.PostAsync($"/v1/babies/{baby}/invitations/{pending.Json!["id"]!.GetValue<string>()}/resend", null, member.Session.AccessToken);
            Assert.Equal("FORBIDDEN_ROLE", resend.Code);
        }
    }

    [Fact]
    public async Task Daily_invitation_limit_of_the_database_is_a_429_QUOTA_EXCEEDED()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        await AdminExecAsync($"SELECT set_config('nina.user_id', '{owner.UserId}', true); UPDATE nina.app_parameter SET value = '2'::jsonb WHERE param_key = 'invites.max_per_day'");
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, baby, ApiClient.NewEmail())).Status);
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, baby, ApiClient.NewEmail())).Status);

        var third = await InviteAsync(owner, baby, ApiClient.NewEmail());
        Assert.Equal(HttpStatusCode.TooManyRequests, third.Status);
        Assert.Equal("QUOTA_EXCEEDED", third.Code);
        Assert.NotNull(third.Header("Retry-After"));
        Assert.Equal(2, Factory.FamilyMailer.Sent.Count);
    }

    [Fact]
    public async Task Invitation_with_an_idempotency_key_is_created_and_mailed_once()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var email = ApiClient.NewEmail();
        var key = ApiClient.Header("Idempotency-Key", Guid.NewGuid().ToString());
        var body = new JsonObject { ["email"] = email, ["role"] = "CAREGIVER" };

        var first = await Api.PostAsync($"/v1/babies/{baby}/invitations", body, owner.AccessToken, key);
        var second = await Api.PostAsync($"/v1/babies/{baby}/invitations", body, owner.AccessToken, key);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal("true", second.Header("Idempotent-Replayed"));
        Assert.Equal(first.Json!["id"]!.GetValue<string>(), second.Json!["id"]!.GetValue<string>());
        Assert.Single(Factory.FamilyMailer.Sent);

        var different = await Api.PostAsync($"/v1/babies/{baby}/invitations", new JsonObject { ["email"] = email, ["role"] = "READ_ONLY" }, owner.AccessToken, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REUSE", different.Code);
    }

    [Fact]
    public async Task A_failed_idempotent_request_is_not_cached()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var key = ApiClient.Header("Idempotency-Key", Guid.NewGuid().ToString());
        var body = new JsonObject { ["email"] = owner.Email, ["role"] = "CAREGIVER" };
        Assert.Equal(HttpStatusCode.Conflict, (await Api.PostAsync($"/v1/babies/{baby}/invitations", body, owner.AccessToken, key)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Api.PostAsync($"/v1/babies/{baby}/invitations", body, owner.AccessToken, key)).Status);
    }

    [Fact]
    public async Task A_removed_caregiver_can_be_invited_again_and_gets_a_fresh_link()
    {
        var (owner, baby) = await OwnerWithBabyAsync();
        var member = await JoinAsync(owner, baby);
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync($"/v1/babies/{baby}/caregivers/{member.MembershipId}", owner.AccessToken)).Status);
        Assert.Equal("ACCESS_REVOKED", (await Api.GetAsync(BabiesPath(baby), member.Session.AccessToken)).Code);

        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, baby, member.Session.Email, "READ_ONLY")).Status);
        var accepted = await Api.PostAsync("/v1/invitations/accept", new JsonObject { ["token"] = LastInviteToken(member.Session.Email) }, member.Session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal("READ_ONLY", accepted.Json!["my_role"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(baby), member.Session.AccessToken)).Status);
    }
}
