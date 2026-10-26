using System.Net;
using System.Text.Json.Nodes;
using Nina.Family.Tests.Infrastructure;

namespace Nina.Family.Tests.Integration;

/// <summary>RF-004/RF-005: perfil do bebê (CRUD), idade derivada, data prevista separada, só o Owner edita, ETag/If-Match.</summary>
public sealed class BabyTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    [Fact]
    public async Task Create_makes_the_creator_an_active_owner_with_derived_age_and_version_1()
    {
        var owner = await NewOwnerAsync();
        var created = await Api.PostAsync("/v1/babies", new JsonObject
        {
            ["display_name"] = "  Nina ",
            ["birth_date"] = TodayUtc(90),
            ["due_date"] = TodayUtc(76),
            ["timezone"] = "UTC",
        }, owner.AccessToken);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        var baby = created.Json!;
        Assert.Equal("Nina", baby["display_name"]!.GetValue<string>());
        Assert.Equal("OWNER", baby["my_role"]!.GetValue<string>());
        Assert.Equal(1, baby["version"]!.GetValue<int>());
        Assert.Equal(TodayUtc(90), baby["birth_date"]!.GetValue<string>());
        Assert.Equal(TodayUtc(76), baby["due_date"]!.GetValue<string>());
        Assert.Null(baby["sex"]);
        Assert.Null(baby["photo_ref"]);
        Assert.EndsWith("Z", baby["created_at"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(90, baby["age_calculation"]!["chronological_days"]!.GetValue<int>());
        Assert.Equal(76, baby["age_calculation"]!["corrected_days"]!.GetValue<int>());
        Assert.True(baby["age_calculation"]!["correction_applied"]!.GetValue<bool>());
        Assert.Equal(76, baby["age"]!["corrected"]!["days"]!.GetValue<int>());
        Assert.Equal(10, baby["age"]!["corrected"]!["weeks"]!.GetValue<int>());
        Assert.Equal(12, baby["age"]!["chronological"]!["weeks"]!.GetValue<int>());
        Assert.Equal(TodayUtc(), baby["age"]!["as_of"]!.GetValue<string>());
        Assert.Equal("CORRECTED", baby["age"]!["displayed"]!.GetValue<string>());

        var id = Guid.Parse(baby["id"]!.GetValue<string>());
        Assert.Equal(1, await AdminCountAsync(
            $"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{id}' AND user_id = '{owner.UserId}' AND role = 'OWNER' AND status = 'ACTIVE'"));
    }

    [Fact]
    public async Task Age_is_null_for_correction_when_there_is_no_due_date_or_it_is_not_after_birth_or_outside_the_window()
    {
        var owner = await NewOwnerAsync();

        var noDue = (await Api.GetAsync(BabiesPath(await CreateBabyAsync(owner, "Sem DPP")), owner.AccessToken)).Json!;
        Assert.Null(noDue["due_date"]);
        Assert.Null(noDue["age_calculation"]!["corrected_days"]);
        Assert.False(noDue["age_calculation"]!["correction_applied"]!.GetValue<bool>());
        Assert.Null(noDue["age"]!["corrected"]);
        Assert.Equal("CHRONOLOGICAL", noDue["age"]!["displayed"]!.GetValue<string>());

        var dueBefore = (await Api.GetAsync(BabiesPath(await CreateBabyAsync(owner, "DPP antes", due: TodayUtc(93))), owner.AccessToken)).Json!;
        Assert.Null(dueBefore["age_calculation"]!["corrected_days"]);
        Assert.False(dueBefore["age_calculation"]!["correction_applied"]!.GetValue<bool>());

        var old = (await Api.GetAsync(BabiesPath(await CreateBabyAsync(owner, "Antigo", birth: TodayUtc(800), due: TodayUtc(786))), owner.AccessToken)).Json!;
        Assert.Equal(800, old["age_calculation"]!["chronological_days"]!.GetValue<int>());
        Assert.False(old["age_calculation"]!["correction_applied"]!.GetValue<bool>());
        Assert.Null(old["age"]!["corrected"]);
    }

    [Fact]
    public async Task Create_requires_the_guardian_declaration_and_validates_the_input()
    {
        var user = await NewUserAsync();
        var noConsent = await Api.PostAsync("/v1/babies", new JsonObject { ["display_name"] = "Nina", ["birth_date"] = TodayUtc(1), ["timezone"] = "UTC" }, user.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, noConsent.Status);
        Assert.Equal("CONSENT_REQUIRED", noConsent.Code);
        Assert.Equal("CHILD_DATA_GUARDIAN", noConsent.Json!["required_consents"]![0]!["purpose_key"]!.GetValue<string>());
        Assert.Equal(0, await AdminCountAsync("SELECT count(*) FROM nina.baby"));

        await GrantGuardianConsentAsync(user.UserId);
        async Task<ApiResponse> Post(JsonObject body) => await Api.PostAsync("/v1/babies", body, user.AccessToken);

        var future = await Post(new JsonObject { ["display_name"] = "N", ["birth_date"] = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"), ["timezone"] = "UTC" });
        Assert.Equal(HttpStatusCode.BadRequest, future.Status);
        Assert.Equal("IN_FUTURE", future.FieldErrorCode("birth_date"));

        var missing = await Post(new JsonObject { ["timezone"] = "UTC" });
        Assert.Equal("VALIDATION_FAILED", missing.Code);
        Assert.Equal("REQUIRED", missing.FieldErrorCode("display_name"));
        Assert.Equal("REQUIRED", missing.FieldErrorCode("birth_date"));

        Assert.Equal("TOO_LONG", (await Post(new JsonObject { ["display_name"] = new string('x', 61), ["birth_date"] = TodayUtc(1) })).FieldErrorCode("display_name"));
        Assert.Equal("REQUIRED", (await Post(new JsonObject { ["display_name"] = "   ", ["birth_date"] = TodayUtc(1) })).FieldErrorCode("display_name"));
        Assert.Equal("INVALID_VALUE", (await Post(new JsonObject { ["display_name"] = "N", ["birth_date"] = TodayUtc(1), ["timezone"] = "Mars/Phobos" })).FieldErrorCode("timezone"));
        Assert.Equal("INVALID_FORMAT", (await Post(new JsonObject { ["display_name"] = "N", ["birth_date"] = TodayUtc(1), ["timezone"] = "'; DROP TABLE x;--" })).FieldErrorCode("timezone"));
        Assert.Equal("UNSUPPORTED_VALUE", (await Post(new JsonObject { ["display_name"] = "N", ["birth_date"] = TodayUtc(1), ["sex"] = "MARTIAN" })).FieldErrorCode("sex"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new JsonObject { ["display_name"] = "N", ["birth_date"] = "2026-13-45" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new JsonObject { ["display_name"] = "N", ["birth_date"] = TodayUtc(1), ["family_id"] = Guid.NewGuid().ToString() })).Status);
        Assert.Equal(0, await AdminCountAsync("SELECT count(*) FROM nina.baby"));
    }

    [Fact]
    public async Task Create_without_timezone_uses_the_profile_timezone_and_sex_is_stored_only_when_sent()
    {
        var owner = await NewOwnerAsync();
        var created = await Api.PostAsync("/v1/babies", new JsonObject { ["display_name"] = "Nina", ["birth_date"] = TodayUtc(10), ["sex"] = "UNSPECIFIED" }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("America/Sao_Paulo", created.Json!["timezone"]!.GetValue<string>());
        Assert.Equal("UNSPECIFIED", created.Json["sex"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_client_generated_id_is_honoured_replayed_for_the_owner_and_never_reused_across_tenants()
    {
        var alice = await NewOwnerAsync();
        var bob = await NewOwnerAsync();
        var id = Guid.NewGuid();
        var body = new JsonObject { ["id"] = id.ToString(), ["display_name"] = "Nina", ["birth_date"] = TodayUtc(5), ["timezone"] = "UTC" };

        var first = await Api.PostAsync("/v1/babies", body, alice.AccessToken);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(id.ToString(), first.Json!["id"]!.GetValue<string>());

        var replay = await Api.PostAsync("/v1/babies", body, alice.AccessToken);
        Assert.Equal(HttpStatusCode.Created, replay.Status);
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.baby WHERE id = '{id}'"));

        var changed = (JsonObject)JsonNode.Parse(body.ToJsonString())!;
        changed["display_name"] = "Outro";
        Assert.Equal("ENTITY_ID_UNAVAILABLE", (await Api.PostAsync("/v1/babies", changed, alice.AccessToken)).FieldErrorCode("id"));

        // Outro tenant usando o mesmo id: mesma resposta, sem revelar que o id já existe e sem tocar no bebê da Alice.
        var stolen = await Api.PostAsync("/v1/babies", body, bob.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.Status);
        Assert.Equal("ENTITY_ID_UNAVAILABLE", stolen.FieldErrorCode("id"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{id}'"));
    }

    [Fact]
    public async Task Idempotency_key_replays_the_first_response_and_rejects_a_different_payload()
    {
        var owner = await NewOwnerAsync();
        var key = Guid.NewGuid().ToString();
        var body = new JsonObject { ["display_name"] = "Nina", ["birth_date"] = TodayUtc(5), ["timezone"] = "UTC" };
        var first = await Api.PostAsync("/v1/babies", body, owner.AccessToken, ApiClient.Header("Idempotency-Key", key));
        var second = await Api.PostAsync("/v1/babies", body, owner.AccessToken, ApiClient.Header("Idempotency-Key", key));

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal("false", first.Header("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal("true", second.Header("Idempotent-Replayed"));
        Assert.Equal(first.Json!["id"]!.GetValue<string>(), second.Json!["id"]!.GetValue<string>());
        Assert.Equal(1, await AdminCountAsync("SELECT count(*) FROM nina.baby"));

        var other = (JsonObject)JsonNode.Parse(body.ToJsonString())!;
        other["display_name"] = "Diferente";
        var reuse = await Api.PostAsync("/v1/babies", other, owner.AccessToken, ApiClient.Header("Idempotency-Key", key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reuse.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REUSE", reuse.Code);

        Assert.Equal(HttpStatusCode.BadRequest, (await Api.PostAsync("/v1/babies", body, owner.AccessToken, ApiClient.Header("Idempotency-Key", "not-a-uuid"))).Status);
        Assert.Equal(1, await AdminCountAsync("SELECT count(*) FROM nina.baby"));
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_by_user()
    {
        var alice = await NewOwnerAsync();
        var bob = await NewOwnerAsync();
        var key = Guid.NewGuid().ToString();
        var body = new JsonObject { ["display_name"] = "Nina", ["birth_date"] = TodayUtc(5), ["timezone"] = "UTC" };
        var a = await Api.PostAsync("/v1/babies", body, alice.AccessToken, ApiClient.Header("Idempotency-Key", key));
        var b = await Api.PostAsync("/v1/babies", body, bob.AccessToken, ApiClient.Header("Idempotency-Key", key));
        Assert.NotEqual(a.Json!["id"]!.GetValue<string>(), b.Json!["id"]!.GetValue<string>());
        Assert.Equal("false", b.Header("Idempotent-Replayed"));
    }

    [Fact]
    public async Task List_returns_only_the_users_babies_with_their_role_and_siblings_coexist()
    {
        var alice = await NewOwnerAsync();
        var bob = await NewOwnerAsync();
        var first = await CreateBabyAsync(alice, "Primeira");
        var second = await CreateBabyAsync(alice, "Segunda");
        var bobs = await CreateBabyAsync(bob, "Do Bob");
        var carol = await JoinAsync(bob, bobs, "READ_ONLY");

        var list = await Api.GetAsync("/v1/babies", alice.AccessToken);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal([first.ToString(), second.ToString()], list.Json!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToArray());
        Assert.All(list.Json["items"]!.AsArray(), i => Assert.Equal("OWNER", i!["my_role"]!.GetValue<string>()));

        var carols = await Api.GetAsync("/v1/babies", carol.Session.AccessToken);
        Assert.Equal([bobs.ToString()], carols.Json!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal("READ_ONLY", carols.Json["items"]![0]!["my_role"]!.GetValue<string>());

        var empty = await Api.GetAsync("/v1/babies", (await NewUserAsync()).AccessToken);
        Assert.Empty(empty.Json!["items"]!.AsArray());
    }

    [Fact]
    public async Task Get_returns_the_baby_with_an_etag_for_every_active_role()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        var caregiver = await JoinAsync(owner, baby, "CAREGIVER");
        var readOnly = await JoinAsync(owner, baby, "READ_ONLY");

        foreach (var (session, role) in new[] { (owner, "OWNER"), (caregiver.Session, "CAREGIVER"), (readOnly.Session, "READ_ONLY") })
        {
            var response = await Api.GetAsync(BabiesPath(baby), session.AccessToken);
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Equal(role, response.Json!["my_role"]!.GetValue<string>());
            Assert.Equal("\"1\"", response.Header("ETag"));
        }
    }

    [Fact]
    public async Task Update_is_owner_only_merge_patch_that_bumps_the_version_and_keeps_birth_and_due_dates_independent()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner, birth: TodayUtc(90), due: TodayUtc(76));

        var renamed = await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = "Ninoca" }, "\"1\"");
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal("Ninoca", renamed.Json!["display_name"]!.GetValue<string>());
        Assert.Equal(2, renamed.Json["version"]!.GetValue<int>());
        Assert.Equal("\"2\"", renamed.Header("ETag"));
        Assert.Equal(TodayUtc(90), renamed.Json["birth_date"]!.GetValue<string>());
        Assert.Equal(TodayUtc(76), renamed.Json["due_date"]!.GetValue<string>());

        // RF-004-A3 / RB-004: editar a DPP não altera o nascimento; null limpa a DPP e a idade corrigida some.
        var cleared = await ChangeBabyAsync(owner, baby, new JsonObject { ["due_date"] = null });
        Assert.Null(cleared.Json!["due_date"]);
        Assert.Equal(TodayUtc(90), cleared.Json["birth_date"]!.GetValue<string>());
        Assert.Null(cleared.Json["age_calculation"]!["corrected_days"]);
        Assert.Equal(3, cleared.Json["version"]!.GetValue<int>());

        var moved = await ChangeBabyAsync(owner, baby, new JsonObject { ["birth_date"] = TodayUtc(60), ["due_date"] = TodayUtc(50), ["timezone"] = "America/Sao_Paulo" });
        Assert.InRange(moved.Json!["age_calculation"]!["chronological_days"]!.GetValue<int>(), 59, 60); // data local do bebê (fuso novo)
        Assert.Equal("America/Sao_Paulo", moved.Json["timezone"]!.GetValue<string>());

        var persisted = await Api.GetAsync(BabiesPath(baby), owner.AccessToken);
        Assert.Equal(TodayUtc(60), persisted.Json!["birth_date"]!.GetValue<string>());
        Assert.Equal(TodayUtc(50), persisted.Json["due_date"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_with_a_stale_if_match_fails_with_412_and_changes_nothing()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        Assert.Equal(HttpStatusCode.OK, (await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = "A" }, "\"1\"")).Status);

        var stale = await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = "B" }, "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal("VERSION_CONFLICT", stale.Code);
        Assert.Equal("A", (await Api.GetAsync(BabiesPath(baby), owner.AccessToken)).Json!["display_name"]!.GetValue<string>());

        // sem If-Match é aceito (PA-12); formato inválido é 400
        Assert.Equal(HttpStatusCode.OK, (await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = "C" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = "D" }, "abc")).Status);
    }

    [Fact]
    public async Task Update_validates_the_patch_body_and_media_type()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);

        Assert.Equal("UNKNOWN_FIELD", (await ChangeBabyAsync(owner, baby, new JsonObject { ["family_id"] = Guid.NewGuid().ToString() })).FieldErrorCode("family_id"));
        Assert.Equal("UNKNOWN_FIELD", (await ChangeBabyAsync(owner, baby, new JsonObject { ["version"] = 99 })).FieldErrorCode("version"));
        Assert.Equal("REQUIRED", (await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = null })).FieldErrorCode("display_name"));
        Assert.Equal("REQUIRED", (await ChangeBabyAsync(owner, baby, new JsonObject { ["birth_date"] = null })).FieldErrorCode("birth_date"));
        Assert.Equal("INVALID_FORMAT", (await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = 5 })).FieldErrorCode("display_name"));
        Assert.Equal("INVALID_FORMAT", (await ChangeBabyAsync(owner, baby, new JsonObject { ["due_date"] = "ontem" })).FieldErrorCode("due_date"));
        Assert.Equal("TOO_LONG", (await ChangeBabyAsync(owner, baby, new JsonObject { ["display_name"] = new string('n', 61) })).FieldErrorCode("display_name"));
        Assert.Equal("INVALID_VALUE", (await ChangeBabyAsync(owner, baby, new JsonObject { ["timezone"] = "Nowhere/City" })).FieldErrorCode("timezone"));
        Assert.Equal("IN_FUTURE", (await ChangeBabyAsync(owner, baby, new JsonObject { ["birth_date"] = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd") })).FieldErrorCode("birth_date"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Api.SendRawAsync(HttpMethod.Patch, BabiesPath(baby), "{}", "text/plain", owner.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.SendRawAsync(HttpMethod.Patch, BabiesPath(baby), "{not json", "application/merge-patch+json", owner.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Api.SendRawAsync(HttpMethod.Patch, BabiesPath(baby), "[1]", "application/json", owner.AccessToken)).Status);

        var empty = await ChangeBabyAsync(owner, baby, []);
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Equal(1, empty.Json!["version"]!.GetValue<int>());
        Assert.Equal("Nina", (await Api.GetAsync(BabiesPath(baby), owner.AccessToken)).Json!["display_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_is_denied_to_caregiver_and_read_only_with_403_FORBIDDEN_ROLE()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        var caregiver = await JoinAsync(owner, baby, "CAREGIVER");
        var readOnly = await JoinAsync(owner, baby, "READ_ONLY");

        foreach (var member in new[] { caregiver, readOnly })
        {
            var denied = await ChangeBabyAsync(member.Session, baby, new JsonObject { ["display_name"] = "Hack" });
            Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
            Assert.Equal("FORBIDDEN_ROLE", denied.Code);
        }

        Assert.Equal("Nina", (await Api.GetAsync(BabiesPath(baby), owner.AccessToken)).Json!["display_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_requires_a_baby_delete_reauth_token_and_removes_the_baby_through_the_database_function()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);

        var none = await Api.DeleteAsync(BabiesPath(baby), owner.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, none.Status);
        Assert.Equal("REAUTH_REQUIRED", none.Code);

        var wrongScope = await Api.DeleteAsync(BabiesPath(baby), owner.AccessToken, ApiClient.Header("X-Reauth-Token", await Api.ReauthAsync(owner, "OWNERSHIP_TRANSFER")));
        Assert.Equal("REAUTH_REQUIRED", wrongScope.Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(baby), owner.AccessToken)).Status);

        var token = await Api.ReauthAsync(owner, "BABY_DELETE");
        var deleted = await Api.DeleteAsync(BabiesPath(baby), owner.AccessToken, ApiClient.Header("X-Reauth-Token", token));
        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync(BabiesPath(baby), owner.AccessToken)).Status);
        Assert.Empty((await Api.GetAsync("/v1/babies", owner.AccessToken)).Json!["items"]!.AsArray());
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.deleted_by_owner' AND baby_id = '{baby}' AND actor_user_id = '{owner.UserId}'"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.baby WHERE id = '{baby}' AND deleted_at IS NOT NULL AND display_name IS NULL"));

        // O token é de uso único: não serve para outro bebê.
        var other = await CreateBabyAsync(owner, "Outro");
        var reused = await Api.DeleteAsync(BabiesPath(other), owner.AccessToken, ApiClient.Header("X-Reauth-Token", token));
        Assert.Equal("REAUTH_REQUIRED", reused.Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(other), owner.AccessToken)).Status);
    }

    [Fact]
    public async Task Delete_of_a_shared_baby_needs_the_acknowledgement_and_notifies_every_active_caregiver()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        var caregiver = await JoinAsync(owner, baby, "CAREGIVER");
        var token = await Api.ReauthAsync(owner, "BABY_DELETE");
        var headers = ApiClient.Header("X-Reauth-Token", token);

        var noAck = await Api.DeleteAsync(BabiesPath(baby), owner.AccessToken, headers);
        Assert.Equal(HttpStatusCode.Conflict, noAck.Status);
        Assert.Equal("OWNER_DECISION_REQUIRED", noAck.Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(baby), caregiver.Session.AccessToken)).Status);

        // O 409 não consumiu a reautenticação: o mesmo token vale quando o Owner confirma.
        var acked = await Api.DeleteAsync(BabiesPath(baby) + "?acknowledge_other_caregivers=true", owner.AccessToken, headers);
        Assert.Equal(HttpStatusCode.NoContent, acked.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync(BabiesPath(baby), caregiver.Session.AccessToken)).Status);
        Assert.Equal(2, await AdminCountAsync($"SELECT count(*) FROM nina.outbox_message WHERE event_type = 'SharedBabyDeletedNotice' AND payload->>'baby_id' = '{baby}'"));
        Assert.Equal(1, await AdminCountAsync($"SELECT count(*) FROM nina.outbox_message WHERE event_type = 'BabyDeleted' AND aggregate_id = '{baby}'"));
    }

    [Fact]
    public async Task Delete_is_owner_only_and_checks_access_before_touching_the_reauth_token()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        var caregiver = await JoinAsync(owner, baby, "CAREGIVER");
        var stranger = await NewUserAsync();

        var asCaregiver = await Api.DeleteAsync(BabiesPath(baby), caregiver.Session.AccessToken, ApiClient.Header("X-Reauth-Token", await Api.ReauthAsync(caregiver.Session, "BABY_DELETE")));
        Assert.Equal(HttpStatusCode.Forbidden, asCaregiver.Status);
        Assert.Equal("FORBIDDEN_ROLE", asCaregiver.Code);

        var strangerToken = await Api.ReauthAsync(stranger, "BABY_DELETE");
        var asStranger = await Api.DeleteAsync(BabiesPath(baby), stranger.AccessToken, ApiClient.Header("X-Reauth-Token", strangerToken));
        Assert.Equal(HttpStatusCode.NotFound, asStranger.Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync(BabiesPath(baby), owner.AccessToken)).Status);

        // o token do estranho não foi gasto
        await GrantGuardianConsentAsync(stranger.UserId);
        var own = await CreateBabyAsync(stranger, "Dele");
        Assert.Equal(HttpStatusCode.NoContent, (await Api.DeleteAsync(BabiesPath(own), stranger.AccessToken, ApiClient.Header("X-Reauth-Token", strangerToken))).Status);
    }

    [Fact]
    public async Task Endpoints_require_authentication()
    {
        var unauthorized = await Api.GetAsync("/v1/babies");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.Status);
        Assert.True(unauthorized.IsProblem);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.PostAsync("/v1/babies", new JsonObject())).Status);
    }
}
