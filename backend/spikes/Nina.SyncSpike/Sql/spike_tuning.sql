-- =============================================================================
-- Ajustes de desempenho AVALIADOS no spike ARCH-003 (não fazem parte de 0001_init.sql).
-- Aplicados pelo bench DEPOIS da medição de base, para quantificar o ganho (ver specs/sync-spike.md).
-- Semântica idêntica às políticas originais; só muda COMO a autorização é calculada.
-- =============================================================================
BEGIN;

-- Conjunto de bebês legíveis/graváveis do usuário corrente, avaliado UMA vez por comando (SubPlan hashed),
-- em vez de chamar nina.can_read_baby(baby_id) / can_write_baby(baby_id) uma vez por LINHA.
CREATE FUNCTION nina_spike.readable_babies() RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT m.baby_id FROM nina.caregiver_membership m
   WHERE m.user_id = nina.current_user_id() AND m.status = 'ACTIVE' $$;
CREATE FUNCTION nina_spike.writable_babies() RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT m.baby_id FROM nina.caregiver_membership m
   WHERE m.user_id = nina.current_user_id() AND m.status = 'ACTIVE' AND m.role IN ('OWNER', 'CAREGIVER') $$;
REVOKE ALL ON FUNCTION nina_spike.readable_babies(), nina_spike.writable_babies() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nina_spike.readable_babies(), nina_spike.writable_babies() TO nina_app;

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'wake_event', 'sleep_schedule_preference', 'sleep_prediction'] LOOP
    EXECUTE format('ALTER POLICY app_select ON nina.%I USING (baby_id IN (SELECT nina_spike.readable_babies()))', t);
    EXECUTE format('ALTER POLICY app_insert ON nina.%I WITH CHECK (baby_id IN (SELECT nina_spike.writable_babies()))', t);
    EXECUTE format('ALTER POLICY app_update ON nina.%I USING (baby_id IN (SELECT nina_spike.writable_babies())) WITH CHECK (baby_id IN (SELECT nina_spike.writable_babies()))', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['change_log', 'tombstone'] LOOP
    EXECUTE format('ALTER POLICY app_select ON nina.%I USING (baby_id IN (SELECT nina_spike.readable_babies()))', t);
  END LOOP;
END $$;
ALTER POLICY app_select ON nina.sync_mutation USING (baby_id IN (SELECT nina_spike.readable_babies()));
ALTER POLICY app_insert ON nina.sync_mutation WITH CHECK (baby_id IN (SELECT nina_spike.writable_babies()) AND user_id = nina.current_user_id());
ALTER POLICY app_select ON nina_spike.field_clock USING (baby_id IN (SELECT nina_spike.readable_babies()));
ALTER POLICY app_insert ON nina_spike.field_clock WITH CHECK (baby_id IN (SELECT nina_spike.writable_babies()));
ALTER POLICY app_update ON nina_spike.field_clock USING (baby_id IN (SELECT nina_spike.writable_babies())) WITH CHECK (baby_id IN (SELECT nina_spike.writable_babies()));

-- Keyset do snapshot do pull: (baby_id, id) das entidades vivas (hoje só existe a PK por id).
CREATE INDEX sleep_session_snapshot_ix   ON nina.sleep_session   (baby_id, id) WHERE deleted_at IS NULL;
CREATE INDEX feeding_session_snapshot_ix ON nina.feeding_session (baby_id, id) WHERE deleted_at IS NULL;
CREATE INDEX pumping_session_snapshot_ix ON nina.pumping_session (baby_id, id) WHERE deleted_at IS NULL;
CREATE INDEX diaper_event_snapshot_ix    ON nina.diaper_event    (baby_id, id) WHERE deleted_at IS NULL;
CREATE INDEX wake_event_snapshot_ix      ON nina.wake_event      (baby_id, id) WHERE deleted_at IS NULL;

COMMIT;
