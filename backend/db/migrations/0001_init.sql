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
-- Enums (ADR-0010): TODOS os CHECKs de valores fixos em MAIÚSCULAS (OWNER, NAP, ACTIVE...). Chaves de
-- catálogo/identificadores (plan.code, flag_key, param_key, purpose_key, audit_event.action) seguem em minúsculas.
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
  status      text NOT NULL DEFAULT 'GATED' CHECK (status IN ('GATED', 'OPEN', 'DISABLED')),
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
  value_type     text NOT NULL CHECK (value_type IN ('INT', 'BOOL', 'STRING', 'JSON')),
  description    text NOT NULL,
  schema_version integer NOT NULL DEFAULT 1 CHECK (schema_version >= 1),
  version        bigint NOT NULL DEFAULT 1,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  updated_by     uuid,
  CONSTRAINT app_parameter_type_ck CHECK (
       (value_type = 'INT'    AND jsonb_typeof(value) = 'number')
    OR (value_type = 'BOOL'   AND jsonb_typeof(value) = 'boolean')
    OR (value_type = 'STRING' AND jsonb_typeof(value) = 'string')
    OR (value_type = 'JSON'   AND jsonb_typeof(value) IN ('object', 'array')))
);

CREATE FUNCTION nina.param_int(p_key text, p_default integer) RETURNS integer
LANGUAGE sql STABLE AS
$$ SELECT coalesce((SELECT (value #>> '{}')::integer FROM nina.app_parameter
                     WHERE param_key = p_key AND value_type = 'INT'), p_default) $$;

CREATE FUNCTION nina.param_text(p_key text, p_default text) RETURNS text
LANGUAGE sql STABLE AS
$$ SELECT coalesce((SELECT value #>> '{}' FROM nina.app_parameter
                     WHERE param_key = p_key AND value_type = 'STRING'), p_default) $$;

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
  status            text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'PENDING_DELETION', 'DELETED')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  deleted_at        timestamptz,
  CONSTRAINT app_user_email_ck CHECK (
    (status = 'DELETED' AND email IS NULL AND deleted_at IS NOT NULL)
    OR (status <> 'DELETED' AND email IS NOT NULL AND position('@' IN email) > 1 AND length(email) <= 254))
);
COMMENT ON TABLE nina.app_user IS 'Apos exclusao de conta a linha permanece anonimizada (status=deleted, sem PII) para preservar integridade de consentimento/auditoria/fiscal.';
CREATE UNIQUE INDEX app_user_email_uq ON nina.app_user (email_normalized) WHERE email_normalized IS NOT NULL;
CREATE TRIGGER app_user_touch BEFORE UPDATE ON nina.app_user FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

CREATE TABLE nina.user_credential (
  user_id        uuid PRIMARY KEY REFERENCES nina.app_user(id) ON DELETE CASCADE,
  password_hash  text NOT NULL,                       -- Argon2id/bcrypt (formato PHC); nunca logar
  hash_algorithm text NOT NULL CHECK (hash_algorithm IN ('ARGON2ID', 'BCRYPT')),
  changed_at     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE nina.user_identity (   -- Google/Apple (ADR-0007); sem fusao automatica por e-mail
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id          uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  provider         text NOT NULL CHECK (provider IN ('GOOGLE', 'APPLE')),
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
  platform           text NOT NULL CHECK (platform IN ('IOS', 'ANDROID', 'WEB')),
  user_agent         text CHECK (length(user_agent) <= 256),
  app_version        text CHECK (length(app_version) <= 32),
  created_at         timestamptz NOT NULL DEFAULT now(),
  last_seen_at       timestamptz NOT NULL DEFAULT now(),
  absolute_expires_at timestamptz NOT NULL,           -- 30 dias absolutos (privacy-spec 4)
  revoked_at         timestamptz,
  revoked_reason     text CHECK (revoked_reason IN ('LOGOUT', 'USER_REVOKED', 'PASSWORD_CHANGED', 'REUSE_DETECTED', 'ACCOUNT_DELETED', 'ADMIN'))
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
  platform     text NOT NULL CHECK (platform IN ('APNS', 'FCM')),
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
  sex              text CHECK (sex IN ('FEMALE', 'MALE', 'UNSPECIFIED')),
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
  role              text NOT NULL CHECK (role IN ('OWNER', 'CAREGIVER', 'READ_ONLY')),
  status            text NOT NULL CHECK (status IN ('PENDING', 'ACTIVE', 'REVOKED', 'DECLINED')),
  invited_by        uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  invite_token_hash bytea UNIQUE CHECK (octet_length(invite_token_hash) = 32),
  invite_expires_at timestamptz,
  invited_at        timestamptz NOT NULL DEFAULT now(),
  accepted_at       timestamptz,
  revoked_at        timestamptz,
  revoked_reason    text CHECK (revoked_reason IN ('OWNER_REMOVED', 'LEFT', 'BABY_DELETED', 'ACCOUNT_DELETED', 'OWNERSHIP_TRANSFERRED')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT membership_active_ck  CHECK (status <> 'ACTIVE' OR (user_id IS NOT NULL AND accepted_at IS NOT NULL)),
  CONSTRAINT membership_pending_ck CHECK (status <> 'PENDING' OR user_id IS NOT NULL OR invited_email IS NOT NULL),
  CONSTRAINT membership_revoked_ck CHECK (status <> 'REVOKED' OR revoked_at IS NOT NULL),
  CONSTRAINT membership_owner_ck   CHECK (role <> 'OWNER' OR status IN ('ACTIVE', 'REVOKED'))
);
-- INV-10: no maximo um vinculo nao encerrado por (bebe, usuario)
CREATE UNIQUE INDEX membership_baby_user_uq ON nina.caregiver_membership (baby_id, user_id)
  WHERE user_id IS NOT NULL AND status IN ('PENDING', 'ACTIVE');
CREATE UNIQUE INDEX membership_baby_email_uq ON nina.caregiver_membership (baby_id, lower(invited_email))
  WHERE user_id IS NULL AND status = 'PENDING';
-- INV-09 (no maximo um): exatamente um Owner ativo e garantido junto com o constraint trigger abaixo
CREATE UNIQUE INDEX membership_one_owner_uq ON nina.caregiver_membership (baby_id)
  WHERE role = 'OWNER' AND status = 'ACTIVE';
CREATE INDEX membership_user_ix ON nina.caregiver_membership (user_id, baby_id) WHERE status = 'ACTIVE';
CREATE INDEX membership_invite_expiry_ix ON nina.caregiver_membership (invite_expires_at) WHERE status = 'PENDING';
CREATE TRIGGER membership_touch BEFORE UPDATE ON nina.caregiver_membership FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

-- INV-09 (pelo menos um) / SEC-007: bebe vivo sempre tem Owner ativo ao fim da transacao
CREATE FUNCTION nina.check_baby_has_owner() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_baby uuid := coalesce(NEW.baby_id, OLD.baby_id);
BEGIN
  IF EXISTS (SELECT 1 FROM nina.baby b WHERE b.id = v_baby AND b.deleted_at IS NULL)
     AND NOT EXISTS (SELECT 1 FROM nina.caregiver_membership m
                      WHERE m.baby_id = v_baby AND m.role = 'OWNER' AND m.status = 'ACTIVE') THEN
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
    WHERE m.baby_id = p_baby AND m.user_id = nina.current_user_id() AND m.status = 'ACTIVE' $$;

CREATE FUNCTION nina.can_read_baby(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT nina.baby_role(p_baby) IS NOT NULL $$;

CREATE FUNCTION nina.can_write_baby(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT coalesce(nina.baby_role(p_baby) IN ('OWNER', 'CAREGIVER'), false) $$;

CREATE FUNCTION nina.is_family_owner(p_family uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.family f WHERE f.id = p_family AND f.owner_user_id = nina.current_user_id()) $$;

CREATE FUNCTION nina.can_bootstrap_owner(p_baby uuid) RETURNS boolean   -- criador do bebe vira Owner (RB-006)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.baby b JOIN nina.family f ON f.id = b.family_id
                   WHERE b.id = p_baby AND f.owner_user_id = nina.current_user_id())
      AND NOT EXISTS (SELECT 1 FROM nina.caregiver_membership m WHERE m.baby_id = p_baby AND m.role = 'OWNER') $$;

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
  entity_type    text   NOT NULL CHECK (entity_type IN ('BABY', 'SLEEP_SESSION', 'FEEDING_SESSION', 'PUMPING_SESSION', 'DIAPER_EVENT', 'WAKE_EVENT', 'SLEEP_SCHEDULE_PREFERENCE')),
  entity_id      uuid   NOT NULL,
  op             text   NOT NULL CHECK (op IN ('UPSERT', 'DELETE')),
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
  op                text NOT NULL CHECK (op IN ('CREATE', 'UPDATE', 'DELETE')),
  base_version      bigint,
  client_created_at timestamptz NOT NULL,
  received_at       timestamptz NOT NULL DEFAULT now(),
  outcome           text NOT NULL CHECK (outcome IN ('APPLIED', 'MERGED', 'IGNORED_TOMBSTONE', 'REJECTED')),
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
    IF current_setting('nina.authorship_scrub', true) = 'on' THEN
      -- Eliminacao de autoria (ADR-0010, scrub_user_personal_data): so os ponteiros created_by/last_modified_by do
      -- usuario-alvo sao anulados; nada mais muda, sem nova versao e sem change_log (autoria nao faz parte do contrato).
      NEW := OLD;
      IF OLD.created_by = nullif(current_setting('nina.scrub_user', true), '')::uuid THEN NEW.created_by := NULL; END IF;
      IF OLD.last_modified_by = nullif(current_setting('nina.scrub_user', true), '')::uuid THEN NEW.last_modified_by := NULL; END IF;
      RETURN NEW;
    END IF;
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
    IF current_setting('nina.authorship_scrub', true) = 'on' THEN     -- ver sync_stamp_child
      NEW := OLD;
      IF OLD.created_by = nullif(current_setting('nina.scrub_user', true), '')::uuid THEN NEW.created_by := NULL; END IF;
      IF OLD.last_modified_by = nullif(current_setting('nina.scrub_user', true), '')::uuid THEN NEW.last_modified_by := NULL; END IF;
      RETURN NEW;
    END IF;
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
  IF TG_OP = 'UPDATE' AND NEW.version = OLD.version THEN RETURN NULL; END IF;   -- sem nova versao (scrub de autoria): nada a sincronizar
  IF TG_ARGV[0] = 'BABY' THEN v_baby := NEW.id; ELSE v_baby := NEW.baby_id; END IF;
  INSERT INTO nina.change_log (baby_id, sync_sequence, entity_type, entity_id, op, actor_user_id, device_id)
  VALUES (v_baby, NEW.version, TG_ARGV[0], NEW.id,
          CASE WHEN NEW.deleted_at IS NULL THEN 'UPSERT' ELSE 'DELETE' END,
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
CREATE TRIGGER baby_sync_log AFTER INSERT OR UPDATE ON nina.baby FOR EACH ROW EXECUTE FUNCTION nina.sync_log_change('BABY');

-- -----------------------------------------------------------------------------
-- 5. Tracking (sincronizáveis)
-- -----------------------------------------------------------------------------
CREATE TABLE nina.sleep_session (
  id               uuid PRIMARY KEY,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  sleep_type       text NOT NULL CHECK (sleep_type IN ('NAP', 'NIGHT')),
  method_or_place  text CHECK (length(method_or_place) <= 60),   -- D-08 (texto livre curto ate decisao)
  notes            text CHECK (length(notes) <= 2000),
  source           text NOT NULL CHECK (source IN ('TIMER', 'MANUAL')),
  version          bigint NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT sleep_interval_ck CHECK (end_at IS NULL OR end_at >= start_at),           -- INV-01
  CONSTRAINT sleep_session_id_baby_uq UNIQUE (id, baby_id)                              -- alvo da FK composta de wake_event
);
CREATE UNIQUE INDEX sleep_one_open_uq ON nina.sleep_session (baby_id)                  -- INV-02
  WHERE end_at IS NULL AND deleted_at IS NULL;
CREATE INDEX sleep_timeline_ix ON nina.sleep_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.feeding_session (
  id               uuid PRIMARY KEY,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  feeding_type     text NOT NULL CHECK (feeding_type IN ('BREASTFEEDING', 'BOTTLE', 'SOLID', 'OTHER')),   -- ADR-0009; clientes toleram valores novos
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  side             text CHECK (side IN ('LEFT', 'RIGHT', 'BOTH')),
  volume_ml        numeric(6,1) CHECK (volume_ml > 0 AND volume_ml <= 5000),
  -- ADR-0009: so se aplica a mamadeira (feeding_shape_ck); UNKNOWN reservado a migracao de dados antigos
  milk_type        text CHECK (milk_type IN ('BREAST_MILK', 'FORMULA', 'MIXED', 'OTHER', 'UNSPECIFIED', 'UNKNOWN')),
  notes            text CHECK (length(notes) <= 2000),
  version          bigint NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT feeding_interval_ck CHECK (end_at IS NULL OR end_at >= start_at),         -- INV-01
  CONSTRAINT feeding_shape_ck CHECK (                                                   -- INV-07 / ADR-0009
       (feeding_type = 'BREASTFEEDING' AND side IS NOT NULL AND end_at IS NOT NULL
          AND volume_ml IS NULL AND milk_type IS NULL)
    OR (feeding_type = 'BOTTLE' AND side IS NULL)                                       -- unico tipo com milk_type
    OR (feeding_type = 'SOLID' AND side IS NULL AND volume_ml IS NULL AND milk_type IS NULL)
    OR (feeding_type = 'OTHER' AND side IS NULL AND milk_type IS NULL))
);
COMMENT ON COLUMN nina.feeding_session.end_at IS 'ADR-0010 item 5: OBRIGATORIO quando feeding_type = BREASTFEEDING (feeding_shape_ck); opcional nos demais tipos.';
COMMENT ON COLUMN nina.feeding_session.milk_type IS 'ADR-0009: NULL quando feeding_type <> BOTTLE (nao se aplica). Em BOTTLE, NULL = indisponivel; UNSPECIFIED = usuario nao especificou.';
CREATE INDEX feeding_timeline_ix ON nina.feeding_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.pumping_session (
  id               uuid PRIMARY KEY,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  volume_ml        numeric(6,1) CHECK (volume_ml > 0 AND volume_ml <= 5000),
  side             text CHECK (side IN ('LEFT', 'RIGHT', 'BOTH')),
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
  diaper_type      text NOT NULL CHECK (diaper_type IN ('WET', 'DIRTY', 'MIXED', 'DRY', 'UNSPECIFIED')),   -- ADR-0009 (WET urina; DIRTY fezes; MIXED ambos; DRY verificada sem nada)
  notes            text CHECK (length(notes) <= 2000),
  version          bigint NOT NULL DEFAULT 0,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL
);
CREATE INDEX diaper_timeline_ix ON nina.diaper_event (baby_id, occurred_at DESC) WHERE deleted_at IS NULL;

-- Despertar noturno (ADR-0009): fonte da verdade de nightAwakenings (que e DERIVADO, nunca persistido).
CREATE TABLE nina.wake_event (
  id                  uuid PRIMARY KEY,
  baby_id             uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  sleep_session_id    uuid NOT NULL,
  started_at          timestamptz NOT NULL,
  ended_at            timestamptz,
  tz                  nina.iana_tz NOT NULL,
  -- derivada de started_at/ended_at (nunca divergente); NULL enquanto ended_at for NULL
  duration_seconds    integer GENERATED ALWAYS AS (
                        CASE WHEN ended_at IS NULL THEN NULL
                             ELSE extract(epoch FROM (ended_at - started_at))::integer END) STORED,
  source              text NOT NULL CHECK (source IN ('MANUAL', 'INFERRED', 'IMPORT')),
  -- correcao manual: marca que o usuario ajustou o evento e preserva o valor original (ex.: inferido)
  manually_corrected  boolean NOT NULL DEFAULT false,
  original_started_at timestamptz,
  original_ended_at   timestamptz,
  version             bigint NOT NULL DEFAULT 0,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  deleted_at          timestamptz,
  created_by          uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by    uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT wake_interval_ck CHECK (ended_at IS NULL OR ended_at >= started_at),
  CONSTRAINT wake_original_ck CHECK (manually_corrected OR (original_started_at IS NULL AND original_ended_at IS NULL)),
  CONSTRAINT wake_original_interval_ck CHECK (original_ended_at IS NULL OR original_started_at IS NULL OR original_ended_at >= original_started_at),
  -- mesma crianca da sessao de sono (impede apontar para sessao de outro bebe)
  CONSTRAINT wake_session_fk FOREIGN KEY (sleep_session_id, baby_id)
    REFERENCES nina.sleep_session (id, baby_id) ON DELETE CASCADE
);
COMMENT ON TABLE nina.wake_event IS 'ADR-0009: despertares dentro de uma sessao de sono. Sincronizavel (change_log/tombstone). Limites dentro da sessao NAO sao impostos no banco (edicao offline); validar na API.';
CREATE INDEX wake_session_ix ON nina.wake_event (sleep_session_id) WHERE deleted_at IS NULL;
CREATE INDEX wake_timeline_ix ON nina.wake_event (baby_id, started_at DESC) WHERE deleted_at IS NULL;

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
  kind               text NOT NULL CHECK (kind IN ('NEXT_NAP', 'BEDTIME')),
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
  FOREACH t IN ARRAY ARRAY['sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'wake_event', 'sleep_schedule_preference'] LOOP
    EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE ON nina.%I FOR EACH ROW EXECUTE FUNCTION nina.sync_stamp_child()', t || '_stamp', t);
    EXECUTE format('CREATE TRIGGER %I AFTER INSERT OR UPDATE ON nina.%I FOR EACH ROW EXECUTE FUNCTION nina.sync_log_change(%L)', t || '_sync_log', t, upper(t));
  END LOOP;
END $$;

-- Excluir uma sessao de sono exclui (tombstone) seus despertares, para os dispositivos convergirem
CREATE FUNCTION nina.sleep_session_cascade_wake() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  UPDATE nina.wake_event
     SET deleted_at = NEW.deleted_at, last_modified_by = NEW.last_modified_by
   WHERE sleep_session_id = NEW.id AND deleted_at IS NULL;
  RETURN NULL;
END $$;
CREATE TRIGGER sleep_session_wake_cascade AFTER UPDATE ON nina.sleep_session
  FOR EACH ROW WHEN (OLD.deleted_at IS NULL AND NEW.deleted_at IS NOT NULL)
  EXECUTE FUNCTION nina.sleep_session_cascade_wake();

-- Politica de sobreposicao de sono (ADR-0009): app_parameter 'sleep.overlap_policy'
--   accept_and_warn (padrao): aceita e a API sinaliza (nina.sleep_overlaps); reject: recusa (NN006).
-- Nome 'zz': dispara DEPOIS de sleep_session_stamp, que ja segurou o lock do contador do bebe;
-- isso serializa escritores concorrentes do mesmo bebe e torna a checagem confiavel.
CREATE FUNCTION nina.sleep_overlap_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF TG_OP = 'UPDATE' AND NEW.start_at IS NOT DISTINCT FROM OLD.start_at AND NEW.end_at IS NOT DISTINCT FROM OLD.end_at THEN
    RETURN NEW;      -- intervalo inalterado: nao re-julga dados aceitos sob a politica anterior
  END IF;
  IF NEW.deleted_at IS NULL AND nina.param_text('sleep.overlap_policy', 'ACCEPT_AND_WARN') = 'REJECT'
     AND EXISTS (SELECT 1 FROM nina.sleep_session o
                  WHERE o.baby_id = NEW.baby_id AND o.id <> NEW.id AND o.deleted_at IS NULL
                    AND tstzrange(o.start_at, coalesce(o.end_at, 'infinity'), '[)')
                        && tstzrange(NEW.start_at, coalesce(NEW.end_at, 'infinity'), '[)')) THEN
    RAISE EXCEPTION 'SLEEP_OVERLAP: sessao de sono sobrepoe outra (sleep.overlap_policy=reject)' USING ERRCODE = 'NN006';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER sleep_session_zz_overlap BEFORE INSERT OR UPDATE ON nina.sleep_session
  FOR EACH ROW EXECUTE FUNCTION nina.sleep_overlap_guard();

-- Sinalizacao (accept_and_warn): ids das sessoes que se sobrepoem a p_session (respeita RLS do chamador)
CREATE FUNCTION nina.sleep_overlaps(p_session uuid) RETURNS SETOF uuid
LANGUAGE sql STABLE AS $$
  SELECT o.id FROM nina.sleep_session s
    JOIN nina.sleep_session o ON o.baby_id = s.baby_id AND o.id <> s.id AND o.deleted_at IS NULL
   WHERE s.id = p_session AND s.deleted_at IS NULL
     AND tstzrange(o.start_at, coalesce(o.end_at, 'infinity'), '[)') && tstzrange(s.start_at, coalesce(s.end_at, 'infinity'), '[)')
   ORDER BY o.start_at $$;

-- nightAwakenings DERIVADO (ADR-0009): NULL = dados insuficientes; 0 = acompanhamento suficiente sem despertar; N>0.
-- Havendo despertares registrados, o numero e devolvido (ha evidencia). Sem eles, 'suficiente' = sessao
-- noturna encerrada com duracao >= app_parameter 'sleep.night_awakenings.min_session_minutes'.
CREATE FUNCTION nina.night_awakenings(p_session uuid) RETURNS integer
LANGUAGE sql STABLE AS $$
  SELECT CASE
           WHEN s.id IS NULL OR s.sleep_type <> 'NIGHT' THEN NULL
           WHEN w.n > 0 THEN w.n
           WHEN s.end_at IS NOT NULL
                AND s.end_at - s.start_at >= make_interval(mins => nina.param_int('sleep.night_awakenings.min_session_minutes', 240)) THEN 0
           ELSE NULL
         END
    FROM (SELECT 1) x
    LEFT JOIN nina.sleep_session s ON s.id = p_session AND s.deleted_at IS NULL
    CROSS JOIN LATERAL (SELECT count(*)::integer AS n FROM nina.wake_event e
                         WHERE e.sleep_session_id = p_session AND e.deleted_at IS NULL) w $$;

-- Idade (ADR-0009): SEMPRE derivada; nenhuma idade corrigida e persistida.
-- corrected = cronologica - (due_date - birth_date), so se birth_date < due_date e dentro da janela
-- 'age.corrected_window_months' (idade cronologica). corrected_days pode ser negativo (antes do termo).
CREATE FUNCTION nina.age_calculation(p_birth date, p_due date, p_on date DEFAULT current_date)
RETURNS TABLE (chronological_days integer, corrected_days integer, correction_applied boolean)
LANGUAGE sql STABLE AS $$
  SELECT (p_on - p_birth),
         CASE WHEN a.ok THEN (p_on - p_birth) - (p_due - p_birth) END,
         a.ok
    FROM (SELECT p_due IS NOT NULL AND p_birth < p_due
                 AND p_on < (p_birth + make_interval(months => nina.param_int('age.corrected_window_months', 24)))::date AS ok) a $$;

-- -----------------------------------------------------------------------------
-- 6. Notificações e outbox
-- -----------------------------------------------------------------------------
CREATE TABLE nina.notification_preference (
  user_id             uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  baby_id             uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  category            text NOT NULL CHECK (category IN ('NAP', 'BEDTIME', 'ROUTINE', 'DEVELOPMENT_PHASE', 'SYSTEM')),
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
  category        text NOT NULL CHECK (category IN ('NAP', 'BEDTIME', 'ROUTINE', 'DEVELOPMENT_PHASE', 'SYSTEM')),
  scheduled_at    timestamptz NOT NULL,
  status          text NOT NULL DEFAULT 'SCHEDULED' CHECK (status IN ('SCHEDULED', 'SENT', 'FAILED', 'CANCELLED', 'SKIPPED_QUIET_HOURS', 'DEAD_LETTER')),
  attempts        smallint NOT NULL DEFAULT 0,
  next_attempt_at timestamptz,
  last_error_code text CHECK (length(last_error_code) <= 64),              -- sem PII
  provider_ref    text,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX notification_job_due_ix ON nina.notification_job (scheduled_at) WHERE status IN ('SCHEDULED', 'FAILED');
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
  store                     text NOT NULL CHECK (store IN ('APPLE', 'GOOGLE')),
  product_ref               text NOT NULL,
  original_transaction_ref  text NOT NULL,
  store_transaction_ref     text NOT NULL,
  status                    text NOT NULL CHECK (status IN ('ACTIVE', 'GRACE', 'EXPIRED', 'CANCELLED', 'REVOKED')),
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
  source          text NOT NULL CHECK (source IN ('SUBSCRIPTION', 'MANUAL_GRANT')),
  subscription_id uuid REFERENCES nina.subscription(id),
  status          text NOT NULL CHECK (status IN ('ACTIVE', 'GRACE', 'EXPIRED', 'REVOKED')),
  valid_from      timestamptz NOT NULL DEFAULT now(),
  valid_until     timestamptz,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  CHECK ((source = 'SUBSCRIPTION') = (subscription_id IS NOT NULL)),
  CHECK (valid_until IS NULL OR valid_until > valid_from)
);
CREATE UNIQUE INDEX family_entitlement_one_live_uq ON nina.family_entitlement (family_id) WHERE status IN ('ACTIVE', 'GRACE');
CREATE TRIGGER family_entitlement_touch BEFORE UPDATE ON nina.family_entitlement FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

CREATE TABLE nina.family_entitlement_member (
  entitlement_id uuid NOT NULL REFERENCES nina.family_entitlement(id) ON DELETE CASCADE,
  user_id        uuid NOT NULL REFERENCES nina.app_user(id),
  member_role    text NOT NULL CHECK (member_role IN ('HOLDER', 'ADDITIONAL')),
  added_at       timestamptz NOT NULL DEFAULT now(),
  removed_at     timestamptz,
  PRIMARY KEY (entitlement_id, user_id)
);
CREATE UNIQUE INDEX fem_one_holder_uq ON nina.family_entitlement_member (entitlement_id) WHERE member_role = 'HOLDER' AND removed_at IS NULL;
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
  IF NEW.member_role = 'HOLDER' AND NEW.user_id <> v_owner THEN
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
       AND e.status IN ('ACTIVE', 'GRACE')
       AND e.valid_from <= now() AND (e.valid_until IS NULL OR e.valid_until > now())
     ORDER BY p.rank DESC LIMIT 1), 'free') $$;

CREATE FUNCTION nina.user_has_feature(p_user uuid, p_flag text) RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT coalesce((
    SELECT CASE f.status
             WHEN 'OPEN' THEN true
             WHEN 'DISABLED' THEN false
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
  scope           text NOT NULL CHECK (scope IN ('USER', 'BABY')),
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
  status          text NOT NULL CHECK (status IN ('GRANTED', 'REVOKED')),
  recorded_at     timestamptz NOT NULL DEFAULT now(),
  source          text NOT NULL CHECK (source IN ('ONBOARDING', 'SETTINGS', 'PROMPT', 'INVITE_ACCEPT', 'SYSTEM')),
  app_version     text,
  platform        text CHECK (platform IN ('IOS', 'ANDROID', 'WEB', 'SERVER'))
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
  actor_type    text NOT NULL DEFAULT 'USER' CHECK (actor_type IN ('USER', 'SYSTEM', 'ADMIN', 'SUPPORT')),
  action        text NOT NULL CHECK (action ~ '^[a-z][a-z0-9_.]*$'),
  entity_type   text,
  entity_id     uuid,
  baby_id       uuid,                                   -- sem FK
  device_id     uuid,
  request_id    text,
  result        text NOT NULL DEFAULT 'SUCCESS' CHECK (result IN ('SUCCESS', 'FAILURE', 'DENIED')),
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
  VALUES (v_actor, CASE WHEN v_actor IS NULL THEN 'SYSTEM' ELSE 'ADMIN' END, 'config.changed', TG_TABLE_NAME,
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

-- Valida valores das flags/parametros de dominio (ADR-0009) para um valor invalido nunca chegar a producao
CREATE FUNCTION nina.validate_app_parameter() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  CASE NEW.param_key
    WHEN 'sleep.overlap_policy' THEN
      IF NEW.value #>> '{}' NOT IN ('ACCEPT_AND_WARN', 'REJECT') THEN
        RAISE EXCEPTION 'sleep.overlap_policy invalida: % (accept_and_warn|reject)', NEW.value #>> '{}' USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'privacy.owner_deletion_policy' THEN
      IF NEW.value #>> '{}' NOT IN ('CASCADE', 'BLOCK', 'TRANSFER_OWNERSHIP') THEN
        RAISE EXCEPTION 'privacy.owner_deletion_policy invalida: % (cascade|block|transfer_ownership)', NEW.value #>> '{}' USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'age.corrected_window_months' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}')::numeric NOT BETWEEN 1 AND 120 THEN
        RAISE EXCEPTION 'age.corrected_window_months deve ser int entre 1 e 120' USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'sleep.night_awakenings.min_session_minutes' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}')::numeric NOT BETWEEN 0 AND 1440 THEN
        RAISE EXCEPTION 'sleep.night_awakenings.min_session_minutes deve ser int entre 0 e 1440' USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'privacy.deletion_grace_days' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}') !~ '^[0-9]+$'
         OR (NEW.value #>> '{}')::numeric NOT BETWEEN 1 AND 30 THEN
        RAISE EXCEPTION 'privacy.deletion_grace_days deve ser int entre 1 e 30' USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'privacy.request_response_days' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}') !~ '^[0-9]+$'
         OR (NEW.value #>> '{}')::numeric NOT BETWEEN 1 AND 60 THEN
        RAISE EXCEPTION 'privacy.request_response_days deve ser int entre 1 e 60' USING ERRCODE = 'check_violation';
      END IF;
    ELSE NULL;
  END CASE;
  RETURN NEW;
END $$;
CREATE TRIGGER app_parameter_validate BEFORE INSERT OR UPDATE ON nina.app_parameter FOR EACH ROW EXECUTE FUNCTION nina.validate_app_parameter();

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
  status         text NOT NULL DEFAULT 'REQUESTED' CHECK (status IN ('REQUESTED', 'PROCESSING', 'READY', 'FAILED', 'EXPIRED')),
  file_ref       text,                                   -- referencia p/ URL assinada; apagada ao expirar (7 dias)
  expires_at     timestamptz,
  schema_version integer NOT NULL DEFAULT 1,
  completed_at   timestamptz
);
CREATE INDEX data_export_user_ix ON nina.data_export_request (user_id, requested_at DESC);

-- Exclusao de conta (ADR-0010 item 3): SO o Owner ativo de um bebe pode solicitar. Nao-Owner usa privacy_request.
-- Janela de arrependimento (ADR-0010 item 6): scheduled_for = requested_at + privacy.deletion_grace_days (7),
-- calculado e congelado pelo trigger account_deletion_guard (o cliente nao decide); cancelavel ate scheduled_for.
CREATE TABLE nina.account_deletion_request (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id       uuid NOT NULL REFERENCES nina.app_user(id),
  requested_at  timestamptz NOT NULL DEFAULT now(),
  status        text NOT NULL DEFAULT 'SCHEDULED' CHECK (status IN ('SCHEDULED', 'BLOCKED', 'COMPLETED', 'CANCELLED')),
  grace_days    integer NOT NULL CHECK (grace_days >= 1),          -- valor do parametro no momento do pedido
  scheduled_for timestamptz NOT NULL,                              -- = requested_at + grace_days; erase_user nunca antes
  block_reason  text CHECK (block_reason IN ('OWNER_HAS_OTHER_CAREGIVERS')),
  -- ADR-0009: confirmacao explicita (reautenticacao) registrada pela API ao criar o pedido; exigida pela
  -- politica 'CASCADE' quando a exclusao apaga dados de bebe com outros cuidadores ativos
  confirmed_at        timestamptz,
  confirmation_method text CHECK (confirmation_method IN ('REAUTHENTICATION')),
  policy_applied      text CHECK (policy_applied IN ('CASCADE', 'BLOCK', 'TRANSFER_OWNERSHIP')),   -- preenchido por erase_user
  completed_at  timestamptz,
  cancelled_at  timestamptz,
  CONSTRAINT deletion_confirmation_ck CHECK ((confirmed_at IS NULL) = (confirmation_method IS NULL)),
  CONSTRAINT deletion_schedule_ck CHECK (scheduled_for >= requested_at)
);
CREATE UNIQUE INDEX account_deletion_open_uq ON nina.account_deletion_request (user_id) WHERE status IN ('SCHEDULED', 'BLOCKED');

CREATE FUNCTION nina.account_deletion_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    NEW.requested_at := now();
    NEW.grace_days := nina.param_int('privacy.deletion_grace_days', 7);
    NEW.scheduled_for := NEW.requested_at + make_interval(days => NEW.grace_days);
    NEW.status := 'SCHEDULED';
    NEW.block_reason := NULL; NEW.policy_applied := NULL; NEW.completed_at := NULL; NEW.cancelled_at := NULL;
    INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe, is_critical)
    VALUES (NEW.user_id, 'account.deletion_requested', 'USER', NEW.user_id,
            jsonb_build_object('request_id', NEW.id, 'grace_days', NEW.grace_days, 'scheduled_for', NEW.scheduled_for,
                               'confirmed', NEW.confirmed_at IS NOT NULL), true);
    RETURN NEW;
  END IF;
  -- UPDATE
  IF NEW.id <> OLD.id OR NEW.user_id <> OLD.user_id OR NEW.requested_at <> OLD.requested_at
     OR NEW.grace_days <> OLD.grace_days OR NEW.scheduled_for <> OLD.scheduled_for
     OR NEW.confirmed_at IS DISTINCT FROM OLD.confirmed_at OR NEW.confirmation_method IS DISTINCT FROM OLD.confirmation_method THEN
    RAISE EXCEPTION 'pedido de exclusao: id/usuario/janela/confirmacao sao imutaveis' USING ERRCODE = 'NN001';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status THEN
    IF OLD.status IN ('COMPLETED', 'CANCELLED') THEN
      RAISE EXCEPTION 'pedido de exclusao ja encerrado (%)', OLD.status USING ERRCODE = 'NN011';
    END IF;
    IF NEW.status = 'CANCELLED' THEN
      IF OLD.status = 'SCHEDULED' AND now() >= OLD.scheduled_for THEN     -- BLOCKED nunca executa: pode ser cancelado a qualquer tempo
        RAISE EXCEPTION 'CANCEL_WINDOW_CLOSED: a janela de arrependimento terminou em %', OLD.scheduled_for USING ERRCODE = 'NN011';
      END IF;
      NEW.cancelled_at := now();
      INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe, is_critical)
      VALUES (OLD.user_id, 'account.deletion_cancelled', 'USER', OLD.user_id, jsonb_build_object('request_id', OLD.id), true);
    ELSIF NEW.status = 'COMPLETED' THEN
      IF now() < OLD.scheduled_for THEN
        RAISE EXCEPTION 'DELETION_GRACE_NOT_ELAPSED: execucao so apos %', OLD.scheduled_for USING ERRCODE = 'NN008';
      END IF;
    ELSIF NEW.status = 'SCHEDULED' THEN
      RAISE EXCEPTION 'transicao % -> SCHEDULED nao permitida', OLD.status USING ERRCODE = 'NN011';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER account_deletion_request_guard BEFORE INSERT OR UPDATE ON nina.account_deletion_request
  FOR EACH ROW EXECUTE FUNCTION nina.account_deletion_guard();

-- Requisicao de privacidade (LGPD art. 18) de QUEM NAO PODE excluir a conta (ADR-0010 item 3): Caregiver/ReadOnly ou
-- sem vinculo. Eliminacao/anonimizacao dos PROPRIOS dados pessoais SEM tocar nos dados do bebe (pertencem ao Owner).
-- Abertura/cancelamento so por funcoes (open_privacy_request/cancel_privacy_request: auditam); atendimento pelo worker.
CREATE TABLE nina.privacy_request (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id             uuid NOT NULL REFERENCES nina.app_user(id),
  request_type        text NOT NULL CHECK (request_type IN ('ACCESS', 'CORRECTION', 'EXPORT', 'ERASURE')),
  status              text NOT NULL DEFAULT 'REQUESTED' CHECK (status IN ('REQUESTED', 'COMPLETED', 'REJECTED', 'CANCELLED')),
  requested_at        timestamptz NOT NULL DEFAULT now(),
  due_at              timestamptz NOT NULL,                       -- requested_at + privacy.request_response_days (15)
  identity_verified_at timestamptz,                               -- verificacao de identidade (privacy-spec 5)
  verification_method text CHECK (verification_method IN ('REAUTHENTICATION')),
  completed_at        timestamptz,
  cancelled_at        timestamptz,
  closed_reason       text CHECK (length(closed_reason) <= 64),   -- codigo curto, sem PII
  CONSTRAINT privacy_verification_ck CHECK ((identity_verified_at IS NULL) = (verification_method IS NULL)),
  CONSTRAINT privacy_erasure_verified_ck CHECK (request_type <> 'ERASURE' OR identity_verified_at IS NOT NULL)
);
CREATE UNIQUE INDEX privacy_request_open_uq ON nina.privacy_request (user_id, request_type) WHERE status = 'REQUESTED';
CREATE INDEX privacy_request_due_ix ON nina.privacy_request (due_at) WHERE status = 'REQUESTED';
COMMENT ON TABLE nina.privacy_request IS 'ADR-0010 item 3: caminho de DSAR para nao-Owner. ERASURE anonimiza dados pessoais proprios (fulfill_privacy_erasure) e nunca apaga dado de bebe.';

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
  DELETE FROM nina.wake_event WHERE baby_id = p_baby;
  DELETE FROM nina.sleep_session WHERE baby_id = p_baby;
  DELETE FROM nina.feeding_session WHERE baby_id = p_baby;
  DELETE FROM nina.pumping_session WHERE baby_id = p_baby;
  DELETE FROM nina.diaper_event WHERE baby_id = p_baby;
  DELETE FROM nina.sleep_schedule_preference WHERE baby_id = p_baby;
  DELETE FROM nina.sleep_prediction WHERE baby_id = p_baby;
  DELETE FROM nina.notification_job WHERE baby_id = p_baby;
  DELETE FROM nina.notification_preference WHERE baby_id = p_baby;
  DELETE FROM nina.sync_mutation WHERE baby_id = p_baby;
  DELETE FROM nina.change_log WHERE baby_id = p_baby AND entity_type <> 'BABY';
  DELETE FROM nina.tombstone WHERE baby_id = p_baby AND entity_type <> 'BABY';
  UPDATE nina.caregiver_membership
     SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'BABY_DELETED',
         invited_email = NULL, invite_token_hash = NULL
   WHERE baby_id = p_baby AND status IN ('PENDING', 'ACTIVE')
     AND NOT (role = 'OWNER');                            -- Owner fica ate o fim (check de INV-09 ignora bebe excluido)
  UPDATE nina.baby
     SET deleted_at = now(), display_name = NULL, birth_date = NULL, due_date = NULL,
         sex = NULL, photo_ref = NULL, last_modified_by = p_actor
   WHERE id = p_baby;                                     -- triggers: version, change_log 'DELETE', tombstone
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, is_critical)
  VALUES (p_actor, 'baby.erased', 'BABY', p_baby, p_baby, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('BABY', p_baby, 'BabyDeleted');
END $$;

-- E o Owner ativo de ao menos um bebe vivo? (so ele exclui a conta - ADR-0010 item 3)
CREATE FUNCTION nina.is_active_baby_owner(p_user uuid) RETURNS boolean
LANGUAGE sql STABLE AS
$$ SELECT EXISTS (SELECT 1 FROM nina.caregiver_membership m
                    JOIN nina.baby b ON b.id = m.baby_id AND b.deleted_at IS NULL
                   WHERE m.user_id = p_user AND m.role = 'OWNER' AND m.status = 'ACTIVE') $$;

-- Remove/anonimiza os dados pessoais PROPRIOS de um usuario, sem tocar em dados de bebe (ADR-0010 item 3).
-- Comum a erase_user (depois de tratar os bebes do Owner) e a fulfill_privacy_erasure (nao-Owner).
-- Devolve so contagens (usadas na auditoria, sem PII).
--  * consentimentos: append-only; os ainda concedidos ganham linha REVOKED (source SYSTEM). A prova minima
--    (ids, finalidade, versao, datas) permanece ligada a app_user ja anonimizado (privacy-spec 4, F12).
--  * autoria: created_by/last_modified_by que apontam para o usuario viram NULL nos dados de bebe (inclusive
--    tombstones), sem nova versao e sem change_log (nina.authorship_scrub, ver triggers de sync).
--  * vinculos de cuidador, preferencias/jobs de notificacao, tokens de push, recuperacao, identidades,
--    credencial, sessoes/refresh: apagados. Entitlement: membro removido. Exports: file_ref zerado.
--  * app_user: anonimizado (status DELETED, sem e-mail/locale/fuso).
CREATE FUNCTION nina.scrub_user_personal_data(p_user uuid, p_actor uuid DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE
  n_consent integer; n_auth integer := 0; n_tmp integer; n_member integer; n_push integer; n_sess integer; t text;
BEGIN
  INSERT INTO nina.consent_record (user_id, subject_baby_id, purpose_key, policy_version, text_hash, locale, status, source, platform)
  SELECT c.user_id, c.subject_baby_id, c.purpose_key, c.policy_version, c.text_hash, c.locale, 'REVOKED', 'SYSTEM', 'SERVER'
    FROM nina.consent_current c WHERE c.user_id = p_user AND c.status = 'GRANTED';
  GET DIAGNOSTICS n_consent = ROW_COUNT;

  PERFORM set_config('nina.authorship_scrub', 'on', true);
  PERFORM set_config('nina.scrub_user', p_user::text, true);
  FOREACH t IN ARRAY ARRAY['baby', 'sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'wake_event', 'sleep_schedule_preference'] LOOP
    EXECUTE format('UPDATE nina.%I SET created_by = created_by WHERE created_by = $1 OR last_modified_by = $1', t) USING p_user;
    GET DIAGNOSTICS n_tmp = ROW_COUNT; n_auth := n_auth + n_tmp;
  END LOOP;
  PERFORM set_config('nina.authorship_scrub', 'off', true);
  PERFORM set_config('nina.scrub_user', '', true);

  UPDATE nina.caregiver_membership SET invited_by = NULL WHERE invited_by = p_user;
  DELETE FROM nina.caregiver_membership WHERE user_id = p_user;
  GET DIAGNOSTICS n_member = ROW_COUNT;
  DELETE FROM nina.notification_job WHERE user_id = p_user;
  DELETE FROM nina.notification_preference WHERE user_id = p_user;
  DELETE FROM nina.device_push_token WHERE user_id = p_user;
  GET DIAGNOSTICS n_push = ROW_COUNT;
  DELETE FROM nina.recovery_request WHERE user_id = p_user;
  DELETE FROM nina.user_identity WHERE user_id = p_user;
  DELETE FROM nina.user_credential WHERE user_id = p_user;
  DELETE FROM nina.auth_session WHERE user_id = p_user;              -- cascata: refresh_token
  GET DIAGNOSTICS n_sess = ROW_COUNT;
  UPDATE nina.family_entitlement_member SET removed_at = now() WHERE user_id = p_user AND removed_at IS NULL;
  UPDATE nina.data_export_request SET file_ref = NULL, status = 'EXPIRED' WHERE user_id = p_user AND file_ref IS NOT NULL;
  UPDATE nina.app_user SET status = 'DELETED', email = NULL, email_verified_at = NULL, locale = NULL,
         timezone = NULL, deleted_at = now() WHERE id = p_user;
  RETURN jsonb_build_object('consents_revoked', n_consent, 'authorship_rows', n_auth, 'memberships_removed', n_member,
                            'push_tokens', n_push, 'sessions', n_sess);
END $$;

-- Exclusao de conta (ADR-0008 + ADR-0009 + ADR-0010). Politica em app_parameter 'privacy.owner_deletion_policy':
--   cascade (PADRAO): apaga o bebe do qual o usuario e Owner MESMO com outros cuidadores ativos.
--       Exige confirmacao registrada (account_deletion_request.confirmed_at, reautenticacao) quando ha
--       outros cuidadores (NN007) e grava auditoria por bebe compartilhado. Validacao juridica: DJ-09.
--   block: recusa (NN004) se Owner ativo de bebe com outros membros ativos; nada de terceiros e tocado.
--   transfer_ownership: promove o cuidador ativo mais antigo (caregiver antes de read_only) a Owner,
--       move o bebe para a familia dele e segue com a exclusao; sem outro membro, o bebe e apagado.
-- Bebes em que o usuario e o unico membro ativo sao sempre apagados.
-- ADR-0010: o BANCO exige (i) pedido SCHEDULED do usuario, (ii) now() >= scheduled_for (NN008 antes disso),
-- (iii) usuario ainda Owner ativo de bebe vivo (NN012; nao-Owner usa privacy_request). Sem pedido: NN009.
CREATE FUNCTION nina.erase_user(p_user uuid, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
  v_policy text := nina.param_text('privacy.owner_deletion_policy', 'CASCADE');
  v_scrub jsonb;
  r record;
  v_req nina.account_deletion_request%ROWTYPE;
  v_new_owner uuid;
  v_family uuid;
BEGIN
  PERFORM 1 FROM nina.app_user WHERE id = p_user AND status <> 'DELETED' FOR UPDATE;
  IF NOT FOUND THEN RETURN; END IF;                       -- idempotente
  IF v_policy NOT IN ('CASCADE', 'BLOCK', 'TRANSFER_OWNERSHIP') THEN
    RAISE EXCEPTION 'politica % desconhecida (cascade|block|transfer_ownership)', v_policy USING ERRCODE = 'NN005';
  END IF;
  SELECT * INTO v_req FROM nina.account_deletion_request
   WHERE user_id = p_user AND status = 'SCHEDULED' ORDER BY requested_at DESC LIMIT 1
     FOR UPDATE;                                           -- serializa com um cancelamento concorrente
  IF v_req.id IS NULL THEN
    RAISE EXCEPTION 'DELETION_REQUEST_REQUIRED: nao ha pedido de exclusao agendado para o usuario' USING ERRCODE = 'NN009';
  END IF;
  IF now() < v_req.scheduled_for THEN
    RAISE EXCEPTION 'DELETION_GRACE_NOT_ELAPSED: execucao so apos % (janela de arrependimento)', v_req.scheduled_for USING ERRCODE = 'NN008';
  END IF;
  IF NOT nina.is_active_baby_owner(p_user) THEN
    RAISE EXCEPTION 'NOT_BABY_OWNER: somente Owner ativo de um bebe exclui a conta; use privacy_request (ADR-0010)' USING ERRCODE = 'NN012';
  END IF;

  -- Pre-checagens (antes de qualquer alteracao)
  IF EXISTS (SELECT 1 FROM nina.caregiver_membership o
              JOIN nina.baby b ON b.id = o.baby_id AND b.deleted_at IS NULL
             WHERE o.user_id = p_user AND o.role = 'OWNER' AND o.status = 'ACTIVE'
               AND EXISTS (SELECT 1 FROM nina.caregiver_membership m
                            WHERE m.baby_id = o.baby_id AND m.status = 'ACTIVE' AND m.user_id <> p_user)) THEN
    IF v_policy = 'BLOCK' THEN
      RAISE EXCEPTION 'OWNER_HAS_OTHER_CAREGIVERS: transferir a propriedade antes (ADR-0008)' USING ERRCODE = 'NN004';
    ELSIF v_policy = 'CASCADE' AND (v_req.id IS NULL OR v_req.confirmed_at IS NULL) THEN
      RAISE EXCEPTION 'CASCADE_CONFIRMATION_REQUIRED: apagar dados de outros cuidadores exige confirmacao registrada (ADR-0009)' USING ERRCODE = 'NN007';
    END IF;
  END IF;

  FOR r IN SELECT o.baby_id,
                  (SELECT count(*) FROM nina.caregiver_membership m
                    WHERE m.baby_id = o.baby_id AND m.status = 'ACTIVE' AND m.user_id <> p_user)::integer AS others
             FROM nina.caregiver_membership o
             JOIN nina.baby b ON b.id = o.baby_id AND b.deleted_at IS NULL
            WHERE o.user_id = p_user AND o.role = 'OWNER' AND o.status = 'ACTIVE'
            ORDER BY o.baby_id LOOP
    IF r.others > 0 AND v_policy = 'TRANSFER_OWNERSHIP' THEN
      SELECT m.user_id INTO v_new_owner FROM nina.caregiver_membership m
       WHERE m.baby_id = r.baby_id AND m.status = 'ACTIVE' AND m.user_id <> p_user
       ORDER BY (m.role = 'CAREGIVER') DESC, m.accepted_at, m.id LIMIT 1;
      UPDATE nina.caregiver_membership
         SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'OWNERSHIP_TRANSFERRED'
       WHERE baby_id = r.baby_id AND user_id = p_user AND role = 'OWNER' AND status = 'ACTIVE';
      UPDATE nina.caregiver_membership SET role = 'OWNER'
       WHERE baby_id = r.baby_id AND user_id = v_new_owner AND status = 'ACTIVE';
      SELECT id INTO v_family FROM nina.family WHERE owner_user_id = v_new_owner;
      IF v_family IS NULL THEN
        INSERT INTO nina.family (owner_user_id) VALUES (v_new_owner) RETURNING id INTO v_family;
      END IF;
      UPDATE nina.baby SET family_id = v_family, last_modified_by = p_actor WHERE id = r.baby_id;
      INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
      VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'SYSTEM' ELSE 'USER' END, 'baby.ownership_transferred', 'BABY', r.baby_id, r.baby_id,
              jsonb_build_object('reason', 'owner_account_deletion'), true);
      INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
      VALUES ('BABY', r.baby_id, 'BabyOwnershipTransferred');
    ELSE
      IF r.others > 0 THEN   -- cascade sobre bebe compartilhado: auditoria com a confirmacao registrada
        INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
        VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'SYSTEM' ELSE 'USER' END, 'account.cascade_shared_baby_erased', 'BABY', r.baby_id, r.baby_id,
                jsonb_build_object('other_active_members', r.others, 'confirmed_at', v_req.confirmed_at,
                                   'confirmation_method', v_req.confirmation_method, 'request_id', v_req.id), true);
      END IF;
      PERFORM nina.erase_baby(r.baby_id, p_actor);
    END IF;
  END LOOP;
  -- Demais dados pessoais: vinculos como cuidador em bebes de terceiros (os eventos que ele criou permanecem, pois
  -- pertencem ao bebe; a autoria e anulada), tokens, sessoes, credenciais, consentimentos revogados, anonimizacao
  v_scrub := nina.scrub_user_personal_data(p_user, p_actor);
  UPDATE nina.account_deletion_request SET status = 'COMPLETED', completed_at = now(), policy_applied = v_policy
   WHERE id = v_req.id;
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'SYSTEM' ELSE 'USER' END, 'account.erased', 'USER', p_user,
          jsonb_build_object('policy', v_policy, 'request_id', v_req.id) || v_scrub, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('USER', p_user, 'AccountDeleted');
END $$;

-- ---- Requisicao de privacidade (ADR-0010 item 3) ----
-- Abre o pedido do PROPRIO usuario (nina.user_id). A identidade ja foi verificada pela API (reautenticacao).
-- ERASURE nao e aceita de Owner ativo de bebe (NN013): ele usa a exclusao de conta, que trata os bebes.
CREATE FUNCTION nina.open_privacy_request(p_type text, p_verification text DEFAULT 'REAUTHENTICATION') RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_id uuid; v_due timestamptz;
BEGIN
  IF v_user IS NULL OR NOT EXISTS (SELECT 1 FROM nina.app_user WHERE id = v_user AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'requisicao de privacidade exige usuario ativo no contexto (nina.user_id)' USING ERRCODE = 'NN015';
  END IF;
  IF p_type = 'ERASURE' AND nina.is_active_baby_owner(v_user) THEN
    RAISE EXCEPTION 'OWNER_MUST_USE_ACCOUNT_DELETION: Owner ativo exclui a conta pelo fluxo de exclusao (ADR-0010)' USING ERRCODE = 'NN013';
  END IF;
  IF p_type = 'ERASURE' AND p_verification IS NULL THEN
    RAISE EXCEPTION 'IDENTITY_NOT_VERIFIED: eliminacao exige verificacao de identidade' USING ERRCODE = 'NN014';
  END IF;
  v_due := now() + make_interval(days => nina.param_int('privacy.request_response_days', 15));
  INSERT INTO nina.privacy_request (user_id, request_type, due_at, identity_verified_at, verification_method)
  VALUES (v_user, p_type, v_due, CASE WHEN p_verification IS NOT NULL THEN now() END, p_verification)
  RETURNING id INTO v_id;
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (v_user, 'privacy.request_opened', 'PRIVACY_REQUEST', v_id,
          jsonb_build_object('request_type', p_type, 'due_at', v_due), true);
  RETURN v_id;
END $$;

CREATE FUNCTION nina.cancel_privacy_request(p_request uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id();
BEGIN
  UPDATE nina.privacy_request SET status = 'CANCELLED', cancelled_at = now(), closed_reason = 'USER_CANCELLED'
   WHERE id = p_request AND user_id = v_user AND status = 'REQUESTED';
  IF NOT FOUND THEN
    RAISE EXCEPTION 'requisicao inexistente, de outro usuario ou ja encerrada' USING ERRCODE = 'NN015';
  END IF;
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (v_user, 'privacy.request_cancelled', 'PRIVACY_REQUEST', p_request, jsonb_build_object('request_id', p_request), true);
END $$;

-- Worker: encerra ACCESS/CORRECTION/EXPORT (cumpridos pela API) como COMPLETED ou REJECTED. ERASURE so por fulfill.
CREATE FUNCTION nina.close_privacy_request(p_request uuid, p_status text, p_reason text DEFAULT NULL, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE v_req nina.privacy_request%ROWTYPE;
BEGIN
  IF p_status NOT IN ('COMPLETED', 'REJECTED') THEN
    RAISE EXCEPTION 'status de encerramento invalido: %', p_status USING ERRCODE = 'NN015';
  END IF;
  SELECT * INTO v_req FROM nina.privacy_request WHERE id = p_request AND status = 'REQUESTED' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'requisicao inexistente ou ja encerrada' USING ERRCODE = 'NN015'; END IF;
  IF v_req.request_type = 'ERASURE' AND p_status = 'COMPLETED' THEN
    RAISE EXCEPTION 'ERASURE so e concluida por fulfill_privacy_erasure' USING ERRCODE = 'NN015';
  END IF;
  UPDATE nina.privacy_request SET status = p_status, completed_at = now(), closed_reason = left(p_reason, 64) WHERE id = p_request;
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'SYSTEM' ELSE 'ADMIN' END, 'privacy.request_closed', 'PRIVACY_REQUEST', p_request,
          jsonb_build_object('request_type', v_req.request_type, 'status', p_status, 'on_time', now() <= v_req.due_at), true);
END $$;

-- Worker: cumpre ERASURE de nao-Owner. Anonimiza os dados pessoais proprios (scrub_user_personal_data) e NAO apaga
-- nada de bebe. Recusa Owner ativo (NN013) e pedido sem identidade verificada (NN014).
CREATE FUNCTION nina.fulfill_privacy_erasure(p_request uuid, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE v_req nina.privacy_request%ROWTYPE; v_scrub jsonb;
BEGIN
  SELECT * INTO v_req FROM nina.privacy_request WHERE id = p_request FOR UPDATE;
  IF NOT FOUND OR v_req.request_type <> 'ERASURE' THEN
    RAISE EXCEPTION 'requisicao ERASURE inexistente' USING ERRCODE = 'NN015';
  END IF;
  IF v_req.status = 'COMPLETED' THEN RETURN; END IF;                  -- idempotente
  IF v_req.status <> 'REQUESTED' THEN
    RAISE EXCEPTION 'requisicao encerrada (%)', v_req.status USING ERRCODE = 'NN015';
  END IF;
  IF v_req.identity_verified_at IS NULL THEN
    RAISE EXCEPTION 'IDENTITY_NOT_VERIFIED' USING ERRCODE = 'NN014';
  END IF;
  PERFORM 1 FROM nina.app_user WHERE id = v_req.user_id AND status <> 'DELETED' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'usuario ja excluido' USING ERRCODE = 'NN015'; END IF;
  IF nina.is_active_baby_owner(v_req.user_id) THEN
    RAISE EXCEPTION 'OWNER_MUST_USE_ACCOUNT_DELETION' USING ERRCODE = 'NN013';
  END IF;
  v_scrub := nina.scrub_user_personal_data(v_req.user_id, p_actor);
  UPDATE nina.privacy_request SET status = 'COMPLETED', completed_at = now(), closed_reason = 'FULFILLED' WHERE id = p_request;
  UPDATE nina.privacy_request SET status = 'CANCELLED', cancelled_at = now(), closed_reason = 'USER_ERASED'
   WHERE user_id = v_req.user_id AND status = 'REQUESTED';            -- demais pedidos abertos perdem o objeto
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'SYSTEM' ELSE 'ADMIN' END, 'privacy.erasure_completed', 'USER', v_req.user_id,
          jsonb_build_object('request_id', p_request, 'on_time', now() <= v_req.due_at) || v_scrub, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('USER', v_req.user_id, 'PersonalDataErased');
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
      WHEN 'SLEEP_SESSION'              THEN DELETE FROM nina.sleep_session WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'FEEDING_SESSION'            THEN DELETE FROM nina.feeding_session WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'PUMPING_SESSION'            THEN DELETE FROM nina.pumping_session WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'DIAPER_EVENT'               THEN DELETE FROM nina.diaper_event WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'WAKE_EVENT'                 THEN DELETE FROM nina.wake_event WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'SLEEP_SCHEDULE_PREFERENCE'  THEN DELETE FROM nina.sleep_schedule_preference WHERE id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'BABY'                       THEN DELETE FROM nina.baby WHERE id = r.entity_id AND deleted_at IS NOT NULL;  -- cascata remove membros e head
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
     WHERE status = 'PENDING' AND invite_expires_at < now() - interval '30 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('expired_invites', n);

  UPDATE nina.data_export_request SET file_ref = NULL, status = 'EXPIRED'
   WHERE status = 'READY' AND expires_at < now();
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('exports_expired', n);

  DELETE FROM nina.outbox_message WHERE id IN (SELECT id FROM nina.outbox_message
     WHERE processed_at < now() - interval '7 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('outbox', n);

  DELETE FROM nina.notification_job WHERE id IN (SELECT id FROM nina.notification_job
     WHERE status IN ('SENT', 'CANCELLED', 'SKIPPED_QUIET_HOURS', 'DEAD_LETTER') AND updated_at < now() - interval '30 days' LIMIT p_batch);
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
  FOREACH t IN ARRAY ARRAY['sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'wake_event', 'sleep_schedule_preference', 'sleep_prediction'] LOOP
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
  FOREACH t IN ARRAY ARRAY['notification_preference', 'auth_session', 'device_push_token', 'consent_record', 'data_export_request'] LOOP
    EXECUTE format('CREATE POLICY app_insert ON nina.%I FOR INSERT TO nina_app WITH CHECK (user_id = nina.current_user_id())', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['notification_preference', 'auth_session', 'device_push_token'] LOOP
    EXECUTE format('CREATE POLICY app_update ON nina.%I FOR UPDATE TO nina_app USING (user_id = nina.current_user_id()) WITH CHECK (user_id = nina.current_user_id())', t);
  END LOOP;
  FOREACH t IN ARRAY ARRAY['notification_preference', 'device_push_token'] LOOP
    EXECUTE format('CREATE POLICY app_delete ON nina.%I FOR DELETE TO nina_app USING (user_id = nina.current_user_id())', t);
  END LOOP;
END $$;

-- Exclusao de conta (ADR-0009): somente o Owner. Interpretacao [P]: pode pedir quem e Owner ativo de ao menos
-- um bebe ou quem nao tem nenhum vinculo ativo (conta sem bebe); quem so e caregiver/read_only deve antes
-- sair dos bebes (revogar o proprio vinculo).
CREATE FUNCTION nina.can_request_account_deletion() RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT nina.is_active_baby_owner(nina.current_user_id()) $$;
CREATE POLICY app_insert ON nina.account_deletion_request FOR INSERT TO nina_app
  WITH CHECK (user_id = nina.current_user_id() AND nina.can_request_account_deletion());
-- cancelamento pelo proprio usuario; o trigger account_deletion_guard limita a SCHEDULED/BLOCKED -> CANCELLED e a janela
CREATE POLICY app_update ON nina.account_deletion_request FOR UPDATE TO nina_app
  USING (user_id = nina.current_user_id() AND status IN ('SCHEDULED', 'BLOCKED'))
  WITH CHECK (user_id = nina.current_user_id() AND status = 'CANCELLED');

-- privacy_request: o usuario so le os proprios; abertura/cancelamento por funcoes (auditadas)
ALTER TABLE nina.privacy_request ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.privacy_request FOR SELECT TO nina_app USING (user_id = nina.current_user_id());

-- baby (perfil editavel somente pelo Owner - ADR-0009)
ALTER TABLE nina.baby ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.baby FOR SELECT TO nina_app
  USING (nina.can_read_baby(id) OR nina.is_family_owner(family_id));
CREATE POLICY app_insert ON nina.baby FOR INSERT TO nina_app WITH CHECK (nina.is_family_owner(family_id));
CREATE POLICY app_update ON nina.baby FOR UPDATE TO nina_app
  USING (nina.baby_role(id) = 'OWNER') WITH CHECK (nina.baby_role(id) = 'OWNER');

-- membership: usuario ve os proprios vinculos; Owner ve/gerencia os do bebe
ALTER TABLE nina.caregiver_membership ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.caregiver_membership FOR SELECT TO nina_app
  USING (user_id = nina.current_user_id() OR nina.baby_role(baby_id) = 'OWNER');
CREATE POLICY app_insert ON nina.caregiver_membership FOR INSERT TO nina_app
  WITH CHECK (nina.baby_role(baby_id) = 'OWNER'
              OR (user_id = nina.current_user_id() AND role = 'OWNER' AND status = 'ACTIVE' AND nina.can_bootstrap_owner(baby_id)));
CREATE POLICY app_update ON nina.caregiver_membership FOR UPDATE TO nina_app
  USING (nina.baby_role(baby_id) = 'OWNER' OR user_id = nina.current_user_id())
  WITH CHECK (nina.baby_role(baby_id) = 'OWNER' OR user_id = nina.current_user_id());

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
  nina.sleep_session, nina.feeding_session, nina.pumping_session, nina.diaper_event, nina.wake_event,
  nina.sleep_schedule_preference, nina.sleep_prediction, nina.sync_mutation,
  nina.notification_preference, nina.notification_job, nina.outbox_message,
  nina.subscription, nina.family_entitlement, nina.family_entitlement_member,
  nina.data_export_request, nina.account_deletion_request TO nina_app;
GRANT SELECT ON nina.privacy_request TO nina_app;                  -- escrita so via funcoes definer
GRANT DELETE ON nina.notification_preference, nina.device_push_token, nina.refresh_token, nina.recovery_request TO nina_app;
GRANT SELECT ON nina.change_log, nina.tombstone, nina.plan, nina.feature_flag, nina.plan_feature,
  nina.app_parameter, nina.consent_purpose, nina.consent_current TO nina_app;
GRANT SELECT, INSERT ON nina.consent_record TO nina_app;
GRANT INSERT ON nina.audit_event TO nina_app;                     -- sem leitura pelo app
GRANT EXECUTE ON FUNCTION nina.current_user_id(), nina.user_plan_code(uuid), nina.user_has_feature(uuid, text),
  nina.param_int(text, integer), nina.param_text(text, text),
  nina.sleep_overlaps(uuid), nina.night_awakenings(uuid), nina.age_calculation(date, date, date) TO nina_app;
-- helpers de RLS e triggers: executados pelo planner/trigger com o papel corrente
GRANT EXECUTE ON FUNCTION nina.baby_role(uuid), nina.can_read_baby(uuid), nina.can_write_baby(uuid),
  nina.is_family_owner(uuid), nina.can_bootstrap_owner(uuid), nina.can_request_account_deletion(),
  nina.is_active_baby_owner(uuid) TO nina_app;
GRANT EXECUTE ON FUNCTION nina.open_privacy_request(text, text), nina.cancel_privacy_request(uuid) TO nina_app;
-- Funcoes de trigger (SECURITY DEFINER/INVOKER) precisam ser executaveis por quem dispara o trigger
GRANT EXECUTE ON FUNCTION nina.sync_stamp_child(), nina.sync_stamp_baby(), nina.sync_log_change(),
  nina.baby_validate(), nina.check_baby_has_owner(), nina.check_entitlement_member(), nina.touch_updated_at(),
  nina.audit_chain(), nina.forbid_mutation(), nina.log_config_change(), nina.bump_param_version(),
  nina.next_sync_sequence(uuid), nina.sleep_session_cascade_wake(), nina.sleep_overlap_guard(),
  nina.validate_app_parameter(), nina.account_deletion_guard() TO nina_app, nina_config_admin;

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
  ('sync.tombstone_retention_days',     '90'::jsonb,    'INT',    'Retencao de tombstones (ADR-0003)'),
  ('sync.changelog_retention_days',     '90'::jsonb,    'INT',    'Retencao do change log; cursor mais antigo => cursor expirado (ADR-0003)'),
  ('prediction.retention_days',         '90'::jsonb,    'INT',    'Retencao de previsoes derivadas (privacy-spec 4)'),
  ('push.token_inactivity_days',        '60'::jsonb,    'INT',    'Inatividade para apagar token de push (privacy-spec 4)'),
  ('privacy.owner_deletion_policy',     '"CASCADE"'::jsonb, 'STRING', 'ADR-0009: politica de exclusao de conta. CASCADE (padrao) = apaga o bebe tambem para outros cuidadores, com confirmacao registrada; BLOCK = recusa; TRANSFER_OWNERSHIP = promove cuidador mais antigo'),
  ('sleep.overlap_policy',              '"ACCEPT_AND_WARN"'::jsonb, 'STRING', 'ADR-0009: sono sobreposto. ACCEPT_AND_WARN (padrao) = aceita e sinaliza; REJECT = recusa (NN006)'),
  ('age.corrected_window_months',       '24'::jsonb,    'INT',    'ADR-0009: janela (idade cronologica, em meses) em que a idade corrigida se aplica. Padrao aceito (ADR-0010 item 4); editavel'),
  ('sleep.night_awakenings.min_session_minutes', '240'::jsonb, 'INT', 'ADR-0009: duracao minima (min) de sessao noturna encerrada para considerar o acompanhamento suficiente (0 despertares em vez de nulo). Padrao aceito (ADR-0010 item 4); editavel'),
  ('privacy.deletion_grace_days',       '7'::jsonb,     'INT',    'ADR-0010 item 6: janela de arrependimento da exclusao de conta, em dias (1..30). scheduled_for = requested_at + este valor (congelado no pedido); erase_user nao executa antes'),
  ('privacy.request_response_days',     '15'::jsonb,    'INT',    'ADR-0010 item 3 / privacy-spec 5: prazo de atendimento de requisicao de privacidade (DSAR), em dias (1..60); due_at. Validacao juridica: DJ-07');

INSERT INTO nina.consent_purpose (purpose_key, description, is_required, scope, current_version, in_mvp) VALUES
  ('terms_of_use',        'Aceite dos Termos de Uso',                                   true,  'USER', '1.0.0', true),
  ('privacy_policy',      'Ciencia da Politica de Privacidade',                         true,  'USER', '1.0.0', true),
  ('child_data_guardian', 'Declaracao de responsavel legal e tratamento de dados do bebe (art. 14)', true, 'BABY', '1.0.0', true),
  ('analytics_product',   'Analytics de produto',                                       false, 'USER', '1.0.0', true),
  ('marketing_email',     'E-mails promocionais',                                       false, 'USER', '1.0.0', false),
  ('push_notifications',  'Registro de token e envio de push',                          false, 'USER', '1.0.0', true);

INSERT INTO nina.schema_migration (version, description) VALUES ('0001', 'init: modelo fisico MVP (DB-001) + convencoes ADR-0009');

COMMIT;
