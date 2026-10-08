-- =============================================================================
-- Nina - migração 0001_init (DB-001)
-- Modelo físico PostgreSQL. Especificação: specs/database-spec.md
-- Alvo: PostgreSQL 15+ (validado em 16). Sem extensões externas.
-- Executar com: psql -v ON_ERROR_STOP=1 -f 0001_init.sql  (uma transação).
-- Convenções: schema `nina`; instantes em timestamptz (UTC); IANA tz em colunas
-- `tz`/`timezone`; PKs uuid gerados no cliente quando sincronizáveis.
-- Papéis (NOLOGIN, a infraestrutura concede membership aos logins reais):
--   nina_app          runtime da API/BFF (sujeito a RLS)
--   nina_worker       jobs/outbox/purga/exclusão (política RLS própria)
--   nina_config_admin edição de flags/planos/parâmetros (auditada)
-- A aplicação NUNCA deve conectar como dono das tabelas nem como superuser
-- (ambos ignoram RLS).
-- =============================================================================

BEGIN;

SET LOCAL TIME ZONE 'UTC';
SET LOCAL check_function_bodies = on;
SET LOCAL nina.migration = 'on';  -- permite seeds sem ator (ver log_config_change)

-- -----------------------------------------------------------------------------
-- 0. Schema, papéis, tipos base
-- -----------------------------------------------------------------------------
CREATE SCHEMA nina;
COMMENT ON SCHEMA nina IS 'Nina - dados do produto (DB-001).';

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nina_app')          THEN CREATE ROLE nina_app NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nina_worker')       THEN CREATE ROLE nina_worker NOLOGIN; END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nina_config_admin') THEN CREATE ROLE nina_config_admin NOLOGIN; END IF;
END $$;

GRANT USAGE ON SCHEMA nina TO nina_app, nina_worker, nina_config_admin;

CREATE TABLE nina.schema_migration (
  version     text PRIMARY KEY,
  description text NOT NULL,
  applied_at  timestamptz NOT NULL DEFAULT now()
);

-- Fuso IANA (RB-014). Rejeita nomes desconhecidos pelo servidor.
CREATE DOMAIN nina.iana_tz AS text
  CHECK (VALUE IS NULL
         OR (VALUE ~ '^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$'
             AND length(VALUE) <= 64
             AND timezone(VALUE, now()) IS NOT NULL));

-- Contexto de requisição (definido por transação: SET LOCAL / set_config(..., true))
CREATE FUNCTION nina.current_user_id() RETURNS uuid
LANGUAGE sql STABLE AS
$$ SELECT nullif(current_setting('nina.user_id', true), '')::uuid $$;

CREATE FUNCTION nina.touch_updated_at() RETURNS trigger
LANGUAGE plpgsql AS
$$ BEGIN NEW.updated_at := now(); RETURN NEW; END $$;

-- -----------------------------------------------------------------------------
-- 1. Configuração editável (ADR-0005): planos, flags, parâmetros
--    (criada primeiro: funções de retenção leem app_parameter)
-- -----------------------------------------------------------------------------
CREATE TABLE nina.plan (
  id                    smallint PRIMARY KEY,
  code                  text NOT NULL UNIQUE CHECK (code ~ '^[a-z][a-z0-9_]*$'),
  name                  text NOT NULL,
  rank                  smallint NOT NULL UNIQUE,            -- maior = melhor
  max_premium_members   smallint NOT NULL DEFAULT 1 CHECK (max_premium_members >= 1),
  limits                jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(limits) = 'object'),
  is_active             boolean NOT NULL DEFAULT true,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now()
);
COMMENT ON COLUMN nina.plan.max_premium_members IS 'Membros com direito ao plano numa familia (premium: titular + 1 adicional = 2).';

CREATE TABLE nina.feature_flag (
  flag_key    text PRIMARY KEY CHECK (flag_key ~ '^[a-z][a-z0-9_.]*$' AND length(flag_key) <= 100),
  description text NOT NULL,
  -- gated: depende de plan_feature (padrao: bloqueada); open: liberada a todos; disabled: ninguem (kill switch)
  status      text NOT NULL DEFAULT 'gated' CHECK (status IN ('gated', 'open', 'disabled')),
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE nina.plan_feature (
  plan_id     smallint NOT NULL REFERENCES nina.plan(id) ON DELETE CASCADE,
  flag_key    text NOT NULL REFERENCES nina.feature_flag(flag_key) ON DELETE CASCADE,
  enabled     boolean NOT NULL DEFAULT true,
  limit_value integer CHECK (limit_value IS NULL OR limit_value >= 0),  -- limite quantitativo opcional
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (plan_id, flag_key)
);

CREATE TABLE nina.app_parameter (
  param_key      text PRIMARY KEY CHECK (param_key ~ '^[a-z][a-z0-9_.]*$' AND length(param_key) <= 120),
  value          jsonb NOT NULL,
  value_type     text NOT NULL CHECK (value_type IN ('int', 'bool', 'string', 'json')),
  description    text NOT NULL,
  schema_version integer NOT NULL DEFAULT 1 CHECK (schema_version >= 1),
  version        bigint NOT NULL DEFAULT 1,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  updated_by     uuid,
  CONSTRAINT app_parameter_type_ck CHECK (
       (value_type = 'int'    AND jsonb_typeof(value) = 'number')
    OR (value_type = 'bool'   AND jsonb_typeof(value) = 'boolean')
    OR (value_type = 'string' AND jsonb_typeof(value) = 'string')
    OR (value_type = 'json'   AND jsonb_typeof(value) IN ('object', 'array')))
);

CREATE FUNCTION nina.param_int(p_key text, p_default integer) RETURNS integer
LANGUAGE sql STABLE AS
$$ SELECT coalesce((SELECT (value #>> '{}')::integer FROM nina.app_parameter
                     WHERE param_key = p_key AND value_type = 'int'), p_default) $$;

CREATE FUNCTION nina.param_text(p_key text, p_default text) RETURNS text
LANGUAGE sql STABLE AS
$$ SELECT coalesce((SELECT value #>> '{}' FROM nina.app_parameter
                     WHERE param_key = p_key AND value_type = 'string'), p_default) $$;

-- -----------------------------------------------------------------------------
-- 2. Identity
-- -----------------------------------------------------------------------------
CREATE TABLE nina.app_user (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email             text,
  email_normalized  text GENERATED ALWAYS AS (lower(btrim(email))) STORED,
  email_verified_at timestamptz,
  locale            text CHECK (locale ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$'),
  timezone          nina.iana_tz,
  status            text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'pending_deletion', 'deleted')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  deleted_at        timestamptz,
  CONSTRAINT app_user_email_ck CHECK (
    (status = 'deleted' AND email IS NULL AND deleted_at IS NOT NULL)
    OR (status <> 'deleted' AND email IS NOT NULL AND position('@' IN email) > 1 AND length(email) <= 254))
);
COMMENT ON TABLE nina.app_user IS 'Apos exclusao de conta a linha permanece anonimizada (status=deleted, sem PII) para preservar integridade de consentimento/auditoria/fiscal.';
CREATE UNIQUE INDEX app_user_email_uq ON nina.app_user (email_normalized) WHERE email_normalized IS NOT NULL;
CREATE TRIGGER app_user_touch BEFORE UPDATE ON nina.app_user FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

CREATE TABLE nina.user_credential (
  user_id        uuid PRIMARY KEY REFERENCES nina.app_user(id) ON DELETE CASCADE,
  password_hash  text NOT NULL,                       -- Argon2id/bcrypt (formato PHC); nunca logar
  hash_algorithm text NOT NULL CHECK (hash_algorithm IN ('argon2id', 'bcrypt')),
  changed_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE nina.user_identity (   -- Google/Apple (ADR-0007); sem fusao automatica por e-mail
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id          uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  provider         text NOT NULL CHECK (provider IN ('google', 'apple')),
  provider_subject text NOT NULL,
  email_at_link    text,
  linked_at        timestamptz NOT NULL DEFAULT now(),
  UNIQUE (provider, provider_subject)
);
CREATE INDEX user_identity_user_ix ON nina.user_identity (user_id);

CREATE TABLE nina.auth_session (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id            uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  device_id          uuid NOT NULL,
  device_label       text CHECK (length(device_label) <= 100),
  platform           text NOT NULL CHECK (platform IN ('ios', 'android', 'web')),
  user_agent         text CHECK (length(user_agent) <= 256),
  app_version        text CHECK (length(app_version) <= 32),
  created_at         timestamptz NOT NULL DEFAULT now(),
  last_seen_at       timestamptz NOT NULL DEFAULT now(),
  absolute_expires_at timestamptz NOT NULL,           -- 30 dias absolutos (privacy-spec 4)
  revoked_at         timestamptz,
  revoked_reason     text CHECK (revoked_reason IN ('logout', 'user_revoked', 'password_changed', 'reuse_detected', 'account_deleted', 'admin'))
);
CREATE INDEX auth_session_user_ix ON nina.auth_session (user_id) WHERE revoked_at IS NULL;
CREATE INDEX auth_session_expiry_ix ON nina.auth_session (absolute_expires_at);

CREATE TABLE nina.refresh_token (   -- rotativo, uso unico, deteccao de reuso (SEC-011)
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id uuid NOT NULL REFERENCES nina.auth_session(id) ON DELETE CASCADE,
  token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
  issued_at  timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  used_at    timestamptz
);
CREATE INDEX refresh_token_session_ix ON nina.refresh_token (session_id);
CREATE INDEX refresh_token_expiry_ix ON nina.refresh_token (expires_at);

CREATE TABLE nina.recovery_request (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id    uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL,
  used_at    timestamptz
);
CREATE INDEX recovery_request_user_ix ON nina.recovery_request (user_id);

CREATE TABLE nina.device_push_token (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id      uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  device_id    uuid NOT NULL,
  platform     text NOT NULL CHECK (platform IN ('apns', 'fcm')),
  token        text NOT NULL,                          -- C4: avaliar cifra em nivel de campo (SEC-021)
  created_at   timestamptz NOT NULL DEFAULT now(),
  last_seen_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (platform, token),
  UNIQUE (user_id, device_id, platform)
);

-- -----------------------------------------------------------------------------
-- 3. Family / Baby / vínculos
-- -----------------------------------------------------------------------------
CREATE TABLE nina.family (   -- agrupador de entitlement (ADR-0005, D-04): 1 familia por titular
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_user_id uuid NOT NULL UNIQUE REFERENCES nina.app_user(id),
  created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE nina.baby (
  id               uuid PRIMARY KEY,                    -- gerado no cliente
  family_id        uuid NOT NULL REFERENCES nina.family(id),
  display_name     text CHECK (length(display_name) BETWEEN 1 AND 100),
  birth_date       date,
  due_date         date,
  sex              text CHECK (sex IN ('female', 'male', 'unspecified')),
  timezone         nina.iana_tz NOT NULL,
  photo_ref        text CHECK (length(photo_ref) <= 512),   -- D-09
  version          bigint NOT NULL DEFAULT 0,               -- = sync_sequence da ultima mudanca
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  -- Casca sem conteudo apos exclusao (tombstone): so entao os campos podem ser nulos
  CONSTRAINT baby_content_ck CHECK (
    (deleted_at IS NULL AND display_name IS NOT NULL AND birth_date IS NOT NULL)
    OR (deleted_at IS NOT NULL AND display_name IS NULL AND birth_date IS NULL
        AND due_date IS NULL AND sex IS NULL AND photo_ref IS NULL))
);
CREATE INDEX baby_family_ix ON nina.baby (family_id);
CREATE INDEX baby_deleted_ix ON nina.baby (deleted_at) WHERE deleted_at IS NOT NULL;

CREATE FUNCTION nina.baby_validate() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.deleted_at IS NULL AND NEW.birth_date > (now() AT TIME ZONE NEW.timezone)::date THEN
    RAISE EXCEPTION 'birth_date no futuro (INV-16)' USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER baby_validate BEFORE INSERT OR UPDATE ON nina.baby FOR EACH ROW EXECUTE FUNCTION nina.baby_validate();

CREATE TABLE nina.caregiver_membership (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  baby_id           uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  user_id           uuid REFERENCES nina.app_user(id),
  invited_email     text CHECK (length(invited_email) <= 254),
  role              text NOT NULL CHECK (role IN ('owner', 'caregiver', 'read_only')),
  status            text NOT NULL CHECK (status IN ('pending', 'active', 'revoked', 'declined')),
  invited_by        uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  invite_token_hash bytea UNIQUE CHECK (octet_length(invite_token_hash) = 32),
  invite_expires_at timestamptz,
  invited_at        timestamptz NOT NULL DEFAULT now(),
  accepted_at       timestamptz,
  revoked_at        timestamptz,
  revoked_reason    text CHECK (revoked_reason IN ('owner_removed', 'left', 'baby_deleted', 'account_deleted', 'ownership_transferred')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT membership_active_ck  CHECK (status <> 'active' OR (user_id IS NOT NULL AND accepted_at IS NOT NULL)),
  CONSTRAINT membership_pending_ck CHECK (status <> 'pending' OR user_id IS NOT NULL OR invited_email IS NOT NULL),
  CONSTRAINT membership_revoked_ck CHECK (status <> 'revoked' OR revoked_at IS NOT NULL),
  CONSTRAINT membership_owner_ck   CHECK (role <> 'owner' OR status IN ('active', 'revoked'))
);
-- INV-10: no maximo um vinculo nao encerrado por (bebe, usuario)
CREATE UNIQUE INDEX membership_baby_user_uq ON nina.caregiver_membership (baby_id, user_id)
  WHERE user_id IS NOT NULL AND status IN ('pending', 'active');
CREATE UNIQUE INDEX membership_baby_email_uq ON nina.caregiver_membership (baby_id, lower(invited_email))
  WHERE user_id IS NULL AND status = 'pending';
-- INV-09 (no maximo um): exatamente um Owner ativo e garantido junto com o constraint trigger abaixo
CREATE UNIQUE INDEX membership_one_owner_uq ON nina.caregiver_membership (baby_id)
  WHERE role = 'owner' AND status = 'active';
CREATE INDEX membership_user_ix ON nina.caregiver_membership (user_id, baby_id) WHERE status = 'active';
CREATE INDEX membership_invite_expiry_ix ON nina.caregiver_membership (invite_expires_at) WHERE status = 'pending';
CREATE TRIGGER membership_touch BEFORE UPDATE ON nina.caregiver_membership FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

-- INV-09 (pelo menos um) / SEC-007: bebe vivo sempre tem Owner ativo ao fim da transacao
CREATE FUNCTION nina.check_baby_has_owner() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_baby uuid := coalesce(NEW.baby_id, OLD.baby_id);
BEGIN
  IF EXISTS (SELECT 1 FROM nina.baby b WHERE b.id = v_baby AND b.deleted_at IS NULL)
     AND NOT EXISTS (SELECT 1 FROM nina.caregiver_membership m
                      WHERE m.baby_id = v_baby AND m.role = 'owner' AND m.status = 'active') THEN
    RAISE EXCEPTION 'bebe % sem Owner ativo (INV-09/SEC-007)', v_baby USING ERRCODE = 'NN010';
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER membership_owner_present
  AFTER INSERT OR UPDATE OR DELETE ON nina.caregiver_membership
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION nina.check_baby_has_owner();

-- Helpers de autorizacao (SECURITY DEFINER para nao recursar nas politicas de RLS)
CREATE FUNCTION nina.baby_role(p_baby uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT m.role FROM nina.caregiver_membership m
    WHERE m.baby_id = p_baby AND m.user_id = nina.current_user_id() AND m.status = 'active' $$;

CREATE FUNCTION nina.can_read_baby(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT nina.baby_role(p_baby) IS NOT NULL $$;

CREATE FUNCTION nina.can_write_baby(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT coalesce(nina.baby_role(p_baby) IN ('owner', 'caregiver'), false) $$;

CREATE FUNCTION nina.is_family_owner(p_family uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.family f WHERE f.id = p_family AND f.owner_user_id = nina.current_user_id()) $$;

CREATE FUNCTION nina.can_bootstrap_owner(p_baby uuid) RETURNS boolean   -- criador do bebe vira Owner (RB-006)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.baby b JOIN nina.family f ON f.id = b.family_id
                   WHERE b.id = p_baby AND f.owner_user_id = nina.current_user_id())
      AND NOT EXISTS (SELECT 1 FROM nina.caregiver_membership m WHERE m.baby_id = p_baby AND m.role = 'owner') $$;

-- -----------------------------------------------------------------------------
-- 4. Sync: contador por bebê, change log, tombstones, mutações (ADR-0003)
-- -----------------------------------------------------------------------------
CREATE TABLE nina.baby_sync_head (
  baby_id        uuid PRIMARY KEY REFERENCES nina.baby(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
  last_sequence  bigint NOT NULL DEFAULT 0,
  purged_through bigint NOT NULL DEFAULT 0   -- cursor < purged_through => "cursor expirado" (resync completo)
);
COMMENT ON TABLE nina.baby_sync_head IS 'Contador serializado por bebe: UPDATE segura o lock de linha ate o commit, logo a sequencia nasce sem lacunas e em ordem de commit.';

CREATE FUNCTION nina.next_sync_sequence(p_baby uuid) RETURNS bigint
LANGUAGE sql AS $$
  INSERT INTO nina.baby_sync_head AS h (baby_id, last_sequence) VALUES (p_baby, 1)
  ON CONFLICT (baby_id) DO UPDATE SET last_sequence = h.last_sequence + 1
  RETURNING last_sequence $$;

CREATE TABLE nina.change_log (
  baby_id        uuid   NOT NULL,                    -- sem FK: sobrevive a casca do bebe ate a purga
  sync_sequence  bigint NOT NULL,
  entity_type    text   NOT NULL CHECK (entity_type IN ('baby', 'sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'sleep_schedule_preference')),
  entity_id      uuid   NOT NULL,
  op             text   NOT NULL CHECK (op IN ('upsert', 'delete')),
  actor_user_id  uuid,
  device_id      uuid,
  changed_at     timestamptz NOT NULL DEFAULT clock_timestamp(),
  PRIMARY KEY (baby_id, sync_sequence)
);
COMMENT ON TABLE nina.change_log IS 'Feed de mudancas SEM conteudo; o pull le o estado atual da entidade. Retencao 90 dias.';
CREATE INDEX change_log_changed_at_ix ON nina.change_log (changed_at);
CREATE INDEX change_log_entity_ix ON nina.change_log (baby_id, entity_type, entity_id);

CREATE TABLE nina.tombstone (
  baby_id     uuid NOT NULL,
  entity_type text NOT NULL,
  entity_id   uuid NOT NULL,
  deleted_at  timestamptz NOT NULL,
  version     bigint NOT NULL,
  expires_at  timestamptz NOT NULL,
  PRIMARY KEY (baby_id, entity_type, entity_id)
);
CREATE INDEX tombstone_expiry_ix ON nina.tombstone (expires_at);

CREATE TABLE nina.sync_mutation (   -- idempotencia do push (INV-18); sem payload
  mutation_id       uuid PRIMARY KEY,
  baby_id           uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  user_id           uuid NOT NULL REFERENCES nina.app_user(id),
  device_id         uuid NOT NULL,
  entity_type       text NOT NULL,
  entity_id         uuid NOT NULL,
  op                text NOT NULL CHECK (op IN ('create', 'update', 'delete')),
  base_version      bigint,
  client_created_at timestamptz NOT NULL,
  received_at       timestamptz NOT NULL DEFAULT now(),
  outcome           text NOT NULL CHECK (outcome IN ('applied', 'merged', 'ignored_tombstone', 'rejected')),
  result_version    bigint,
  reject_code       text CHECK (length(reject_code) <= 64)
);
CREATE INDEX sync_mutation_baby_ix ON nina.sync_mutation (baby_id, received_at);
CREATE INDEX sync_mutation_received_ix ON nina.sync_mutation (received_at);

-- Trigger BEFORE: atribui version (servidor), protege tombstone, limpa notas na exclusao
CREATE FUNCTION nina.sync_stamp_child() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    IF EXISTS (SELECT 1 FROM nina.baby b WHERE b.id = NEW.baby_id AND b.deleted_at IS NOT NULL) THEN
      RAISE EXCEPTION 'bebe % excluido', NEW.baby_id USING ERRCODE = 'NN003';
    END IF;
    NEW.created_at := now();
  ELSE
    IF NEW.id <> OLD.id OR NEW.baby_id <> OLD.baby_id THEN
      RAISE EXCEPTION 'id/baby_id sao imutaveis' USING ERRCODE = 'NN001';
    END IF;
    IF OLD.deleted_at IS NOT NULL THEN
      RAISE EXCEPTION 'entidade com tombstone nao pode ser alterada/ressuscitada (INV-20)' USING ERRCODE = 'NN002';
    END IF;
    NEW.created_at := OLD.created_at;
  END IF;
  IF NEW.deleted_at IS NOT NULL THEN      -- tombstone sem conteudo livre
    NEW := jsonb_populate_record(NEW, jsonb_build_object('notes', NULL));
  END IF;
  NEW.updated_at := now();
  NEW.version := nina.next_sync_sequence(NEW.baby_id);
  RETURN NEW;
END $$;

CREATE FUNCTION nina.sync_stamp_baby() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    IF NEW.id <> OLD.id THEN RAISE EXCEPTION 'id imutavel' USING ERRCODE = 'NN001'; END IF;
    IF OLD.deleted_at IS NOT NULL THEN
      RAISE EXCEPTION 'bebe excluido nao pode ser alterado (INV-20)' USING ERRCODE = 'NN002';
    END IF;
    NEW.created_at := OLD.created_at;
  ELSE
    NEW.created_at := now();
  END IF;
  NEW.updated_at := now();
  NEW.version := nina.next_sync_sequence(NEW.id);
  RETURN NEW;
END $$;

-- Trigger AFTER: grava change_log e tombstone na MESMA transacao da mudanca
CREATE FUNCTION nina.sync_log_change() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  v_baby uuid;
  v_days integer;
BEGIN
  IF TG_ARGV[0] = 'baby' THEN v_baby := NEW.id; ELSE v_baby := NEW.baby_id; END IF;
  INSERT INTO nina.change_log (baby_id, sync_sequence, entity_type, entity_id, op, actor_user_id, device_id)
  VALUES (v_baby, NEW.version, TG_ARGV[0], NEW.id,
          CASE WHEN NEW.deleted_at IS NULL THEN 'upsert' ELSE 'delete' END,
          NEW.last_modified_by, nullif(current_setting('nina.device_id', true), '')::uuid);
  IF NEW.deleted_at IS NOT NULL THEN
    v_days := nina.param_int('sync.tombstone_retention_days', 90);
    INSERT INTO nina.tombstone (baby_id, entity_type, entity_id, deleted_at, version, expires_at)
    VALUES (v_baby, TG_ARGV[0], NEW.id, NEW.deleted_at, NEW.version, NEW.deleted_at + make_interval(days => v_days))
    ON CONFLICT (baby_id, entity_type, entity_id) DO UPDATE
      SET deleted_at = EXCLUDED.deleted_at, version = EXCLUDED.version, expires_at = EXCLUDED.expires_at;
  END IF;
  RETURN NULL;
END $$;

CREATE TRIGGER baby_stamp BEFORE INSERT OR UPDATE ON nina.baby FOR EACH ROW EXECUTE FUNCTION nina.sync_stamp_baby();
CREATE TRIGGER baby_sync_log AFTER INSERT OR UPDATE ON nina.baby FOR EACH ROW EXECUTE FUNCTION nina.sync_log_change('baby');

-- -----------------------------------------------------------------------------
-- 5. Tracking (sincronizáveis)
-- -----------------------------------------------------------------------------
CREATE TABLE nina.sleep_session (
  id               uuid PRIMARY KEY,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  sleep_type       text NOT NULL CHECK (sleep_type IN ('nap', 'night')),
  method_or_place  text CHECK (length(method_or_place) <= 60),   -- D-08 (texto livre curto ate decisao)
  notes            text CHECK (length(notes) <= 2000),
  source           text NOT NULL CHECK (source IN ('timer', 'manual')),
  version          bigint NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT sleep_interval_ck CHECK (end_at IS NULL OR end_at >= start_at)            -- INV-01
);
CREATE UNIQUE INDEX sleep_one_open_uq ON nina.sleep_session (baby_id)                  -- INV-02
  WHERE end_at IS NULL AND deleted_at IS NULL;
CREATE INDEX sleep_timeline_ix ON nina.sleep_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.feeding_session (
  id               uuid PRIMARY KEY,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  feeding_type     text NOT NULL CHECK (feeding_type IN ('breast', 'bottle')),
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  side             text CHECK (side IN ('left', 'right', 'both')),
  volume_ml        numeric(6,1) CHECK (volume_ml > 0 AND volume_ml <= 5000),
  milk_type        text CHECK (length(milk_type) <= 40),
  notes            text CHECK (length(notes) <= 2000),
  version          bigint NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT feeding_interval_ck CHECK (end_at IS NULL OR end_at >= start_at),         -- INV-01
  CONSTRAINT feeding_shape_ck CHECK (                                                   -- INV-07
       (feeding_type = 'breast' AND side IS NOT NULL AND end_at IS NOT NULL
          AND volume_ml IS NULL AND milk_type IS NULL)
    OR (feeding_type = 'bottle' AND side IS NULL))
);
CREATE INDEX feeding_timeline_ix ON nina.feeding_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.pumping_session (
  id               uuid PRIMARY KEY,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  volume_ml        numeric(6,1) CHECK (volume_ml > 0 AND volume_ml <= 5000),
  side             text CHECK (side IN ('left', 'right', 'both')),
  notes            text CHECK (length(notes) <= 2000),
  version          bigint NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT pumping_interval_ck CHECK (end_at IS NULL OR end_at >= start_at)
);
CREATE INDEX pumping_timeline_ix ON nina.pumping_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.diaper_event (
  id               uuid PRIMARY KEY,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  occurred_at      timestamptz NOT NULL,
  tz               nina.iana_tz NOT NULL,
  diaper_type      text NOT NULL CHECK (length(diaper_type) BETWEEN 1 AND 40),      -- D-08
  notes            text CHECK (length(notes) <= 2000),
  version          bigint NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL
);
CREATE INDEX diaper_timeline_ix ON nina.diaper_event (baby_id, occurred_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.sleep_schedule_preference (   -- RF-014; 1 por bebe
  id                uuid PRIMARY KEY,
  baby_id           uuid NOT NULL UNIQUE REFERENCES nina.baby(id) ON DELETE CASCADE,
  target_nap_count  smallint CHECK (target_nap_count BETWEEN 0 AND 8),
  bedtime_from      time,                          -- hora local do bebe
  bedtime_to        time,
  version           bigint NOT NULL DEFAULT 0,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  deleted_at        timestamptz,
  created_by        uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by  uuid REFERENCES nina.app_user(id) ON DELETE SET NULL
);

-- Previsoes: derivadas/descartaveis, NAO sincronizadas (INV-05). Retencao 90 dias.
CREATE TABLE nina.sleep_prediction (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  baby_id            uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  kind               text NOT NULL CHECK (kind IN ('next_nap', 'bedtime')),
  predicted_start    timestamptz NOT NULL,
  predicted_end      timestamptz,
  confidence         numeric(4,3) NOT NULL CHECK (confidence BETWEEN 0 AND 1),
  explanation_key    text NOT NULL,
  explanation_params jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(explanation_params) = 'object'),
  model_version      text NOT NULL,
  inputs_version     bigint,                        -- baby_sync_head.last_sequence usado no calculo
  computed_at        timestamptz NOT NULL DEFAULT now(),
  CHECK (predicted_end IS NULL OR predicted_end >= predicted_start)
);
CREATE INDEX sleep_prediction_ix ON nina.sleep_prediction (baby_id, kind, computed_at DESC);

DO $$
DECLARE t text; e text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'sleep_schedule_preference'] LOOP
    EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE ON nina.%I FOR EACH ROW EXECUTE FUNCTION nina.sync_stamp_child()', t || '_stamp', t);
    EXECUTE format('CREATE TRIGGER %I AFTER INSERT OR UPDATE ON nina.%I FOR EACH ROW EXECUTE FUNCTION nina.sync_log_change(%L)', t || '_sync_log', t, t);
  END LOOP;
END $$;

-- -----------------------------------------------------------------------------
-- 6. Notificações e outbox
-- -----------------------------------------------------------------------------
CREATE TABLE nina.notification_preference (
  user_id             uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  baby_id             uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  category            text NOT NULL CHECK (category IN ('nap', 'bedtime', 'routine', 'development_phase', 'system')),
  enabled             boolean NOT NULL,
  lead_time_minutes   smallint NOT NULL DEFAULT 0 CHECK (lead_time_minutes BETWEEN 0 AND 720),
  quiet_hours_start   time,
  quiet_hours_end     time,
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, baby_id, category),
  CHECK ((quiet_hours_start IS NULL) = (quiet_hours_end IS NULL))
);
CREATE TRIGGER notification_preference_touch BEFORE UPDATE ON nina.notification_preference FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

CREATE TABLE nina.notification_job (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  event_key       text NOT NULL UNIQUE CHECK (length(event_key) <= 200),   -- INV-22
  user_id         uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  baby_id         uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  category        text NOT NULL CHECK (category IN ('nap', 'bedtime', 'routine', 'development_phase', 'system')),
  scheduled_at    timestamptz NOT NULL,
  status          text NOT NULL DEFAULT 'scheduled' CHECK (status IN ('scheduled', 'sent', 'failed', 'cancelled', 'skipped_quiet_hours', 'dead_letter')),
  attempts        smallint NOT NULL DEFAULT 0,
  next_attempt_at timestamptz,
  last_error_code text CHECK (length(last_error_code) <= 64),              -- sem PII
  provider_ref    text,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX notification_job_due_ix ON nina.notification_job (scheduled_at) WHERE status IN ('scheduled', 'failed');
CREATE INDEX notification_job_baby_ix ON nina.notification_job (baby_id);
CREATE INDEX notification_job_user_ix ON nina.notification_job (user_id);
CREATE TRIGGER notification_job_touch BEFORE UPDATE ON nina.notification_job FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

CREATE TABLE nina.outbox_message (   -- outbox transacional (ADR-0002); payload sem PII
  id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  event_key        text UNIQUE,
  aggregate_type   text NOT NULL,
  aggregate_id     uuid,
  event_type       text NOT NULL,
  payload          jsonb NOT NULL DEFAULT '{}'::jsonb CHECK (jsonb_typeof(payload) = 'object'),
  created_at       timestamptz NOT NULL DEFAULT now(),
  available_at     timestamptz NOT NULL DEFAULT now(),
  processed_at     timestamptz,
  attempts         smallint NOT NULL DEFAULT 0,
  last_error_code  text CHECK (length(last_error_code) <= 64),
  dead_lettered_at timestamptz
);
CREATE INDEX outbox_pending_ix ON nina.outbox_message (available_at, id)
  WHERE processed_at IS NULL AND dead_lettered_at IS NULL;

-- -----------------------------------------------------------------------------
-- 7. Assinaturas e entitlement por família (ADR-0005)
-- -----------------------------------------------------------------------------
CREATE TABLE nina.subscription (
  id                        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  family_id                 uuid NOT NULL REFERENCES nina.family(id),
  purchaser_user_id         uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  plan_id                   smallint NOT NULL REFERENCES nina.plan(id),
  store                     text NOT NULL CHECK (store IN ('apple', 'google')),
  product_ref               text NOT NULL,
  original_transaction_ref  text NOT NULL,
  store_transaction_ref     text NOT NULL,
  status                    text NOT NULL CHECK (status IN ('active', 'grace', 'expired', 'cancelled', 'revoked')),
  valid_until               timestamptz,
  last_validated_at         timestamptz,
  created_at                timestamptz NOT NULL DEFAULT now(),
  updated_at                timestamptz NOT NULL DEFAULT now(),
  UNIQUE (store, original_transaction_ref)         -- INV-26: restauracao reaproveita a mesma assinatura
);
CREATE INDEX subscription_family_ix ON nina.subscription (family_id);
CREATE TRIGGER subscription_touch BEFORE UPDATE ON nina.subscription FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

CREATE TABLE nina.family_entitlement (
  id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  family_id       uuid NOT NULL REFERENCES nina.family(id),
  plan_id         smallint NOT NULL REFERENCES nina.plan(id),
  source          text NOT NULL CHECK (source IN ('subscription', 'manual_grant')),
  subscription_id uuid REFERENCES nina.subscription(id),
  status          text NOT NULL CHECK (status IN ('active', 'grace', 'expired', 'revoked')),
  valid_from      timestamptz NOT NULL DEFAULT now(),
  valid_until     timestamptz,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  CHECK ((source = 'subscription') = (subscription_id IS NOT NULL)),
  CHECK (valid_until IS NULL OR valid_until > valid_from)
);
CREATE UNIQUE INDEX family_entitlement_one_live_uq ON nina.family_entitlement (family_id) WHERE status IN ('active', 'grace');
CREATE TRIGGER family_entitlement_touch BEFORE UPDATE ON nina.family_entitlement FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

CREATE TABLE nina.family_entitlement_member (
  entitlement_id uuid NOT NULL REFERENCES nina.family_entitlement(id) ON DELETE CASCADE,
  user_id        uuid NOT NULL REFERENCES nina.app_user(id),
  member_role    text NOT NULL CHECK (member_role IN ('holder', 'additional')),
  added_at       timestamptz NOT NULL DEFAULT now(),
  removed_at     timestamptz,
  PRIMARY KEY (entitlement_id, user_id)
);
CREATE UNIQUE INDEX fem_one_holder_uq ON nina.family_entitlement_member (entitlement_id) WHERE member_role = 'holder' AND removed_at IS NULL;
CREATE INDEX fem_user_ix ON nina.family_entitlement_member (user_id) WHERE removed_at IS NULL;

-- Titular = dono da familia; total de membros ativos <= plan.max_premium_members (premium: 2)
CREATE FUNCTION nina.check_entitlement_member() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_max smallint; v_owner uuid; v_count integer;
BEGIN
  IF NEW.removed_at IS NOT NULL THEN RETURN NEW; END IF;
  SELECT p.max_premium_members, f.owner_user_id INTO v_max, v_owner
    FROM nina.family_entitlement e
    JOIN nina.plan p ON p.id = e.plan_id
    JOIN nina.family f ON f.id = e.family_id
   WHERE e.id = NEW.entitlement_id FOR UPDATE OF e;       -- serializa concorrentes
  IF NEW.member_role = 'holder' AND NEW.user_id <> v_owner THEN
    RAISE EXCEPTION 'o titular deve ser o dono da familia' USING ERRCODE = 'NN020';
  END IF;
  SELECT count(*) INTO v_count FROM nina.family_entitlement_member m
   WHERE m.entitlement_id = NEW.entitlement_id AND m.removed_at IS NULL
     AND NOT (m.user_id = NEW.user_id);
  IF v_count + 1 > v_max THEN
    RAISE EXCEPTION 'limite de membros do plano excedido (% )', v_max USING ERRCODE = 'NN021';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER fem_check BEFORE INSERT OR UPDATE ON nina.family_entitlement_member
  FOR EACH ROW EXECUTE FUNCTION nina.check_entitlement_member();

-- Resolucao no servidor (RB-009): melhor plano valido do usuario; sem entitlement = free
CREATE FUNCTION nina.user_plan_code(p_user uuid) RETURNS text
LANGUAGE sql STABLE AS $$
  SELECT coalesce((
    SELECT p.code
      FROM nina.family_entitlement_member m
      JOIN nina.family_entitlement e ON e.id = m.entitlement_id
      JOIN nina.plan p ON p.id = e.plan_id AND p.is_active
     WHERE m.user_id = p_user AND m.removed_at IS NULL
       AND e.status IN ('active', 'grace')
       AND e.valid_from <= now() AND (e.valid_until IS NULL OR e.valid_until > now())
     ORDER BY p.rank DESC LIMIT 1), 'free') $$;

CREATE FUNCTION nina.user_has_feature(p_user uuid, p_flag text) RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT coalesce((
    SELECT CASE f.status
             WHEN 'open' THEN true
             WHEN 'disabled' THEN false
             ELSE coalesce((SELECT pf.enabled FROM nina.plan_feature pf JOIN nina.plan p ON p.id = pf.plan_id
                             WHERE p.code = nina.user_plan_code(p_user) AND pf.flag_key = f.flag_key), false)
           END
      FROM nina.feature_flag f WHERE f.flag_key = p_flag), false) $$;   -- flag inexistente = bloqueado

-- Auditoria de alteracao de configuracao (ADR-0005): exige ator
CREATE TABLE nina.config_change (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  table_name    text NOT NULL,
  op            text NOT NULL CHECK (op IN ('INSERT', 'UPDATE', 'DELETE')),
  record_key    text NOT NULL,
  old_row       jsonb,
  new_row       jsonb,
  actor_user_id uuid,
  reason        text,
  changed_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX config_change_ix ON nina.config_change (table_name, record_key, changed_at DESC);

-- -----------------------------------------------------------------------------
-- 8. Privacidade: consentimento (append-only), auditoria (append-only), DSAR
-- -----------------------------------------------------------------------------
CREATE TABLE nina.consent_purpose (
  purpose_key     text PRIMARY KEY CHECK (purpose_key ~ '^[a-z][a-z0-9_]*$'),
  description     text NOT NULL,
  is_required     boolean NOT NULL,
  scope           text NOT NULL CHECK (scope IN ('user', 'baby')),
  current_version text,
  in_mvp          boolean NOT NULL DEFAULT false
);

CREATE TABLE nina.consent_record (
  seq             bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
  consent_id      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id         uuid NOT NULL REFERENCES nina.app_user(id),
  subject_baby_id uuid,                                  -- sem FK: nao reter vinculo com crianca excluida
  purpose_key     text NOT NULL REFERENCES nina.consent_purpose(purpose_key),
  policy_version  text NOT NULL,
  text_hash       text NOT NULL CHECK (text_hash ~ '^[0-9a-f]{64}$'),
  locale          text NOT NULL,
  status          text NOT NULL CHECK (status IN ('granted', 'revoked')),
  recorded_at     timestamptz NOT NULL DEFAULT now(),
  source          text NOT NULL CHECK (source IN ('onboarding', 'settings', 'prompt', 'invite_accept', 'system')),
  app_version     text,
  platform        text CHECK (platform IN ('ios', 'android', 'web', 'server'))
);
CREATE INDEX consent_lookup_ix ON nina.consent_record (user_id, purpose_key, seq DESC);

CREATE VIEW nina.consent_current WITH (security_invoker = true) AS
SELECT DISTINCT ON (user_id, subject_baby_id, purpose_key)
       user_id, subject_baby_id, purpose_key, policy_version, text_hash, locale, status, recorded_at
  FROM nina.consent_record
 ORDER BY user_id, subject_baby_id, purpose_key, seq DESC;   -- NULLs de subject_baby_id agrupam juntos em DISTINCT ON

CREATE TABLE nina.audit_event (
  id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  occurred_at   timestamptz NOT NULL DEFAULT now(),
  actor_user_id uuid,                                   -- sem FK (pseudonimo apos exclusao)
  actor_type    text NOT NULL DEFAULT 'user' CHECK (actor_type IN ('user', 'system', 'admin', 'support')),
  action        text NOT NULL CHECK (action ~ '^[a-z][a-z0-9_.]*$'),
  entity_type   text,
  entity_id     uuid,
  baby_id       uuid,                                   -- sem FK
  device_id     uuid,
  request_id    text,
  result        text NOT NULL DEFAULT 'success' CHECK (result IN ('success', 'failure', 'denied')),
  ip_hash       bytea CHECK (octet_length(ip_hash) = 32),
  metadata_safe jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_critical   boolean NOT NULL DEFAULT false,
  chain_seq     bigint UNIQUE,
  prev_hash     bytea,
  row_hash      bytea,
  CONSTRAINT audit_metadata_ck CHECK (
    jsonb_typeof(metadata_safe) = 'object'
    AND octet_length(metadata_safe::text) <= 2048
    AND NOT (metadata_safe ?| ARRAY['email', 'name', 'display_name', 'nickname', 'notes', 'password', 'token', 'birth_date', 'photo']))
);
CREATE INDEX audit_time_ix ON nina.audit_event (occurred_at);
CREATE INDEX audit_actor_ix ON nina.audit_event (actor_user_id, occurred_at DESC);
CREATE INDEX audit_entity_ix ON nina.audit_event (entity_type, entity_id);
CREATE INDEX audit_baby_ix ON nina.audit_event (baby_id, occurred_at DESC) WHERE baby_id IS NOT NULL;

-- Encadeamento de hash para eventos criticos (SEC-032)
CREATE FUNCTION nina.audit_chain() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_prev bytea; v_seq bigint;
BEGIN
  IF NEW.is_critical THEN
    PERFORM pg_advisory_xact_lock(hashtextextended('nina.audit_chain', 0));
    SELECT chain_seq, row_hash INTO v_seq, v_prev FROM nina.audit_event
     WHERE chain_seq IS NOT NULL ORDER BY chain_seq DESC LIMIT 1;
    NEW.chain_seq := coalesce(v_seq, 0) + 1;
    NEW.prev_hash := v_prev;
    NEW.row_hash := sha256(convert_to(concat_ws('|', coalesce(encode(v_prev, 'hex'), ''), NEW.chain_seq,
        NEW.occurred_at, coalesce(NEW.actor_user_id::text, ''), NEW.action, coalesce(NEW.entity_type, ''),
        coalesce(NEW.entity_id::text, ''), coalesce(NEW.baby_id::text, ''), NEW.result, NEW.metadata_safe::text), 'UTF8'));
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER audit_event_chain BEFORE INSERT ON nina.audit_event FOR EACH ROW EXECUTE FUNCTION nina.audit_chain();

-- Imutabilidade: UPDATE e TRUNCATE proibidos; DELETE somente pelo job de retencao
CREATE FUNCTION nina.forbid_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'DELETE'
     AND current_setting('nina.retention_purge', true) = 'on'
     AND pg_has_role(current_user, 'nina_worker', 'MEMBER') THEN
    RETURN OLD;
  END IF;
  RAISE EXCEPTION 'tabela % e append-only (% proibido)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'NN030';
END $$;
CREATE TRIGGER consent_record_immutable BEFORE UPDATE OR DELETE ON nina.consent_record FOR EACH ROW EXECUTE FUNCTION nina.forbid_mutation();
CREATE TRIGGER consent_record_no_truncate BEFORE TRUNCATE ON nina.consent_record FOR EACH STATEMENT EXECUTE FUNCTION nina.forbid_mutation();
CREATE TRIGGER audit_event_immutable BEFORE UPDATE OR DELETE ON nina.audit_event FOR EACH ROW EXECUTE FUNCTION nina.forbid_mutation();
CREATE TRIGGER audit_event_no_truncate BEFORE TRUNCATE ON nina.audit_event FOR EACH STATEMENT EXECUTE FUNCTION nina.forbid_mutation();
CREATE TRIGGER config_change_immutable BEFORE UPDATE OR DELETE ON nina.config_change FOR EACH ROW EXECUTE FUNCTION nina.forbid_mutation();
CREATE TRIGGER config_change_no_truncate BEFORE TRUNCATE ON nina.config_change FOR EACH STATEMENT EXECUTE FUNCTION nina.forbid_mutation();

CREATE FUNCTION nina.log_config_change() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  v_row jsonb := to_jsonb(coalesce(NEW, OLD));
  v_actor uuid := nina.current_user_id();
  v_key text;
BEGIN
  IF v_actor IS NULL AND coalesce(current_setting('nina.migration', true), '') <> 'on' THEN
    RAISE EXCEPTION 'alteracao de configuracao exige nina.user_id (auditoria, ADR-0005)' USING ERRCODE = 'NN040';
  END IF;
  v_key := CASE TG_TABLE_NAME
             WHEN 'plan_feature' THEN (v_row->>'plan_id') || ':' || (v_row->>'flag_key')
             WHEN 'plan' THEN v_row->>'code'
             WHEN 'feature_flag' THEN v_row->>'flag_key'
             ELSE v_row->>'param_key' END;
  INSERT INTO nina.config_change (table_name, op, record_key, old_row, new_row, actor_user_id, reason)
  VALUES (TG_TABLE_NAME, TG_OP, v_key,
          CASE WHEN TG_OP <> 'INSERT' THEN to_jsonb(OLD) END,
          CASE WHEN TG_OP <> 'DELETE' THEN to_jsonb(NEW) END,
          v_actor, nullif(current_setting('nina.change_reason', true), ''));
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, metadata_safe, is_critical)
  VALUES (v_actor, CASE WHEN v_actor IS NULL THEN 'system' ELSE 'admin' END, 'config.changed', TG_TABLE_NAME,
          jsonb_build_object('op', TG_OP, 'key', v_key), true);
  RETURN NULL;
END $$;

CREATE FUNCTION nina.bump_param_version() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN NEW.version := OLD.version + 1; END IF;
  NEW.updated_at := now();
  NEW.updated_by := nina.current_user_id();
  RETURN NEW;
END $$;
CREATE TRIGGER app_parameter_bump BEFORE INSERT OR UPDATE ON nina.app_parameter FOR EACH ROW EXECUTE FUNCTION nina.bump_param_version();

DO $$
DECLARE t text;
BEGIN
  FOREACH t IN ARRAY ARRAY['plan', 'feature_flag', 'plan_feature', 'app_parameter'] LOOP
    EXECUTE format('CREATE TRIGGER %I AFTER INSERT OR UPDATE OR DELETE ON nina.%I FOR EACH ROW EXECUTE FUNCTION nina.log_config_change()', t || '_audit', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['plan', 'feature_flag'] LOOP
    EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE ON nina.%I FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at()', t || '_touch', t);
  END LOOP;
  EXECUTE 'CREATE TRIGGER plan_feature_touch BEFORE UPDATE ON nina.plan_feature FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at()';
END $$;

CREATE TABLE nina.data_export_request (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id        uuid NOT NULL REFERENCES nina.app_user(id),
  requested_at   timestamptz NOT NULL DEFAULT now(),
  status         text NOT NULL DEFAULT 'requested' CHECK (status IN ('requested', 'processing', 'ready', 'failed', 'expired')),
  file_ref       text,                                   -- referencia p/ URL assinada; apagada ao expirar (7 dias)
  expires_at     timestamptz,
  schema_version integer NOT NULL DEFAULT 1,
  completed_at   timestamptz
);
CREATE INDEX data_export_user_ix ON nina.data_export_request (user_id, requested_at DESC);

CREATE TABLE nina.account_deletion_request (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id       uuid NOT NULL REFERENCES nina.app_user(id),
  requested_at  timestamptz NOT NULL DEFAULT now(),
  status        text NOT NULL DEFAULT 'requested' CHECK (status IN ('requested', 'scheduled', 'blocked', 'completed', 'cancelled')),
  scheduled_for timestamptz,                             -- janela de arrependimento (D-07)
  block_reason  text CHECK (block_reason IN ('owner_has_other_caregivers')),
  completed_at  timestamptz,
  cancelled_at  timestamptz
);
CREATE UNIQUE INDEX account_deletion_open_uq ON nina.account_deletion_request (user_id) WHERE status IN ('requested', 'scheduled', 'blocked');

-- -----------------------------------------------------------------------------
-- 9. Exclusão (ADR-0008) e purga/retenção
-- -----------------------------------------------------------------------------
-- Apaga o CONTEUDO do bebe de imediato e deixa casca-tombstone (sem PII) que
-- propaga a exclusao aos dispositivos; a casca some na purga (>= 90 dias).
CREATE FUNCTION nina.erase_baby(p_baby uuid, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
  PERFORM 1 FROM nina.baby WHERE id = p_baby AND deleted_at IS NULL FOR UPDATE;
  IF NOT FOUND THEN RETURN; END IF;                       -- idempotente
  DELETE FROM nina.sleep_session WHERE baby_id = p_baby;
  DELETE FROM nina.feeding_session WHERE baby_id = p_baby;
  DELETE FROM nina.pumping_session WHERE baby_id = p_baby;
  DELETE FROM nina.diaper_event WHERE baby_id = p_baby;
  DELETE FROM nina.sleep_schedule_preference WHERE baby_id = p_baby;
  DELETE FROM nina.sleep_prediction WHERE baby_id = p_baby;
  DELETE FROM nina.notification_job WHERE baby_id = p_baby;
  DELETE FROM nina.notification_preference WHERE baby_id = p_baby;
  DELETE FROM nina.sync_mutation WHERE baby_id = p_baby;
  DELETE FROM nina.change_log WHERE baby_id = p_baby AND entity_type <> 'baby';
  DELETE FROM nina.tombstone WHERE baby_id = p_baby AND entity_type <> 'baby';
  UPDATE nina.caregiver_membership
     SET status = 'revoked', revoked_at = now(), revoked_reason = 'baby_deleted',
         invited_email = NULL, invite_token_hash = NULL
   WHERE baby_id = p_baby AND status IN ('pending', 'active')
     AND NOT (role = 'owner');                            -- Owner fica ate o fim (check de INV-09 ignora bebe excluido)
  UPDATE nina.baby
     SET deleted_at = now(), display_name = NULL, birth_date = NULL, due_date = NULL,
         sex = NULL, photo_ref = NULL, last_modified_by = p_actor
   WHERE id = p_baby;                                     -- triggers: version, change_log 'delete', tombstone
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, is_critical)
  VALUES (p_actor, 'baby.erased', 'baby', p_baby, p_baby, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('baby', p_baby, 'BabyDeleted');
END $$;

-- Exclusao de conta. Politica padrao 'block' (ADR-0008 opcao b): NAO apaga dados
-- de outros cuidadores; recusa quando o usuario e Owner ativo de bebe com
-- outros membros ativos. Outras politicas aguardam decisao (DJ-09).
CREATE FUNCTION nina.erase_user(p_user uuid, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE v_policy text := nina.param_text('privacy.owner_deletion_policy', 'block'); r record;
BEGIN
  PERFORM 1 FROM nina.app_user WHERE id = p_user AND status <> 'deleted' FOR UPDATE;
  IF NOT FOUND THEN RETURN; END IF;                       -- idempotente
  IF v_policy <> 'block' THEN
    RAISE EXCEPTION 'politica % ainda nao implementada (ADR-0008 em aberto)', v_policy USING ERRCODE = 'NN005';
  END IF;
  IF EXISTS (SELECT 1 FROM nina.caregiver_membership o
              JOIN nina.baby b ON b.id = o.baby_id AND b.deleted_at IS NULL
             WHERE o.user_id = p_user AND o.role = 'owner' AND o.status = 'active'
               AND EXISTS (SELECT 1 FROM nina.caregiver_membership m
                            WHERE m.baby_id = o.baby_id AND m.status = 'active' AND m.user_id <> p_user)) THEN
    RAISE EXCEPTION 'OWNER_HAS_OTHER_CAREGIVERS: transferir a propriedade antes (ADR-0008)' USING ERRCODE = 'NN004';
  END IF;
  -- Bebes em que e o unico membro ativo: exclusao em cascata do conteudo
  FOR r IN SELECT o.baby_id FROM nina.caregiver_membership o
            WHERE o.user_id = p_user AND o.role = 'owner' AND o.status = 'active' LOOP
    PERFORM nina.erase_baby(r.baby_id, p_actor);
  END LOOP;
  -- Vinculos remanescentes (cuidador em bebe de outros): remover vinculo, preservar dados dos demais
  DELETE FROM nina.caregiver_membership WHERE user_id = p_user;
  UPDATE nina.caregiver_membership SET invited_by = NULL WHERE invited_by = p_user;
  DELETE FROM nina.notification_job WHERE user_id = p_user;
  DELETE FROM nina.notification_preference WHERE user_id = p_user;
  DELETE FROM nina.device_push_token WHERE user_id = p_user;
  DELETE FROM nina.recovery_request WHERE user_id = p_user;
  DELETE FROM nina.user_identity WHERE user_id = p_user;
  DELETE FROM nina.user_credential WHERE user_id = p_user;
  DELETE FROM nina.auth_session WHERE user_id = p_user;              -- cascata: refresh_token
  UPDATE nina.family_entitlement_member SET removed_at = now() WHERE user_id = p_user AND removed_at IS NULL;
  UPDATE nina.data_export_request SET file_ref = NULL, status = 'expired' WHERE user_id = p_user AND file_ref IS NOT NULL;
  UPDATE nina.app_user SET status = 'deleted', email = NULL, email_verified_at = NULL, locale = NULL,
         timezone = NULL, deleted_at = now() WHERE id = p_user;
  UPDATE nina.account_deletion_request SET status = 'completed', completed_at = now()
   WHERE user_id = p_user AND status IN ('requested', 'scheduled');
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, is_critical)
  VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'system' ELSE 'user' END, 'account.erased', 'user', p_user, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('user', p_user, 'AccountDeleted');
END $$;

-- Job de limpeza de sync (tombstones 90 dias). Idempotente, em lotes, com lock
-- consultivo; agendar diariamente no worker (nao depende de pg_cron).
CREATE FUNCTION nina.purge_expired_sync_data(p_batch integer DEFAULT 5000)
RETURNS TABLE (change_log_deleted bigint, tombstones_deleted bigint, mutations_deleted bigint)
LANGUAGE plpgsql AS $$
DECLARE
  v_log_days integer := nina.param_int('sync.changelog_retention_days', 90);
  v_cl bigint := 0; v_ts bigint := 0; v_mu bigint := 0; r record;
BEGIN
  IF NOT pg_try_advisory_xact_lock(hashtextextended('nina.purge_expired_sync_data', 0)) THEN
    RETURN QUERY SELECT 0::bigint, 0::bigint, 0::bigint; RETURN;
  END IF;

  -- 1) poda o change log e sobe o piso do cursor por bebe
  WITH del AS (
    DELETE FROM nina.change_log c
     WHERE (c.baby_id, c.sync_sequence) IN (
       SELECT baby_id, sync_sequence FROM nina.change_log
        WHERE changed_at < now() - make_interval(days => v_log_days)
        ORDER BY changed_at LIMIT p_batch)
    RETURNING c.baby_id, c.sync_sequence),
  mx AS (SELECT baby_id, max(sync_sequence) AS m FROM del GROUP BY baby_id),
  upd AS (UPDATE nina.baby_sync_head h SET purged_through = greatest(h.purged_through, mx.m)
            FROM mx WHERE h.baby_id = mx.baby_id RETURNING 1)
  SELECT count(*) INTO v_cl FROM del;

  -- 2) remove tombstone + linha fisica SO quando o evento de delete ja saiu do log
  FOR r IN SELECT t.baby_id, t.entity_type, t.entity_id
             FROM nina.tombstone t
             JOIN nina.baby_sync_head h ON h.baby_id = t.baby_id
            WHERE t.expires_at < now() AND t.version <= h.purged_through
            ORDER BY t.expires_at LIMIT p_batch
              FOR UPDATE OF t SKIP LOCKED LOOP
    CASE r.entity_type
      WHEN 'sleep_session'              THEN DELETE FROM nina.sleep_session WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'feeding_session'            THEN DELETE FROM nina.feeding_session WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'pumping_session'            THEN DELETE FROM nina.pumping_session WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'diaper_event'               THEN DELETE FROM nina.diaper_event WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'sleep_schedule_preference'  THEN DELETE FROM nina.sleep_schedule_preference WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'baby'                       THEN DELETE FROM nina.baby WHERE id = r.entity_id AND deleted_at IS NOT NULL;  -- cascata remove membros e head
      ELSE NULL;
    END CASE;
    DELETE FROM nina.tombstone WHERE baby_id = r.baby_id AND entity_type = r.entity_type AND entity_id = r.entity_id;
    v_ts := v_ts + 1;
  END LOOP;

  -- 3) idempotencia de push alem da janela de cursor nao tem mais utilidade
  DELETE FROM nina.sync_mutation WHERE mutation_id IN (
    SELECT mutation_id FROM nina.sync_mutation
     WHERE received_at < now() - make_interval(days => v_log_days) LIMIT p_batch);
  GET DIAGNOSTICS v_mu = ROW_COUNT;

  RETURN QUERY SELECT v_cl, v_ts, v_mu;
END $$;

-- Retencao operacional (privacy-spec secao 4). Prazos em app_parameter.
CREATE FUNCTION nina.purge_expired_operational_data(p_batch integer DEFAULT 5000) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE v jsonb := '{}'::jsonb; n bigint;
BEGIN
  IF NOT pg_try_advisory_xact_lock(hashtextextended('nina.purge_expired_operational_data', 0)) THEN RETURN v; END IF;

  DELETE FROM nina.auth_session WHERE id IN (SELECT id FROM nina.auth_session
     WHERE absolute_expires_at < now() - interval '30 days' OR revoked_at < now() - interval '30 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('auth_session', n);

  DELETE FROM nina.recovery_request WHERE id IN (SELECT id FROM nina.recovery_request
     WHERE expires_at < now() - interval '7 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('recovery_request', n);

  DELETE FROM nina.device_push_token WHERE id IN (SELECT id FROM nina.device_push_token
     WHERE last_seen_at < now() - make_interval(days => nina.param_int('push.token_inactivity_days', 60)) LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('device_push_token', n);

  DELETE FROM nina.sleep_prediction WHERE id IN (SELECT id FROM nina.sleep_prediction
     WHERE computed_at < now() - make_interval(days => nina.param_int('prediction.retention_days', 90)) LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('sleep_prediction', n);

  DELETE FROM nina.caregiver_membership WHERE id IN (SELECT id FROM nina.caregiver_membership
     WHERE status = 'pending' AND invite_expires_at < now() - interval '30 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('expired_invites', n);

  UPDATE nina.data_export_request SET file_ref = NULL, status = 'expired'
   WHERE status = 'ready' AND expires_at < now();
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('exports_expired', n);

  DELETE FROM nina.outbox_message WHERE id IN (SELECT id FROM nina.outbox_message
     WHERE processed_at < now() - interval '7 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('outbox', n);

  DELETE FROM nina.notification_job WHERE id IN (SELECT id FROM nina.notification_job
     WHERE status IN ('sent', 'cancelled', 'skipped_quiet_hours', 'dead_letter') AND updated_at < now() - interval '30 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('notification_job', n);
  RETURN v;
END $$;

-- -----------------------------------------------------------------------------
-- 10. Row-Level Security (defesa em profundidade - SEC-003)
--     Contexto: SET LOCAL nina.user_id = '<uuid>' no inicio de cada transacao.
--     Sem contexto => nenhuma linha visivel. nina_worker tem politica propria.
-- -----------------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
  -- baby-scoped: leitura por qualquer membro ativo, escrita por owner/caregiver
  FOREACH t IN ARRAY ARRAY['sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'sleep_schedule_preference', 'sleep_prediction'] LOOP
    EXECUTE format('ALTER TABLE nina.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY app_select ON nina.%I FOR SELECT TO nina_app USING (nina.can_read_baby(baby_id))', t);
    EXECUTE format('CREATE POLICY app_insert ON nina.%I FOR INSERT TO nina_app WITH CHECK (nina.can_write_baby(baby_id))', t);
    EXECUTE format('CREATE POLICY app_update ON nina.%I FOR UPDATE TO nina_app USING (nina.can_write_baby(baby_id)) WITH CHECK (nina.can_write_baby(baby_id))', t);
  END LOOP;
  -- somente leitura para o app (escritas feitas por triggers definer / worker)
  FOREACH t IN ARRAY ARRAY['change_log', 'tombstone'] LOOP
    EXECUTE format('ALTER TABLE nina.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY app_select ON nina.%I FOR SELECT TO nina_app USING (nina.can_read_baby(baby_id))', t);
  END LOOP;
  EXECUTE 'ALTER TABLE nina.sync_mutation ENABLE ROW LEVEL SECURITY';
  EXECUTE 'CREATE POLICY app_select ON nina.sync_mutation FOR SELECT TO nina_app USING (nina.can_read_baby(baby_id))';
  EXECUTE 'CREATE POLICY app_insert ON nina.sync_mutation FOR INSERT TO nina_app WITH CHECK (nina.can_write_baby(baby_id) AND user_id = nina.current_user_id())';
  -- por usuario
  FOREACH t IN ARRAY ARRAY['notification_preference', 'notification_job', 'auth_session', 'device_push_token', 'consent_record', 'data_export_request', 'account_deletion_request'] LOOP
    EXECUTE format('ALTER TABLE nina.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY app_select ON nina.%I FOR SELECT TO nina_app USING (user_id = nina.current_user_id())', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['notification_preference', 'auth_session', 'device_push_token', 'consent_record', 'data_export_request', 'account_deletion_request'] LOOP
    EXECUTE format('CREATE POLICY app_insert ON nina.%I FOR INSERT TO nina_app WITH CHECK (user_id = nina.current_user_id())', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['notification_preference', 'auth_session', 'device_push_token'] LOOP
    EXECUTE format('CREATE POLICY app_update ON nina.%I FOR UPDATE TO nina_app USING (user_id = nina.current_user_id()) WITH CHECK (user_id = nina.current_user_id())', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['notification_preference', 'device_push_token'] LOOP
    EXECUTE format('CREATE POLICY app_delete ON nina.%I FOR DELETE TO nina_app USING (user_id = nina.current_user_id())', t);
  END LOOP;
END $$;

-- baby
ALTER TABLE nina.baby ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.baby FOR SELECT TO nina_app
  USING (nina.can_read_baby(id) OR nina.is_family_owner(family_id));
CREATE POLICY app_insert ON nina.baby FOR INSERT TO nina_app WITH CHECK (nina.is_family_owner(family_id));
CREATE POLICY app_update ON nina.baby FOR UPDATE TO nina_app
  USING (nina.baby_role(id) = 'owner') WITH CHECK (nina.baby_role(id) = 'owner');

-- membership: usuario ve os proprios vinculos; Owner ve/gerencia os do bebe
ALTER TABLE nina.caregiver_membership ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.caregiver_membership FOR SELECT TO nina_app
  USING (user_id = nina.current_user_id() OR nina.baby_role(baby_id) = 'owner');
CREATE POLICY app_insert ON nina.caregiver_membership FOR INSERT TO nina_app
  WITH CHECK (nina.baby_role(baby_id) = 'owner'
              OR (user_id = nina.current_user_id() AND role = 'owner' AND status = 'active' AND nina.can_bootstrap_owner(baby_id)));
CREATE POLICY app_update ON nina.caregiver_membership FOR UPDATE TO nina_app
  USING (nina.baby_role(baby_id) = 'owner' OR user_id = nina.current_user_id())
  WITH CHECK (nina.baby_role(baby_id) = 'owner' OR user_id = nina.current_user_id());

-- politica do worker em todas as tabelas com RLS
DO $$
DECLARE t text;
BEGIN
  FOR t IN SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'nina' AND c.relkind = 'r' AND c.relrowsecurity LOOP
    EXECUTE format('CREATE POLICY worker_all ON nina.%I FOR ALL TO nina_worker USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- -----------------------------------------------------------------------------
-- 11. Privilégios
-- -----------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES IN SCHEMA nina FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA nina FROM PUBLIC;

-- worker: tudo (exceto TRUNCATE); append-only continua protegido por trigger
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA nina TO nina_worker;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA nina TO nina_worker;

-- app: leitura/escrita onde o modelo exige; sem DELETE em dados sincronizaveis
GRANT SELECT, INSERT, UPDATE ON
  nina.app_user, nina.user_credential, nina.user_identity, nina.auth_session, nina.refresh_token,
  nina.recovery_request, nina.device_push_token, nina.family, nina.baby, nina.caregiver_membership,
  nina.sleep_session, nina.feeding_session, nina.pumping_session, nina.diaper_event,
  nina.sleep_schedule_preference, nina.sleep_prediction, nina.sync_mutation,
  nina.notification_preference, nina.notification_job, nina.outbox_message,
  nina.subscription, nina.family_entitlement, nina.family_entitlement_member,
  nina.data_export_request, nina.account_deletion_request TO nina_app;
GRANT DELETE ON nina.notification_preference, nina.device_push_token, nina.refresh_token, nina.recovery_request TO nina_app;
GRANT SELECT ON nina.change_log, nina.tombstone, nina.plan, nina.feature_flag, nina.plan_feature,
  nina.app_parameter, nina.consent_purpose, nina.consent_current TO nina_app;
GRANT SELECT, INSERT ON nina.consent_record TO nina_app;
GRANT INSERT ON nina.audit_event TO nina_app;                     -- sem leitura pelo app
GRANT EXECUTE ON FUNCTION nina.current_user_id(), nina.user_plan_code(uuid), nina.user_has_feature(uuid, text),
  nina.param_int(text, integer), nina.param_text(text, text) TO nina_app;
-- helpers de RLS e triggers: executados pelo planner/trigger com o papel corrente
GRANT EXECUTE ON FUNCTION nina.baby_role(uuid), nina.can_read_baby(uuid), nina.can_write_baby(uuid),
  nina.is_family_owner(uuid), nina.can_bootstrap_owner(uuid) TO nina_app;
-- Funcoes de trigger (SECURITY DEFINER/INVOKER) precisam ser executaveis por quem dispara o trigger
GRANT EXECUTE ON FUNCTION nina.sync_stamp_child(), nina.sync_stamp_baby(), nina.sync_log_change(),
  nina.baby_validate(), nina.check_baby_has_owner(), nina.check_entitlement_member(), nina.touch_updated_at(),
  nina.audit_chain(), nina.forbid_mutation(), nina.log_config_change(), nina.bump_param_version(),
  nina.next_sync_sequence(uuid) TO nina_app, nina_config_admin;

-- config: editada somente por nina_config_admin (auditada por trigger)
GRANT SELECT, INSERT, UPDATE, DELETE ON nina.plan, nina.feature_flag, nina.plan_feature, nina.app_parameter TO nina_config_admin;
GRANT SELECT ON nina.config_change TO nina_config_admin;
GRANT INSERT ON nina.audit_event TO nina_config_admin;
GRANT EXECUTE ON FUNCTION nina.current_user_id(), nina.param_int(text, integer), nina.param_text(text, text) TO nina_config_admin;

-- -----------------------------------------------------------------------------
-- 12. Seeds (ADR-0005, ADR-0003, privacy-spec 3.2/4). Gates nascem sem flags: bloqueado por padrão.
-- -----------------------------------------------------------------------------
INSERT INTO nina.plan (id, code, name, rank, max_premium_members) VALUES
  (1, 'free',    'Gratuito', 0, 1),
  (2, 'premium', 'Premium',  1, 2);   -- titular + 1 adicional (ADR-0005)

INSERT INTO nina.app_parameter (param_key, value, value_type, description) VALUES
  ('sync.tombstone_retention_days',     '90'::jsonb,    'int',    'Retencao de tombstones (ADR-0003)'),
  ('sync.changelog_retention_days',     '90'::jsonb,    'int',    'Retencao do change log; cursor mais antigo => cursor expirado (ADR-0003)'),
  ('prediction.retention_days',         '90'::jsonb,    'int',    'Retencao de previsoes derivadas (privacy-spec 4)'),
  ('push.token_inactivity_days',        '60'::jsonb,    'int',    'Inatividade para apagar token de push (privacy-spec 4)'),
  ('privacy.owner_deletion_policy',     '"block"'::jsonb, 'string', 'ADR-0008 (aberto): block = recusar exclusao do Owner com outros cuidadores ativos');

INSERT INTO nina.consent_purpose (purpose_key, description, is_required, scope, current_version, in_mvp) VALUES
  ('terms_of_use',        'Aceite dos Termos de Uso',                                   true,  'user', '1.0.0', true),
  ('privacy_policy',      'Ciencia da Politica de Privacidade',                         true,  'user', '1.0.0', true),
  ('child_data_guardian', 'Declaracao de responsavel legal e tratamento de dados do bebe (art. 14)', true, 'baby', '1.0.0', true),
  ('analytics_product',   'Analytics de produto',                                       false, 'user', '1.0.0', true),
  ('marketing_email',     'E-mails promocionais',                                       false, 'user', '1.0.0', false),
  ('push_notifications',  'Registro de token e envio de push',                          false, 'user', '1.0.0', true);

INSERT INTO nina.schema_migration (version, description) VALUES ('0001', 'init: modelo fisico MVP (DB-001)');

COMMIT;
