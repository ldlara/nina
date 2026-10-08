namespace Nina.Database.Tests.Infrastructure;

/// <summary>
/// Cenário base (montado pelo dono, ignorando RLS): Alice (Owner do bebê A, família A), Bob (Owner do bebê B, família B),
/// Carol (READ_ONLY em A), Erin (CAREGIVER em A) e Dave (sem vínculo nenhum). Um evento de sono do Bob no bebê B.
/// </summary>
public sealed class World
{
    public Guid Alice { get; } = Guid.NewGuid();

    public Guid Bob { get; } = Guid.NewGuid();

    public Guid Carol { get; } = Guid.NewGuid();

    public Guid Dave { get; } = Guid.NewGuid();

    public Guid Erin { get; } = Guid.NewGuid();

    public Guid FamilyA { get; } = Guid.NewGuid();

    public Guid FamilyB { get; } = Guid.NewGuid();

    public Guid BabyA { get; } = Guid.NewGuid();

    public Guid BabyB { get; } = Guid.NewGuid();

    public Guid SleepB { get; } = Guid.NewGuid();

    public Guid MembershipAlice { get; } = Guid.NewGuid();

    public Guid MembershipBob { get; } = Guid.NewGuid();

    public Guid MembershipCarol { get; } = Guid.NewGuid();

    public Guid MembershipErin { get; } = Guid.NewGuid();

    public static async Task<World> CreateAsync(DbTestBase db)
    {
        var w = new World();
        await db.OwnerAsync(
            $"""
            INSERT INTO nina.app_user (id, email, email_verified_at, display_name, locale, timezone) VALUES
              ('{w.Alice}', 'alice@example.org', now(), 'Alice', 'pt-BR', 'America/Sao_Paulo'),
              ('{w.Bob}',   'bob@example.org',   now(), 'Bob',   'pt-BR', 'America/Sao_Paulo'),
              ('{w.Carol}', 'carol@example.org', now(), 'Carol', 'pt-BR', 'America/Sao_Paulo'),
              ('{w.Dave}',  'dave@example.org',  now(), 'Dave',  'pt-BR', 'America/Sao_Paulo'),
              ('{w.Erin}',  'erin@example.org',  now(), 'Erin',  'pt-BR', 'America/Sao_Paulo');
            INSERT INTO nina.family (id, owner_user_id) VALUES ('{w.FamilyA}', '{w.Alice}'), ('{w.FamilyB}', '{w.Bob}');
            INSERT INTO nina.baby (id, family_id, display_name, birth_date, timezone, created_by, last_modified_by) VALUES
              ('{w.BabyA}', '{w.FamilyA}', 'BebeAlice', current_date - 40, 'America/Sao_Paulo', '{w.Alice}', '{w.Alice}'),
              ('{w.BabyB}', '{w.FamilyB}', 'BebeBob',   current_date - 60, 'America/Sao_Paulo', '{w.Bob}', '{w.Bob}');
            INSERT INTO nina.caregiver_membership (id, baby_id, user_id, role, status, accepted_at) VALUES
              ('{w.MembershipAlice}', '{w.BabyA}', '{w.Alice}', 'OWNER',     'ACTIVE', now()),
              ('{w.MembershipBob}',   '{w.BabyB}', '{w.Bob}',   'OWNER',     'ACTIVE', now()),
              ('{w.MembershipCarol}', '{w.BabyA}', '{w.Carol}', 'READ_ONLY', 'ACTIVE', now()),
              ('{w.MembershipErin}',  '{w.BabyA}', '{w.Erin}',  'CAREGIVER', 'ACTIVE', now());
            INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source, notes, created_by, last_modified_by)
              VALUES ('{w.SleepB}', '{w.BabyB}', now() - interval '3 hours', now() - interval '2 hours', 'UTC', 'NAP', 'MANUAL', 'nota privada do Bob', '{w.Bob}', '{w.Bob}');
            """);
        return w;
    }
}
