-- =============================================================================
-- Extras do spike ARCH-003 (NÃO fazem parte da migração 0001_init.sql).
-- Aplicados em cima de 0001 só no PostgreSQL temporário do spike. Cada objeto aqui
-- é uma RECOMENDAÇÃO para o BE-004/DB (ver specs/sync-spike.md, seção de divergências).
-- =============================================================================
BEGIN;

-- Logins do spike: a aplicação NUNCA conecta como superuser nem dono (RLS seria ignorada).
CREATE ROLE spike_app    LOGIN IN ROLE nina_app;
CREATE ROLE spike_worker LOGIN IN ROLE nina_worker;
CREATE ROLE spike_cfg    LOGIN IN ROLE nina_config_admin;

CREATE SCHEMA nina_spike;
GRANT USAGE ON SCHEMA nina_spike TO nina_app, nina_worker;

-- (1) Relógio por campo para LWW por campo. O banco só tem `version` por entidade, o que não
--     permite saber QUAIS campos mudaram depois de `base_version`. Recomendação: coluna
--     `field_clock jsonb` na própria entidade (some junto com a purga) ou esta tabela.
CREATE TABLE nina_spike.field_clock (
  entity_id   uuid        NOT NULL,
  field       text        NOT NULL,
  baby_id     uuid        NOT NULL,
  ts          timestamptz NOT NULL,   -- instante LWW efetivo (min(client_created_at, recebido))
  version     bigint      NOT NULL,   -- versão da entidade que gravou este campo
  user_id     uuid,
  device_id   uuid        NOT NULL,   -- 00000000-... = servidor (ex.: fechamento automático)
  mutation_id uuid,
  PRIMARY KEY (entity_id, field)
);
CREATE INDEX field_clock_baby_ix ON nina_spike.field_clock (baby_id);
ALTER TABLE nina_spike.field_clock ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina_spike.field_clock FOR SELECT TO nina_app USING (nina.can_read_baby(baby_id));
CREATE POLICY app_insert ON nina_spike.field_clock FOR INSERT TO nina_app WITH CHECK (nina.can_write_baby(baby_id));
CREATE POLICY app_update ON nina_spike.field_clock FOR UPDATE TO nina_app USING (nina.can_write_baby(baby_id)) WITH CHECK (nina.can_write_baby(baby_id));
CREATE POLICY worker_all ON nina_spike.field_clock FOR ALL TO nina_worker USING (true) WITH CHECK (true);
GRANT SELECT, INSERT, UPDATE ON nina_spike.field_clock TO nina_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON nina_spike.field_clock TO nina_worker;

-- (2) nina_app não tem SELECT em baby_sync_head (0001, seção 11), mas o pull precisa de
--     last_sequence (ponto do snapshot, cursor "do futuro") e purged_through (cursor expirado).
--     Recomendação: função definer como esta (ou GRANT + RLS em baby_sync_head).
CREATE FUNCTION nina_spike.sync_head(p_baby uuid)
RETURNS TABLE (last_sequence bigint, purged_through bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT h.last_sequence, h.purged_through
    FROM nina.baby_sync_head h
   WHERE h.baby_id = p_baby AND nina.can_read_baby(p_baby) $$;
REVOKE ALL ON FUNCTION nina_spike.sync_head(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nina_spike.sync_head(uuid) TO nina_app, nina_worker;

COMMIT;
