-- =============================================================================
-- Nina - migração 0001_init (DB-001) - revisada pelo SECURITY-REVIEW-001 (+ reteste NR-03..NR-13) e pelo spike ARCH-003
-- Modelo físico PostgreSQL. Especificação: specs/database-spec.md
-- Alvo: PostgreSQL 15+ (validado em 16). Sem extensões externas.
-- Executar com: psql -v ON_ERROR_STOP=1 -f 0001_init.sql  (uma transação), como DONO do schema.
-- Convenções: schema `nina`; instantes em timestamptz (UTC); IANA tz em colunas
-- `tz`/`timezone`; PKs uuid gerados no cliente quando sincronizáveis (escopadas por bebê: PK (baby_id, id)).
-- Papéis (NOLOGIN, a infraestrutura concede membership aos logins reais; UM login por papel, nunca dois):
--   nina_app          runtime da API/BFF (sujeito a RLS; privilégios mínimos por tabela/coluna; escreve dados
--                     sensíveis só por funções SECURITY DEFINER estreitas)
--   nina_worker       jobs/outbox/purga/exclusão (política RLS própria; SEM DML em tabelas append-only)
--   nina_config_admin edição de flags/planos/parâmetros (auditada; sem INSERT em auditoria)
-- A aplicação NUNCA deve conectar como dono das tabelas nem como superuser (ambos ignoram RLS).
-- Enums (ADR-0010): TODOS os CHECKs de valores fixos em MAIÚSCULAS (OWNER, NAP, ACTIVE...). Chaves de
-- catálogo/identificadores (plan.code, flag_key, param_key, purpose_key, audit_event.action) seguem em minúsculas.
-- Controles privilegiados (autoria, purga, propriedade, vínculo) NÃO dependem de GUC definível pelo chamador:
-- usam fichas por transação (nina.guard_arm/guard_ok) que só código SECURITY DEFINER do dono consegue emitir.
-- Fatos que só a API pode atestar (reautenticação emitida, código de e-mail, verificação social) entram assinados por uma chave
-- servidor<->banco (nina.server_key, instalada pelo dono com nina.provision_server_key; sem ela essas funções falham fechado).
-- =============================================================================

BEGIN;

SET LOCAL TIME ZONE 'UTC';
SET LOCAL check_function_bodies = on;

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
  -- Reafirma os atributos (SR-012): papel de aplicação nunca privilegiado. Em nuvem gerenciada sem CREATEROLE, só avisa.
  BEGIN
    ALTER ROLE nina_app          NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
    ALTER ROLE nina_worker       NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
    ALTER ROLE nina_config_admin NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
  EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE 'sem privilegio para reafirmar atributos dos papeis nina_*; verifique NOSUPERUSER/NOBYPASSRLS na infraestrutura';
  END;
  -- Sem TEMP para PUBLIC (SR-012); idem aviso se o executor nao for dono do banco.
  BEGIN
    EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
  EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE 'sem privilegio para revogar TEMP de PUBLIC no banco %', current_database();
  END;
  -- NR-04: limites de tempo POR PAPEL (um cliente lento ou malicioso nao segura locks, transacoes nem conexoes indefinidamente).
  -- ALTER ROLE ... SET vale para sessoes que entram COMO esse papel; a infraestrutura que cria logins membros dos papeis
  -- nina_* deve repetir os mesmos valores no login (ver nina.apply_role_limits e infra/docker/initdb/10-bootstrap.sh).
  BEGIN
    ALTER ROLE nina_app          SET statement_timeout = '15s';
    ALTER ROLE nina_app          SET lock_timeout = '5s';
    ALTER ROLE nina_app          SET idle_in_transaction_session_timeout = '30s';
    ALTER ROLE nina_worker       SET statement_timeout = '10min';
    ALTER ROLE nina_worker       SET lock_timeout = '30s';
    ALTER ROLE nina_worker       SET idle_in_transaction_session_timeout = '60s';
    ALTER ROLE nina_config_admin SET statement_timeout = '30s';
    ALTER ROLE nina_config_admin SET lock_timeout = '5s';
    ALTER ROLE nina_config_admin SET idle_in_transaction_session_timeout = '30s';
  EXCEPTION WHEN insufficient_privilege THEN
    RAISE WARNING 'sem privilegio para definir timeouts por papel (NR-04): aplique statement_timeout/lock_timeout/idle_in_transaction_session_timeout na infraestrutura';
  END;
END $$;

GRANT USAGE ON SCHEMA nina TO nina_app, nina_worker, nina_config_admin;

-- Funcoes futuras do dono nascem sem EXECUTE para PUBLIC (SR-012). O EXECUTE de PUBLIC e um padrao GLOBAL do PostgreSQL: so a forma
-- sem IN SCHEMA o remove (a forma por schema apenas soma privilegios). Vale para os objetos criados por este papel neste banco.
ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES REVOKE ALL ON SEQUENCES FROM PUBLIC;

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

-- Fichas por transacao (SR-005, SR-008). Um GUC comum (SET LOCAL x = 'on') pode ser definido por qualquer papel, logo
-- nao serve de autorizacao. A ficha e sha256(segredo-do-banco : finalidade : id-da-transacao): so codigo SECURITY
-- DEFINER do dono le o segredo, entao so ele consegue emiti-la; ela vale so na transacao que a emitiu.
CREATE TABLE nina.guard_secret (
  id     smallint PRIMARY KEY CHECK (id = 1),
  secret text NOT NULL
);
INSERT INTO nina.guard_secret VALUES (1, gen_random_uuid()::text || gen_random_uuid()::text);

CREATE FUNCTION nina.guard_token(p_purpose text) RETURNS text
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT encode(sha256(convert_to(s.secret || ':' || p_purpose || ':' || pg_current_xact_id()::text, 'UTF8')), 'hex')
     FROM nina.guard_secret s $$;

CREATE FUNCTION nina.guard_arm(p_purpose text) RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT set_config('nina.guard.' || p_purpose, nina.guard_token(p_purpose), true) $$;

CREATE FUNCTION nina.guard_disarm(p_purpose text) RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT set_config('nina.guard.' || p_purpose, '', true) $$;

CREATE FUNCTION nina.guard_ok(p_purpose text) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_presented text := coalesce(current_setting('nina.guard.' || p_purpose, true), '');
BEGIN
  IF v_presented = '' THEN RETURN false; END IF;     -- caminho comum (nenhuma ficha): sem custo de hash nem de id de transacao
  RETURN v_presented = nina.guard_token(p_purpose);
END $$;

-- A propria migracao e privilegiada (seeds sem ator, ver log_config_change); quem roda e o dono.
SELECT nina.guard_arm('migration');

-- Deny-list PROFUNDA de PII (SR-006): chaves em qualquer nivel (normalizadas: "e_mail", "E-Mail" e "email" sao iguais)
-- e valores textuais com formato de e-mail. Usada em audit_event.metadata_safe, outbox_message.payload e sync.
CREATE FUNCTION nina.pii_key(p_key text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
$$ SELECT regexp_replace(lower(p_key), '[^a-z0-9]', '', 'g')
            ~ '(email|password|passwd|senha|secret|token|birthdate|birthday|displayname|nickname|photo|phone|telefone|cpf|address|endereco|nascimento|fullname|firstname|lastname)'
       OR regexp_replace(lower(p_key), '[^a-z0-9]', '', 'g')
            IN ('name', 'nome', 'notes', 'note', 'mail', 'ip', 'dob', 'body', 'content', 'text', 'idtoken') $$;

CREATE FUNCTION nina.jsonb_pii_ok(p jsonb, p_depth integer DEFAULT 0) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE k text; v jsonb;
BEGIN
  IF p IS NULL THEN RETURN true; END IF;
  IF p_depth > 6 THEN RETURN false; END IF;
  CASE jsonb_typeof(p)
    WHEN 'object' THEN
      FOR k, v IN SELECT * FROM jsonb_each(p) LOOP
        IF nina.pii_key(k) OR NOT nina.jsonb_pii_ok(v, p_depth + 1) THEN RETURN false; END IF;
      END LOOP;
    WHEN 'array' THEN
      FOR v IN SELECT * FROM jsonb_array_elements(p) LOOP
        IF NOT nina.jsonb_pii_ok(v, p_depth + 1) THEN RETURN false; END IF;
      END LOOP;
    WHEN 'string' THEN
      IF (p #>> '{}') ~ '[^@[:space:]"]+@[^@[:space:]"]+[.][^@[:space:]"]+' THEN RETURN false; END IF;
    ELSE NULL;
  END CASE;
  RETURN true;
END $$;

-- Copia os timeouts do papel de grupo (nina_app/nina_worker/nina_config_admin) para um LOGIN membro dele (NR-04). Sem GRANT: so o
-- dono/migrator chama (bootstrap de infraestrutura).
CREATE FUNCTION nina.apply_role_limits(p_login name, p_group name) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE s text;
BEGIN
  IF p_group NOT IN ('nina_app', 'nina_worker', 'nina_config_admin') THEN
    RAISE EXCEPTION 'papel de grupo invalido' USING ERRCODE = 'NN070';
  END IF;
  FOR s IN SELECT unnest(rs.setconfig) FROM pg_db_role_setting rs JOIN pg_roles r ON r.oid = rs.setrole
            WHERE r.rolname = p_group AND rs.setdatabase = 0 LOOP
    EXECUTE format('ALTER ROLE %I SET %s = %L', p_login, split_part(s, '=', 1), substr(s, position('=' IN s) + 1));
  END LOOP;
END $$;

-- HMAC-SHA256 (RFC 2104) em SQL puro: o banco nao usa extensoes (pgcrypto).
CREATE FUNCTION nina.hmac_sha256(p_key bytea, p_msg bytea) RETURNS bytea
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE k bytea := p_key; ipad bytea; opad bytea; i integer;
BEGIN
  IF octet_length(k) > 64 THEN k := sha256(k); END IF;
  k := k || decode(repeat('00', 64 - octet_length(k)), 'hex');
  ipad := k; opad := k;
  FOR i IN 0..63 LOOP
    ipad := set_byte(ipad, i, get_byte(k, i) # 54);    -- 0x36
    opad := set_byte(opad, i, get_byte(k, i) # 92);    -- 0x5c
  END LOOP;
  RETURN sha256(opad || sha256(ipad || p_msg));
END $$;

-- Chave de ASSINATURA servidor -> banco (NR-03/NR-09). Fatos que so a API pode atestar (a senha foi conferida e o reauth emitido; o
-- codigo foi gerado e enviado ao e-mail; o provedor social atestou o e-mail) entram no banco acompanhados de um HMAC desta chave.
-- Quem executa SQL arbitrario como nina_app NAO tem a chave (ela vive so na configuracao da API e nesta tabela, que nenhum papel de
-- aplicacao le), logo nao consegue forjar jti de reautenticacao, codigo de e-mail nem verificacao social.
-- Provisionada pelo dono/migrator com nina.provision_server_key(); ate la as funcoes assinadas FALHAM (fechado).
CREATE TABLE nina.server_key (
  id             smallint PRIMARY KEY CHECK (id = 1),
  key            bytea NOT NULL CHECK (octet_length(key) >= 32),
  provisioned_at timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION nina.provision_server_key(p_key bytea) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF p_key IS NULL OR octet_length(p_key) < 32 THEN
    RAISE EXCEPTION 'a chave servidor-banco deve ter ao menos 32 bytes' USING ERRCODE = 'NN070';
  END IF;
  INSERT INTO nina.server_key (id, key) VALUES (1, p_key)
  ON CONFLICT (id) DO UPDATE SET key = EXCLUDED.key, provisioned_at = now();
END $$;

-- Confere o MAC de uma mensagem canonica `purpose|message`. Interno (sem GRANT): so as funcoes definer o usam.
CREATE FUNCTION nina.mac_ok(p_purpose text, p_message text, p_mac bytea) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_key bytea;
BEGIN
  SELECT k.key INTO v_key FROM nina.server_key k WHERE k.id = 1;
  IF v_key IS NULL THEN
    RAISE EXCEPTION 'SERVER_KEY_NOT_PROVISIONED: chave de assinatura servidor-banco ausente (nina.provision_server_key)' USING ERRCODE = 'NN070';
  END IF;
  IF p_mac IS NULL OR octet_length(p_mac) <> 32 THEN RETURN false; END IF;
  RETURN nina.hmac_sha256(v_key, convert_to(p_purpose || '|' || p_message, 'UTF8')) = p_mac;
END $$;

-- Locks de CONTROLE (NR-04): a serializacao da cadeia de auditoria e das purgas usa o bloqueio de LINHA desta tabela privada, nao
-- advisory locks com chave deterministica (qualquer papel poderia segurar `pg_advisory_lock(hashtextextended('nina.audit_chain', 0))`).
-- Nenhum papel de aplicacao tem privilegio na tabela; so as funcoes definer travam as linhas.
CREATE TABLE nina.control_lock (name text PRIMARY KEY);
INSERT INTO nina.control_lock VALUES ('audit_chain'), ('purge_sync'), ('purge_operational'), ('purge_audit'), ('purge_consent');

-- A sessao pertence a um login de aplicacao (membro de nina_app, nao superusuario)? Os gatilhos BEFORE INSERT que rodam como dono usam
-- isto para negar PRIMEIRO, com o mesmo erro da RLS, o que a politica negaria depois (NR-05).
CREATE FUNCTION nina.is_app_session() RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT pg_has_role(session_user, 'nina_app', 'MEMBER')
      AND NOT EXISTS (SELECT 1 FROM pg_roles r WHERE r.rolname = session_user AND (r.rolsuper OR r.rolbypassrls)) $$;

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
  display_name      text CHECK (length(btrim(display_name)) BETWEEN 1 AND 80),   -- BE-001: nome exibido (PII; zerado na anonimizacao)
  locale            text CHECK (locale ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$'),
  timezone          nina.iana_tz,
  status            text NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE', 'PENDING_DELETION', 'DELETED')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  deleted_at        timestamptz,
  CONSTRAINT app_user_email_ck CHECK (
    (status = 'DELETED' AND email IS NULL AND display_name IS NULL AND deleted_at IS NOT NULL)
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

-- refresh_token/recovery_request: uso unico. Depois de usado, `used_at` nunca volta a NULL nem muda (o app so tem UPDATE(used_at)).
CREATE FUNCTION nina.single_use_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.used_at IS NOT NULL AND NEW.used_at IS DISTINCT FROM OLD.used_at THEN
    RAISE EXCEPTION 'segredo de uso unico ja consumido (used_at e imutavel)' USING ERRCODE = 'NN053';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER refresh_token_single_use BEFORE UPDATE ON nina.refresh_token FOR EACH ROW EXECUTE FUNCTION nina.single_use_guard();
CREATE TRIGGER recovery_request_single_use BEFORE UPDATE ON nina.recovery_request FOR EACH ROW EXECUTE FUNCTION nina.single_use_guard();

-- Codigo de verificacao do e-mail ATUAL (BE-001): um vigente por usuario, contador de tentativas no proprio registro.
CREATE TABLE nina.email_verification_code (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id        uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  code_hash      bytea NOT NULL CHECK (octet_length(code_hash) = 32),      -- HMAC no app; nunca o codigo
  created_at     timestamptz NOT NULL DEFAULT now(),
  expires_at     timestamptz NOT NULL,
  attempts       smallint NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  max_attempts   smallint NOT NULL DEFAULT 5 CHECK (max_attempts BETWEEN 1 AND 10),
  used_at        timestamptz,
  invalidated_at timestamptz,
  CONSTRAINT email_code_expiry_ck CHECK (expires_at > created_at)
);
CREATE UNIQUE INDEX email_verification_open_uq ON nina.email_verification_code (user_id) WHERE used_at IS NULL AND invalidated_at IS NULL;

-- Pendencia de TROCA de e-mail (RF-050): o novo endereco so vira o e-mail da conta apos confirmar o codigo enviado a ele.
CREATE TABLE nina.email_change_request (
  id                   uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id              uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  new_email            text NOT NULL CHECK (position('@' IN new_email) > 1 AND length(new_email) <= 254),
  new_email_normalized text GENERATED ALWAYS AS (lower(btrim(new_email))) STORED,
  code_hash            bytea NOT NULL CHECK (octet_length(code_hash) = 32),
  created_at           timestamptz NOT NULL DEFAULT now(),
  expires_at           timestamptz NOT NULL,
  attempts             smallint NOT NULL DEFAULT 0 CHECK (attempts >= 0),
  max_attempts         smallint NOT NULL DEFAULT 5 CHECK (max_attempts BETWEEN 1 AND 10),
  confirmed_at         timestamptz,
  invalidated_at       timestamptz,
  CONSTRAINT email_change_expiry_ck CHECK (expires_at > created_at)
);
CREATE UNIQUE INDEX email_change_open_uq ON nina.email_change_request (user_id) WHERE confirmed_at IS NULL AND invalidated_at IS NULL;

-- Livro-razao de reautenticacao (SR-013/SR-016/NR-03). Duas fases, ambas no banco:
--   EMISSAO  (nina.reauth_issue): so a API emite, e a linha so nasce com um MAC valido (nina.server_key) sobre
--            usuario|sessao|jti|escopos|emissao|expiracao. Um jti inventado por quem executa SQL arbitrario nao existe aqui.
--   CONSUMO  (nina.consume_reauth_jti): so um jti EMITIDO para este usuario e esta sessao, ainda nao consumido, dentro do escopo.
--            Depois, reauth_bind vincula o jti consumido a um recurso (uso unico real).
-- `scopes` vazio = token de transicao v1.x (vale uma operacao sensivel qualquer, uma vez). `attempts` conta apresentacoes.
CREATE TABLE nina.reauth_jti (
  jti_hash          bytea PRIMARY KEY CHECK (octet_length(jti_hash) = 32),
  user_id           uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  session_id        uuid NOT NULL,
  scopes            text[] NOT NULL DEFAULT '{}' CHECK (cardinality(scopes) <= 3
                      AND scopes <@ ARRAY['ACCOUNT_PASSWORD_CHANGE', 'ACCOUNT_EMAIL_CHANGE', 'IDENTITY_LINK', 'IDENTITY_UNLINK',
                                          'DATA_EXPORT_REQUEST', 'DATA_EXPORT_DOWNLOAD', 'ACCOUNT_DELETE', 'PRIVACY_REQUEST',
                                          'BABY_DELETE', 'OWNERSHIP_TRANSFER']),
  issued_at         timestamptz NOT NULL,                          -- relogio da API (coberto pelo MAC)
  expires_at        timestamptz NOT NULL,
  consumed_at       timestamptz,
  consumed_scope    text CHECK (consumed_scope IN ('ACCOUNT_PASSWORD_CHANGE', 'ACCOUNT_EMAIL_CHANGE', 'IDENTITY_LINK', 'IDENTITY_UNLINK',
                                                   'DATA_EXPORT_REQUEST', 'DATA_EXPORT_DOWNLOAD', 'ACCOUNT_DELETE', 'PRIVACY_REQUEST',
                                                   'BABY_DELETE', 'OWNERSHIP_TRANSFER')),
  attempts          smallint NOT NULL DEFAULT 0,
  bound_entity_type text CHECK (bound_entity_type IN ('ACCOUNT_DELETION_REQUEST', 'PRIVACY_REQUEST', 'BABY', 'EMAIL_CHANGE_REQUEST')),
  bound_entity_id   uuid,
  CONSTRAINT reauth_lifetime_ck CHECK (expires_at > issued_at AND expires_at <= issued_at + interval '5 minutes'),
  CONSTRAINT reauth_consumed_ck CHECK ((consumed_at IS NULL) = (consumed_scope IS NULL)),
  CONSTRAINT reauth_bound_ck CHECK ((bound_entity_type IS NULL) = (bound_entity_id IS NULL) AND (bound_entity_id IS NULL OR consumed_at IS NOT NULL))
);
CREATE INDEX reauth_jti_user_ix ON nina.reauth_jti (user_id, issued_at DESC);
CREATE INDEX reauth_jti_expiry_ix ON nina.reauth_jti (expires_at);

CREATE TABLE nina.device_push_token (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id      uuid NOT NULL REFERENCES nina.app_user(id) ON DELETE CASCADE,
  device_id    uuid NOT NULL,
  platform     text NOT NULL CHECK (platform IN ('APNS', 'FCM')),
  token        text NOT NULL CHECK (length(token) BETWEEN 1 AND 4096),   -- C4: avaliar cifra em nivel de campo (SEC-021)
  environment  text CHECK (environment IN ('PRODUCTION', 'SANDBOX')),
  locale       text CHECK (locale ~ '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$'),
  app_version  text CHECK (length(app_version) <= 32),
  os_notifications_authorized boolean,                 -- permissao do SO; NULL = nao informado
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

-- SR-001: a titularidade da familia nao muda por UPDATE do app (nem de ninguem fora de transfer_ownership/erase_user).
CREATE FUNCTION nina.family_owner_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF (NEW.id <> OLD.id OR NEW.owner_user_id <> OLD.owner_user_id) AND NOT nina.guard_ok('family_owner') THEN
    RAISE EXCEPTION 'a titularidade da familia so muda por funcao de transferencia' USING ERRCODE = 'NN050';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER family_owner_guard BEFORE UPDATE ON nina.family FOR EACH ROW EXECUTE FUNCTION nina.family_owner_guard();

CREATE TABLE nina.baby (
  id               uuid PRIMARY KEY,                    -- gerado no cliente
  family_id        uuid NOT NULL REFERENCES nina.family(id),
  display_name     text CHECK (length(display_name) BETWEEN 1 AND 100),
  birth_date       date,
  due_date         date,
  sex              text CHECK (sex IN ('FEMALE', 'MALE', 'OTHER', 'UNSPECIFIED')),   -- SR-015: coleta de `sex` pendente de decisao juridica (DJ-01); OTHER alinha ao contrato
  timezone         nina.iana_tz NOT NULL,
  photo_ref        text CHECK (length(photo_ref) <= 512),   -- D-09
  version          bigint NOT NULL DEFAULT 0,               -- = sync_sequence da ultima mudanca
  field_versions   jsonb NOT NULL DEFAULT '{}'::jsonb       -- R-01: {campo: [version, device_id]} so de campos editados apos a criacao
                   CHECK (jsonb_typeof(field_versions) = 'object' AND octet_length(field_versions::text) <= 2048),
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

-- SR-001: family_id e imutavel pelo app; so a transferencia de propriedade (funcao definer) o move.
CREATE FUNCTION nina.baby_family_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF NEW.family_id <> OLD.family_id AND NOT nina.guard_ok('baby_family') THEN
    RAISE EXCEPTION 'baby.family_id e imutavel fora da transferencia de propriedade' USING ERRCODE = 'NN050';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER baby_a_family_guard BEFORE UPDATE ON nina.baby FOR EACH ROW EXECUTE FUNCTION nina.baby_family_guard();

CREATE TABLE nina.caregiver_membership (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  baby_id           uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  user_id           uuid REFERENCES nina.app_user(id),
  invited_email     text CHECK (length(invited_email) <= 254 AND invited_email = lower(btrim(invited_email)) AND position('@' IN invited_email) > 1),
  role              text NOT NULL CHECK (role IN ('OWNER', 'CAREGIVER', 'READ_ONLY')),
  status            text NOT NULL CHECK (status IN ('PENDING', 'ACTIVE', 'REVOKED', 'DECLINED')),
  invited_by        uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  invite_token_hash bytea UNIQUE CHECK (octet_length(invite_token_hash) = 32),
  invite_expires_at timestamptz,
  invite_used_at    timestamptz,                          -- SR-009: convite de uso unico
  invite_failed_attempts smallint NOT NULL DEFAULT 0 CHECK (invite_failed_attempts >= 0),
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
CREATE INDEX membership_inviter_ix ON nina.caregiver_membership (invited_by, invited_at) WHERE invited_by IS NOT NULL;
CREATE TRIGGER membership_touch BEFORE UPDATE ON nina.caregiver_membership FOR EACH ROW EXECUTE FUNCTION nina.touch_updated_at();

-- SR-002: maquina de estados do vinculo, independente de papel e de politica. Fora das funcoes definer (que emitem a ficha
-- 'membership'/'ownership') NENHUMA coluna muda; dentro delas so as transicoes permitidas:
--   PENDING -> ACTIVE | DECLINED | REVOKED ;  ACTIVE -> REVOKED ;  REVOKED/DECLINED sao terminais (nunca reativam).
--   baby_id/id/invited_at/created_at sao imutaveis; user_id so vai de NULL para um usuario (aceite);
--   role so muda em vinculo ACTIVE, e a promocao a OWNER exige a ficha 'ownership'.
CREATE FUNCTION nina.membership_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF NEW.id <> OLD.id OR NEW.baby_id <> OLD.baby_id OR NEW.invited_at <> OLD.invited_at OR NEW.created_at <> OLD.created_at THEN
    RAISE EXCEPTION 'vinculo: id/baby_id/datas de criacao sao imutaveis' USING ERRCODE = 'NN051';
  END IF;
  IF OLD.user_id IS NOT NULL AND NEW.user_id IS DISTINCT FROM OLD.user_id THEN
    RAISE EXCEPTION 'vinculo: user_id e imutavel depois de definido' USING ERRCODE = 'NN051';
  END IF;
  IF NEW.invited_by IS DISTINCT FROM OLD.invited_by AND NEW.invited_by IS NOT NULL THEN
    RAISE EXCEPTION 'vinculo: invited_by so pode ser anulado (anonimizacao)' USING ERRCODE = 'NN051';
  END IF;
  IF NOT nina.guard_ok('membership') THEN
    IF (NEW.user_id, NEW.invited_email, NEW.role, NEW.status, NEW.invited_by, NEW.invite_token_hash, NEW.invite_expires_at,
        NEW.invite_used_at, NEW.invite_failed_attempts, NEW.accepted_at, NEW.revoked_at, NEW.revoked_reason)
       IS DISTINCT FROM
       (OLD.user_id, OLD.invited_email, OLD.role, OLD.status, OLD.invited_by, OLD.invite_token_hash, OLD.invite_expires_at,
        OLD.invite_used_at, OLD.invite_failed_attempts, OLD.accepted_at, OLD.revoked_at, OLD.revoked_reason) THEN
      RAISE EXCEPTION 'vinculo so muda por funcoes de convite/propriedade (SR-002)' USING ERRCODE = 'NN051';
    END IF;
    RETURN NEW;
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'PENDING' AND NEW.status IN ('ACTIVE', 'DECLINED', 'REVOKED'))
    OR (OLD.status = 'ACTIVE' AND NEW.status = 'REVOKED')) THEN
    RAISE EXCEPTION 'vinculo: transicao % -> % proibida', OLD.status, NEW.status USING ERRCODE = 'NN051';
  END IF;
  IF OLD.status IN ('REVOKED', 'DECLINED') AND (NEW.role IS DISTINCT FROM OLD.role OR NEW.user_id IS DISTINCT FROM OLD.user_id) THEN
    RAISE EXCEPTION 'vinculo encerrado nao pode ser alterado' USING ERRCODE = 'NN051';
  END IF;
  IF NEW.role IS DISTINCT FROM OLD.role THEN
    IF OLD.status <> 'ACTIVE' OR NEW.status <> 'ACTIVE' THEN
      RAISE EXCEPTION 'vinculo: papel so muda em vinculo ativo' USING ERRCODE = 'NN051';
    END IF;
    IF NEW.role = 'OWNER' AND NOT nina.guard_ok('ownership') THEN
      RAISE EXCEPTION 'promocao a OWNER so por transferencia de propriedade' USING ERRCODE = 'NN050';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER membership_a_guard BEFORE UPDATE ON nina.caregiver_membership FOR EACH ROW EXECUTE FUNCTION nina.membership_guard();

-- SR-009 / SEC-004: limite diario de convites por Owner (app_parameter 'invites.max_per_day'). Conta apenas convites novos
-- (PENDING) criados pelo convidante nas ultimas 24 h.
CREATE FUNCTION nina.membership_invite_limit() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  -- NR-05: convite em nome de OUTRO usuario e negado como a RLS nega (nunca revela o contador diario de terceiros)
  IF NEW.status = 'PENDING' AND NEW.invited_by IS NOT NULL AND nina.is_app_session()
     AND NEW.invited_by IS DISTINCT FROM nina.current_user_id() THEN
    RAISE EXCEPTION 'new row violates row-level security policy for table "caregiver_membership"' USING ERRCODE = '42501';
  END IF;
  IF NEW.status = 'PENDING' AND NEW.invited_by IS NOT NULL
     AND (SELECT count(*) FROM nina.caregiver_membership m
           WHERE m.invited_by = NEW.invited_by AND m.invited_at > now() - interval '24 hours')
         >= nina.param_int('invites.max_per_day', 20) THEN
    RAISE EXCEPTION 'INVITE_LIMIT_REACHED: limite diario de convites' USING ERRCODE = 'NN057';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER membership_b_invite_limit BEFORE INSERT ON nina.caregiver_membership FOR EACH ROW EXECUTE FUNCTION nina.membership_invite_limit();

-- INV-09 (pelo menos um) / SEC-007: bebe vivo sempre tem Owner ativo ao fim da transacao.
-- SECURITY DEFINER (SR-004): a checagem precisa enxergar o Owner mesmo quando o chamador (RLS) nao o enxerga.
CREATE FUNCTION nina.check_baby_has_owner() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_baby uuid := coalesce(NEW.baby_id, OLD.baby_id);
BEGIN
  IF EXISTS (SELECT 1 FROM nina.baby b WHERE b.id = v_baby AND b.deleted_at IS NULL)
     AND NOT EXISTS (SELECT 1 FROM nina.caregiver_membership m
                      WHERE m.baby_id = v_baby AND m.role = 'OWNER' AND m.status = 'ACTIVE') THEN
    RAISE EXCEPTION 'bebe sem Owner ativo (INV-09/SEC-007)' USING ERRCODE = 'NN010', DETAIL = v_baby::text;
  END IF;
  RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER membership_owner_present
  AFTER INSERT OR UPDATE OR DELETE ON nina.caregiver_membership
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION nina.check_baby_has_owner();

-- Helpers de autorizacao (SECURITY DEFINER para nao recursar nas politicas de RLS)
-- NR-07: bebe excluido (casca/tombstone) nao e legivel nem gravavel por ninguem pelo app: vinculos e eventos ficam ilegiveis
-- (baby_role, readable_babies e writable_babies ignoram bebe com deleted_at).
CREATE FUNCTION nina.baby_role(p_baby uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT m.role FROM nina.caregiver_membership m JOIN nina.baby b ON b.id = m.baby_id AND b.deleted_at IS NULL
    WHERE m.baby_id = p_baby AND m.user_id = nina.current_user_id() AND m.status = 'ACTIVE' $$;

CREATE FUNCTION nina.can_read_baby(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT nina.baby_role(p_baby) IS NOT NULL $$;

CREATE FUNCTION nina.can_write_baby(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT coalesce(nina.baby_role(p_baby) IN ('OWNER', 'CAREGIVER'), false) $$;

-- R-03: conjuntos de bebes do usuario corrente, avaliados UMA vez por comando (SubPlan hashed) nas politicas
-- `baby_id IN (SELECT nina.readable_babies())`, em vez de uma chamada de funcao por LINHA. Semantica identica a
-- can_read_baby/can_write_baby (snapshot de 20 mil eventos: 29,4 s -> 0,41 s no spike).
CREATE FUNCTION nina.readable_babies() RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT m.baby_id FROM nina.caregiver_membership m JOIN nina.baby b ON b.id = m.baby_id AND b.deleted_at IS NULL
    WHERE m.user_id = nina.current_user_id() AND m.status = 'ACTIVE' $$;

CREATE FUNCTION nina.writable_babies() RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT m.baby_id FROM nina.caregiver_membership m JOIN nina.baby b ON b.id = m.baby_id AND b.deleted_at IS NULL
    WHERE m.user_id = nina.current_user_id() AND m.status = 'ACTIVE' AND m.role IN ('OWNER', 'CAREGIVER') $$;

-- NR-05: gatilhos BEFORE INSERT rodam ANTES do WITH CHECK da RLS. Os que sao SECURITY DEFINER chamam isto primeiro: para uma sessao
-- do app que NAO pode escrever no bebe, o erro e o MESMO da politica (42501), nunca um erro de negocio (SLEEP_OVERLAP, bebe excluido,
-- despertar sem sessao...) que revelaria dados de outro tenant, e antes de tocar no contador do bebe (sem oraculo de tempo/lock).
CREATE FUNCTION nina.rls_precheck_baby(p_baby uuid, p_table text) RETURNS void
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF nina.is_app_session() AND NOT EXISTS (SELECT 1 FROM nina.writable_babies() w WHERE w = p_baby) THEN
    RAISE EXCEPTION 'new row violates row-level security policy for table "%"', p_table USING ERRCODE = '42501';
  END IF;
END $$;

-- NR-07: o vinculo do usuario com um bebe EXCLUIDO (inclusive o encerrado) deixa de ser legivel pelo app; a trilha fica na auditoria.
-- So responde "true" a quem tem vinculo com o bebe (para qualquer outro UUID, false: sem oraculo de bebe excluido) e false no instante
-- do INSERT de um vinculo novo (bebe vivo), o que preserva INSERT ... RETURNING.
CREATE FUNCTION nina.own_link_on_deleted_baby(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.baby b JOIN nina.caregiver_membership m ON m.baby_id = b.id AND m.user_id = nina.current_user_id()
                   WHERE b.id = p_baby AND b.deleted_at IS NOT NULL) $$;

CREATE FUNCTION nina.is_family_owner(p_family uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.family f WHERE f.id = p_family AND f.owner_user_id = nina.current_user_id()) $$;

-- NR-13: so responde "sim" a quem tem (ou teve) vinculo com o bebe ou e titular da familia dele; para um UUID alheio ou
-- inexistente a resposta e sempre false (sem oraculo de existencia). Uso: politica de SELECT de baby no instante de criacao (a
-- propria familia, ainda sem Owner).
CREATE FUNCTION nina.baby_ever_had_owner(p_baby uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.caregiver_membership m WHERE m.baby_id = p_baby AND m.role = 'OWNER')
      AND (EXISTS (SELECT 1 FROM nina.caregiver_membership me WHERE me.baby_id = p_baby AND me.user_id = nina.current_user_id())
           OR EXISTS (SELECT 1 FROM nina.baby b JOIN nina.family f ON f.id = b.family_id
                       WHERE b.id = p_baby AND f.owner_user_id = nina.current_user_id())) $$;

-- Criador do bebe vira Owner (RB-006): bebe da PROPRIA familia que ainda nunca teve vinculo OWNER (nem revogado).
CREATE FUNCTION nina.can_bootstrap_owner(p_baby uuid) RETURNS boolean
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

-- R-02: o pull precisa de last_sequence (ponto do snapshot, cursor "do futuro") e purged_through (cursor expirado), mas
-- nina_app nao le baby_sync_head; esta funcao devolve so a linha de um bebe que o usuario pode ler.
CREATE FUNCTION nina.sync_head(p_baby uuid) RETURNS TABLE (last_sequence bigint, purged_through bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT h.last_sequence, h.purged_through FROM nina.baby_sync_head h
    WHERE h.baby_id = p_baby AND nina.can_read_baby(p_baby) $$;

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

-- R-04 / SR-014: mutation_id escopado por (usuario, dispositivo): um tenant nao "queima" o mutation_id de outro nem descobre
-- sua existencia (contrato 1.0.1). Idempotencia continua por (user, device, mutation_id).
CREATE TABLE nina.sync_mutation (   -- idempotencia do push (INV-18); sem payload
  mutation_id       uuid NOT NULL,
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
  reject_code       text CHECK (length(reject_code) <= 64),
  -- R-09: a resolucao devolvida na primeira resposta; o reenvio (DUPLICATE) a reproduz sem recalcular. Nulo em REJECTED.
  resolution        text CHECK (resolution IN ('NONE', 'MERGED', 'LWW_CLIENT_WON', 'LWW_SERVER_WON', 'DELETE_WINS', 'KEPT_BOTH')),
  CONSTRAINT sync_mutation_resolution_ck CHECK (resolution IS NULL OR outcome <> 'REJECTED'),
  PRIMARY KEY (user_id, device_id, mutation_id)
);
CREATE INDEX sync_mutation_baby_ix ON nina.sync_mutation (baby_id, received_at);
CREATE INDEX sync_mutation_received_ix ON nina.sync_mutation (received_at);

-- Trigger BEFORE: atribui version (servidor), protege tombstone, limpa notas na exclusao
CREATE FUNCTION nina.sync_stamp_child() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF TG_OP = 'INSERT' THEN
    PERFORM nina.rls_precheck_baby(NEW.baby_id, TG_TABLE_NAME);        -- NR-05: antes de qualquer erro de negocio ou lock do bebe
    IF EXISTS (SELECT 1 FROM nina.baby b WHERE b.id = NEW.baby_id AND b.deleted_at IS NOT NULL) THEN
      RAISE EXCEPTION 'bebe % excluido', NEW.baby_id USING ERRCODE = 'NN003';
    END IF;
    NEW.created_at := now();
    -- NR-06: a autoria vem do CONTEXTO (nina.user_id), nunca do cliente. Sem contexto (dono/sistema) vale o valor informado.
    IF nina.current_user_id() IS NOT NULL THEN
      NEW.created_by := nina.current_user_id();
      NEW.last_modified_by := nina.current_user_id();
    END IF;
  ELSE
    IF nina.guard_ok('authorship_scrub') THEN
      -- Eliminacao de autoria (ADR-0010, scrub_user_personal_data): so os ponteiros created_by/last_modified_by do
      -- usuario-alvo sao anulados; nada mais muda, sem nova versao e sem change_log (autoria nao faz parte do contrato).
      -- SR-008: a autorizacao e a ficha por transacao emitida pela funcao de scrub, nao um GUC definivel pelo chamador.
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
    NEW.created_by := OLD.created_by;                                  -- NR-06: autoria de criacao e imutavel
    IF nina.current_user_id() IS NOT NULL THEN NEW.last_modified_by := nina.current_user_id(); END IF;
  END IF;
  IF NEW.deleted_at IS NOT NULL THEN      -- tombstone sem conteudo livre
    NEW := jsonb_populate_record(NEW, jsonb_build_object('notes', NULL, 'field_versions', '{}'::jsonb));
  END IF;
  NEW.updated_at := now();
  NEW.version := nina.next_sync_sequence(NEW.baby_id);
  RETURN NEW;
END $$;

CREATE FUNCTION nina.sync_stamp_baby() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF TG_OP = 'UPDATE' THEN
    IF nina.guard_ok('authorship_scrub') THEN     -- ver sync_stamp_child
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
    NEW.created_by := OLD.created_by;                                  -- NR-06
    IF nina.current_user_id() IS NOT NULL THEN NEW.last_modified_by := nina.current_user_id(); END IF;
  ELSE
    NEW.created_at := now();
    IF nina.current_user_id() IS NOT NULL THEN                         -- NR-06
      NEW.created_by := nina.current_user_id();
      NEW.last_modified_by := nina.current_user_id();
    END IF;
  END IF;
  -- NR-07: a exclusao de bebe so existe por nina.delete_baby/erase_baby (ficha 'baby_delete'): reauth, auditoria e aviso aos cuidadores.
  IF NEW.deleted_at IS NOT NULL AND (TG_OP = 'INSERT' OR OLD.deleted_at IS NULL) AND NOT nina.guard_ok('baby_delete') THEN
    RAISE EXCEPTION 'bebe so e excluido por nina.delete_baby (reautenticacao, auditoria e aviso aos cuidadores)' USING ERRCODE = 'NN052';
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
-- R-04: PK escopada por bebe (baby_id, id). O id da entidade nao e unico globalmente: nao denuncia existencia em outro
-- tenant (SR-014). Toda consulta/atualizacao por entidade DEVE incluir baby_id.
CREATE TABLE nina.sleep_session (
  id               uuid NOT NULL,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  sleep_type       text NOT NULL CHECK (sleep_type IN ('NAP', 'NIGHT')),
  method_or_place  text CHECK (length(method_or_place) <= 80),   -- D-08 (texto livre curto); 80 = contrato 1.0.1 (R-10)
  notes            text CHECK (length(notes) <= 500),            -- 500 = contrato 1.0.1 (R-10/SR-021)
  source           text NOT NULL CHECK (source IN ('TIMER', 'MANUAL')),
  version          bigint NOT NULL DEFAULT 0,
  field_versions   jsonb NOT NULL DEFAULT '{}'::jsonb       -- R-01: {campo: [version, device_id]}; so campos editados apos a criacao
                   CHECK (jsonb_typeof(field_versions) = 'object' AND octet_length(field_versions::text) <= 2048),

  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT sleep_interval_ck CHECK (end_at IS NULL OR end_at >= start_at),           -- INV-01
  PRIMARY KEY (baby_id, id)                                                              -- alvo da FK composta de wake_event
);
-- R-05 (DECISAO): sem indice UNICO de "uma sessao aberta por bebe". O contrato 1.0.1 e o api-spec 3.1 mandam ACEITAR dois
-- timers abertos (dois aparelhos offline) como KEPT_BOTH + aviso OPEN_SLEEP_EXISTS; um indice unico os rejeitaria e obrigaria
-- a API a fechar uma das sessoes por heuristica (perda de dado). INV-02 passa a ser "uma aberta e o esperado; varias sao
-- toleradas e sinalizadas": sleep.overlap_policy=REJECT continua recusando a segunda (sessao aberta = intervalo ate o infinito).
CREATE INDEX sleep_open_ix ON nina.sleep_session (baby_id) WHERE end_at IS NULL AND deleted_at IS NULL;
CREATE INDEX sleep_timeline_ix ON nina.sleep_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.feeding_session (
  id               uuid NOT NULL,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  feeding_type     text NOT NULL CHECK (feeding_type IN ('BREASTFEEDING', 'BOTTLE', 'SOLID', 'OTHER')),   -- ADR-0009; clientes toleram valores novos
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  side             text CHECK (side IN ('LEFT', 'RIGHT', 'BOTH')),
  volume_ml        numeric(6,1) CHECK (volume_ml > 0 AND volume_ml <= 5000),
  -- ADR-0009: so se aplica a mamadeira (feeding_shape_ck); UNKNOWN reservado a migracao de dados antigos
  milk_type        text CHECK (milk_type IN ('BREAST_MILK', 'FORMULA', 'MIXED', 'OTHER', 'UNSPECIFIED', 'UNKNOWN')),
  notes            text CHECK (length(notes) <= 500),
  version          bigint NOT NULL DEFAULT 0,
  field_versions   jsonb NOT NULL DEFAULT '{}'::jsonb       -- R-01: {campo: [version, device_id]}; so campos editados apos a criacao
                   CHECK (jsonb_typeof(field_versions) = 'object' AND octet_length(field_versions::text) <= 2048),

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
    OR (feeding_type = 'OTHER' AND side IS NULL AND milk_type IS NULL)),
  PRIMARY KEY (baby_id, id)
);
COMMENT ON COLUMN nina.feeding_session.end_at IS 'ADR-0010 item 5: OBRIGATORIO quando feeding_type = BREASTFEEDING (feeding_shape_ck); opcional nos demais tipos.';
COMMENT ON COLUMN nina.feeding_session.milk_type IS 'ADR-0009: NULL quando feeding_type <> BOTTLE (nao se aplica). Em BOTTLE, NULL = indisponivel; UNSPECIFIED = usuario nao especificou.';
CREATE INDEX feeding_timeline_ix ON nina.feeding_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.pumping_session (
  id               uuid NOT NULL,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  start_at         timestamptz NOT NULL,
  end_at           timestamptz,
  tz               nina.iana_tz NOT NULL,
  volume_ml        numeric(6,1) CHECK (volume_ml > 0 AND volume_ml <= 5000),
  side             text CHECK (side IN ('LEFT', 'RIGHT', 'BOTH')),
  notes            text CHECK (length(notes) <= 500),
  version          bigint NOT NULL DEFAULT 0,
  field_versions   jsonb NOT NULL DEFAULT '{}'::jsonb       -- R-01: {campo: [version, device_id]}; so campos editados apos a criacao
                   CHECK (jsonb_typeof(field_versions) = 'object' AND octet_length(field_versions::text) <= 2048),
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT pumping_interval_ck CHECK (end_at IS NULL OR end_at >= start_at),
  PRIMARY KEY (baby_id, id)
);
CREATE INDEX pumping_timeline_ix ON nina.pumping_session (baby_id, start_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.diaper_event (
  id               uuid NOT NULL,
  baby_id          uuid NOT NULL REFERENCES nina.baby(id) ON DELETE CASCADE,
  occurred_at      timestamptz NOT NULL,
  tz               nina.iana_tz NOT NULL,
  diaper_type      text NOT NULL CHECK (diaper_type IN ('WET', 'DIRTY', 'MIXED', 'DRY', 'UNSPECIFIED')),   -- ADR-0009 (WET urina; DIRTY fezes; MIXED ambos; DRY verificada sem nada)
  notes            text CHECK (length(notes) <= 500),
  version          bigint NOT NULL DEFAULT 0,
  field_versions   jsonb NOT NULL DEFAULT '{}'::jsonb       -- R-01: {campo: [version, device_id]}; so campos editados apos a criacao
                   CHECK (jsonb_typeof(field_versions) = 'object' AND octet_length(field_versions::text) <= 2048),
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  deleted_at       timestamptz,
  created_by       uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  PRIMARY KEY (baby_id, id)
);
CREATE INDEX diaper_timeline_ix ON nina.diaper_event (baby_id, occurred_at DESC) WHERE deleted_at IS NULL;

-- Despertar noturno (ADR-0009): fonte da verdade de nightAwakenings (que e DERIVADO, nunca persistido).
CREATE TABLE nina.wake_event (
  id                  uuid NOT NULL,
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
  field_versions      jsonb NOT NULL DEFAULT '{}'::jsonb      -- R-01
                      CHECK (jsonb_typeof(field_versions) = 'object' AND octet_length(field_versions::text) <= 2048),
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  deleted_at          timestamptz,
  created_by          uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by    uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  CONSTRAINT wake_interval_ck CHECK (ended_at IS NULL OR ended_at >= started_at),
  CONSTRAINT wake_original_ck CHECK (manually_corrected OR (original_started_at IS NULL AND original_ended_at IS NULL)),
  CONSTRAINT wake_original_interval_ck CHECK (original_ended_at IS NULL OR original_started_at IS NULL OR original_ended_at >= original_started_at),
  -- mesma crianca da sessao de sono (impede apontar para sessao de outro bebe)
  CONSTRAINT wake_session_fk FOREIGN KEY (baby_id, sleep_session_id)
    REFERENCES nina.sleep_session (baby_id, id) ON DELETE CASCADE,
  PRIMARY KEY (baby_id, id)
);
COMMENT ON TABLE nina.wake_event IS 'ADR-0009: despertares dentro de uma sessao de sono. Sincronizavel (change_log/tombstone). Limites dentro da sessao NAO sao impostos no banco (edicao offline); validar na API.';
CREATE INDEX wake_session_ix ON nina.wake_event (baby_id, sleep_session_id) WHERE deleted_at IS NULL;
CREATE INDEX wake_timeline_ix ON nina.wake_event (baby_id, started_at DESC) WHERE deleted_at IS NULL;

CREATE TABLE nina.sleep_schedule_preference (   -- RF-014; 1 por bebe
  id                uuid NOT NULL,
  baby_id           uuid NOT NULL UNIQUE REFERENCES nina.baby(id) ON DELETE CASCADE,
  target_nap_count  smallint CHECK (target_nap_count BETWEEN 0 AND 8),
  bedtime_from      time,                          -- hora local do bebe
  bedtime_to        time,
  version           bigint NOT NULL DEFAULT 0,
  field_versions    jsonb NOT NULL DEFAULT '{}'::jsonb      -- R-01
                    CHECK (jsonb_typeof(field_versions) = 'object' AND octet_length(field_versions::text) <= 2048),
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  deleted_at        timestamptz,
  created_by        uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  last_modified_by  uuid REFERENCES nina.app_user(id) ON DELETE SET NULL,
  PRIMARY KEY (baby_id, id)
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
   WHERE baby_id = NEW.baby_id AND sleep_session_id = NEW.id AND deleted_at IS NULL;
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
  IF TG_OP = 'INSERT' THEN PERFORM nina.rls_precheck_baby(NEW.baby_id, TG_TABLE_NAME); END IF;   -- NR-05: SLEEP_OVERLAP nunca antes da RLS
  IF TG_OP = 'UPDATE' AND NEW.start_at IS NOT DISTINCT FROM OLD.start_at AND NEW.end_at IS NOT DISTINCT FROM OLD.end_at THEN
    RETURN NEW;      -- intervalo inalterado: nao re-julga dados aceitos sob a politica anterior
  END IF;
  IF NEW.end_at IS NOT NULL AND NEW.end_at < NEW.start_at THEN
    RETURN NEW;      -- R-08: intervalo invertido: deixa o CHECK sleep_interval_ck falhar (23514, com o nome da constraint)
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

-- R-06: despertar vivo so existe dentro de sessao de sono VIVA do mesmo bebe (a FK so exige a linha, que pode ser um
-- tombstone); R-07: `tz` herdado da sessao quando omitido (WakeEventData nao tem tz). Excluir a sessao tombstona os
-- despertares por trigger (deleted_at preenchido), que passam por aqui sem a exigencia.
CREATE FUNCTION nina.wake_event_session_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_tz text; v_deleted timestamptz; v_found boolean;
BEGIN
  IF TG_OP = 'INSERT' THEN PERFORM nina.rls_precheck_baby(NEW.baby_id, TG_TABLE_NAME); END IF;   -- NR-05
  SELECT true, s.tz, s.deleted_at INTO v_found, v_tz, v_deleted
    FROM nina.sleep_session s WHERE s.baby_id = NEW.baby_id AND s.id = NEW.sleep_session_id;
  IF TG_OP = 'INSERT' AND NEW.tz IS NULL AND v_found THEN NEW.tz := v_tz; END IF;
  IF NEW.deleted_at IS NULL AND (TG_OP = 'INSERT' OR NEW.sleep_session_id IS DISTINCT FROM OLD.sleep_session_id) THEN
    IF v_found IS NOT TRUE OR v_deleted IS NOT NULL THEN
      RAISE EXCEPTION 'despertar exige sessao de sono viva do mesmo bebe (INV-20)' USING ERRCODE = 'NN002';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER wake_event_a_session_guard BEFORE INSERT OR UPDATE ON nina.wake_event
  FOR EACH ROW EXECUTE FUNCTION nina.wake_event_session_guard();

-- Sinalizacao (accept_and_warn): ids das sessoes que se sobrepoem a p_session (respeita RLS do chamador).
-- A versao com p_baby e a correta com PK (baby_id, id); a de 1 argumento fica por compatibilidade (ambigua so se o mesmo id
-- existir em dois bebes legiveis).
CREATE FUNCTION nina.sleep_overlaps(p_baby uuid, p_session uuid) RETURNS SETOF uuid
LANGUAGE sql STABLE AS $$
  SELECT o.id FROM nina.sleep_session s
    JOIN nina.sleep_session o ON o.baby_id = s.baby_id AND o.id <> s.id AND o.deleted_at IS NULL
   WHERE s.baby_id = p_baby AND s.id = p_session AND s.deleted_at IS NULL
     AND tstzrange(o.start_at, coalesce(o.end_at, 'infinity'), '[)') && tstzrange(s.start_at, coalesce(s.end_at, 'infinity'), '[)')
   ORDER BY o.start_at $$;
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
CREATE FUNCTION nina.night_awakenings(p_baby uuid, p_session uuid) RETURNS integer
LANGUAGE sql STABLE AS $$
  SELECT CASE
           WHEN s.id IS NULL OR s.sleep_type <> 'NIGHT' THEN NULL
           WHEN w.n > 0 THEN w.n
           WHEN s.end_at IS NOT NULL
                AND s.end_at - s.start_at >= make_interval(mins => nina.param_int('sleep.night_awakenings.min_session_minutes', 240)) THEN 0
           ELSE NULL
         END
    FROM (SELECT 1) x
    LEFT JOIN nina.sleep_session s ON s.baby_id = p_baby AND s.id = p_session AND s.deleted_at IS NULL
    CROSS JOIN LATERAL (SELECT count(*)::integer AS n FROM nina.wake_event e
                         WHERE e.baby_id = p_baby AND e.sleep_session_id = p_session AND e.deleted_at IS NULL) w $$;
CREATE FUNCTION nina.night_awakenings(p_session uuid) RETURNS integer      -- compatibilidade (ver sleep_overlaps)
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
  event_key        text UNIQUE CHECK (length(event_key) <= 200),
  aggregate_type   text NOT NULL CHECK (aggregate_type ~ '^[A-Z][A-Z0-9_]{0,39}$'),
  aggregate_id     uuid,
  event_type       text NOT NULL CHECK (event_type ~ '^[A-Za-z][A-Za-z0-9_.]{0,79}$'),
  payload          jsonb NOT NULL DEFAULT '{}'::jsonb
                   CHECK (jsonb_typeof(payload) = 'object' AND octet_length(payload::text) <= 4096 AND nina.jsonb_pii_ok(payload)),   -- SR-003/INV-28: sem PII, mesmo aninhada
  created_at       timestamptz NOT NULL DEFAULT now(),
  available_at     timestamptz NOT NULL DEFAULT now(),
  processed_at     timestamptz,
  attempts         smallint NOT NULL DEFAULT 0,
  last_error_code  text CHECK (length(last_error_code) <= 64),
  dead_lettered_at timestamptz
);
CREATE INDEX outbox_pending_ix ON nina.outbox_message (available_at, id)
  WHERE processed_at IS NULL AND dead_lettered_at IS NULL;

-- Catalogo de tipos de mensagem do outbox (NR-11): tipo -> agregado e chaves de payload permitidas. TODA insercao (app, worker,
-- funcoes definer) e validada por gatilho contra o catalogo; o app ainda so insere por colunas restritas e por politica que limita os
-- tipos "do proprio usuario" (ver secao 12). Um tipo novo exige migracao (revisao de seguranca), nunca configuracao.
CREATE TABLE nina.outbox_event_type (
  event_type     text PRIMARY KEY,
  aggregate_type text NOT NULL,
  allowed_keys   text[] NOT NULL DEFAULT ARRAY[]::text[]
);

CREATE FUNCTION nina.outbox_validate() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE t nina.outbox_event_type%ROWTYPE; k text;
BEGIN
  SELECT * INTO t FROM nina.outbox_event_type WHERE event_type = NEW.event_type;
  IF NOT FOUND OR t.aggregate_type <> NEW.aggregate_type THEN
    RAISE EXCEPTION 'tipo de mensagem do outbox desconhecido ou com agregado incompativel' USING ERRCODE = 'NN080';
  END IF;
  FOR k IN SELECT jsonb_object_keys(NEW.payload) LOOP
    IF k <> ALL (t.allowed_keys) THEN
      RAISE EXCEPTION 'chave de payload nao permitida para o tipo de mensagem' USING ERRCODE = 'NN080';
    END IF;
  END LOOP;
  IF NEW.event_type = 'SecurityNoticeRequested'
     AND coalesce(NEW.payload->>'notice', '') NOT IN ('NEW_DEVICE_LOGIN', 'PASSWORD_CHANGED', 'PASSWORD_RESET', 'IDENTITY_LINKED', 'EMAIL_CHANGED') THEN
    RAISE EXCEPTION 'aviso de seguranca invalido' USING ERRCODE = 'NN080';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER outbox_message_validate BEFORE INSERT ON nina.outbox_message FOR EACH ROW EXECUTE FUNCTION nina.outbox_validate();

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

-- SR-012: o app consulta SO o proprio plano/flags (sem parametro de usuario => sem oraculo sobre terceiros).
CREATE FUNCTION nina.my_plan_code() RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT CASE WHEN nina.current_user_id() IS NULL THEN 'free' ELSE nina.user_plan_code(nina.current_user_id()) END $$;

CREATE FUNCTION nina.my_has_feature(p_flag text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT CASE WHEN nina.current_user_id() IS NULL THEN false ELSE nina.user_has_feature(nina.current_user_id(), p_flag) END $$;

-- Helpers das politicas de entitlement (definer: evitam recursao de politicas entre family_entitlement e _member)
CREATE FUNCTION nina.owns_entitlement(p_entitlement uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.family_entitlement e JOIN nina.family f ON f.id = e.family_id
                   WHERE e.id = p_entitlement AND f.owner_user_id = nina.current_user_id()) $$;

CREATE FUNCTION nina.is_entitlement_member(p_entitlement uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.family_entitlement_member m
                   WHERE m.entitlement_id = p_entitlement AND m.user_id = nina.current_user_id() AND m.removed_at IS NULL) $$;

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
  -- SR-006: deny-list PROFUNDA (chaves em qualquer nivel, normalizadas, e valores com formato de e-mail), nao so chaves de topo.
  CONSTRAINT audit_metadata_ck CHECK (
    jsonb_typeof(metadata_safe) = 'object'
    AND octet_length(metadata_safe::text) <= 2048
    AND nina.jsonb_pii_ok(metadata_safe)),
  CONSTRAINT audit_request_id_ck CHECK (length(request_id) <= 128)
);
CREATE INDEX audit_time_ix ON nina.audit_event (occurred_at);
CREATE INDEX audit_actor_ix ON nina.audit_event (actor_user_id, occurred_at DESC);
CREATE INDEX audit_entity_ix ON nina.audit_event (entity_type, entity_id);
CREATE INDEX audit_baby_ix ON nina.audit_event (baby_id, occurred_at DESC) WHERE baby_id IS NOT NULL;

-- Hash da linha da cadeia (SEC-032). O instante e serializado em UTC fixo: independe do TimeZone/DateStyle da sessao.
CREATE FUNCTION nina.audit_row_hash(p_prev bytea, p_seq bigint, p_at timestamptz, p_actor uuid, p_action text, p_entity_type text,
                                    p_entity_id uuid, p_baby uuid, p_result text, p_meta jsonb) RETURNS bytea
LANGUAGE sql IMMUTABLE AS $$
  SELECT sha256(convert_to(concat_ws('|', coalesce(encode(p_prev, 'hex'), ''), p_seq,
           to_char(p_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'), coalesce(p_actor::text, ''), p_action,
           coalesce(p_entity_type, ''), coalesce(p_entity_id::text, ''), coalesce(p_baby::text, ''), p_result, p_meta::text), 'UTF8')) $$;

-- Ancora da cadeia depois de purgas: o ultimo hash removido (a verificacao recomeca dele). Escrita so por purge_audit.
CREATE TABLE nina.audit_chain_checkpoint (
  purged_through_seq bigint PRIMARY KEY,
  row_hash           bytea NOT NULL CHECK (octet_length(row_hash) = 32),
  recorded_at        timestamptz NOT NULL DEFAULT now()
);

-- Encadeamento de hash (SEC-032, SR-005): eventos criticos, negados e de atores nao-USER (SYSTEM/ADMIN/SUPPORT) entram na
-- cadeia, nao so `is_critical`. Truncamento do FIM da cadeia so e detectavel com a ancora externa (WORM): ver
-- nina.audit_chain_head(), exportada periodicamente pelo worker.
CREATE FUNCTION nina.audit_chain() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_prev bytea; v_seq bigint;
BEGIN
  IF NEW.is_critical OR NEW.result = 'DENIED' OR NEW.actor_type <> 'USER' THEN
    -- NR-04: serializa pelo lock de LINHA da tabela privada de controle (nao por advisory lock de chave previsivel); aguarda no maximo
    -- lock_timeout do papel e FALHA ALTO (55P03) em vez de esperar sem limite.
    PERFORM 1 FROM nina.control_lock WHERE name = 'audit_chain' FOR UPDATE;
    SELECT chain_seq, row_hash INTO v_seq, v_prev FROM nina.audit_event
     WHERE chain_seq IS NOT NULL ORDER BY chain_seq DESC LIMIT 1;
    IF v_seq IS NULL THEN   -- cadeia vazia (apos purga total): continua do checkpoint
      SELECT purged_through_seq, row_hash INTO v_seq, v_prev FROM nina.audit_chain_checkpoint ORDER BY purged_through_seq DESC LIMIT 1;
    END IF;
    NEW.chain_seq := coalesce(v_seq, 0) + 1;
    NEW.prev_hash := v_prev;
    NEW.row_hash := nina.audit_row_hash(v_prev, NEW.chain_seq, NEW.occurred_at, NEW.actor_user_id, NEW.action, NEW.entity_type,
                                        NEW.entity_id, NEW.baby_id, NEW.result, NEW.metadata_safe);
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER audit_event_chain BEFORE INSERT ON nina.audit_event FOR EACH ROW EXECUTE FUNCTION nina.audit_chain();

-- Imutabilidade (SR-005): UPDATE e TRUNCATE sempre proibidos. DELETE so quando a funcao de purga correspondente
-- (SECURITY DEFINER, com piso de idade) emitiu a ficha 'purge_<tabela>' NESTA transacao. Nenhum papel tem o privilegio de
-- DELETE nestas tabelas; a ficha nao e um GUC definivel pelo chamador (ver nina.guard_ok). config_change nunca e apagada.
CREATE FUNCTION nina.forbid_mutation() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF TG_OP = 'DELETE' AND TG_ARGV[0] <> 'config' AND nina.guard_ok('purge_' || TG_ARGV[0]) THEN
    RETURN OLD;
  END IF;
  RAISE EXCEPTION 'tabela % e append-only (% proibido)', TG_TABLE_NAME, TG_OP USING ERRCODE = 'NN030';
END $$;
CREATE TRIGGER consent_record_immutable BEFORE UPDATE OR DELETE ON nina.consent_record FOR EACH ROW EXECUTE FUNCTION nina.forbid_mutation('consent');
CREATE TRIGGER consent_record_no_truncate BEFORE TRUNCATE ON nina.consent_record FOR EACH STATEMENT EXECUTE FUNCTION nina.forbid_mutation('consent');
CREATE TRIGGER audit_event_immutable BEFORE UPDATE OR DELETE ON nina.audit_event FOR EACH ROW EXECUTE FUNCTION nina.forbid_mutation('audit');
CREATE TRIGGER audit_event_no_truncate BEFORE TRUNCATE ON nina.audit_event FOR EACH STATEMENT EXECUTE FUNCTION nina.forbid_mutation('audit');
CREATE TRIGGER config_change_immutable BEFORE UPDATE OR DELETE ON nina.config_change FOR EACH ROW EXECUTE FUNCTION nina.forbid_mutation('config');
CREATE TRIGGER config_change_no_truncate BEFORE TRUNCATE ON nina.config_change FOR EACH STATEMENT EXECUTE FUNCTION nina.forbid_mutation('config');

-- Catalogo de acoes de auditoria (SR-006). O app so grava auditoria por nina.audit()/audit_auth_attempt(), que validam a
-- acao, o resultado e as CHAVES de metadata_safe contra este catalogo. Quem altera o catalogo e o dono (migracao).
CREATE TABLE nina.audit_action (
  action       text PRIMARY KEY CHECK (action ~ '^[a-z][a-z0-9_.]*$'),
  is_critical  boolean NOT NULL DEFAULT false,
  caller       text NOT NULL CHECK (caller IN ('USER', 'PRE_AUTH', 'PRIVILEGED')),   -- quem pode registrar: usuario autenticado | tentativa sem sessao | worker
  results      text[] NOT NULL DEFAULT ARRAY['SUCCESS'],
  allowed_keys text[] NOT NULL DEFAULT ARRAY[]::text[],
  CONSTRAINT audit_action_results_ck CHECK (results <@ ARRAY['SUCCESS', 'FAILURE', 'DENIED'])
);

CREATE FUNCTION nina.audit_check_args(p_action text, p_result text, p_metadata jsonb, p_caller text) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE a nina.audit_action%ROWTYPE; k text;
BEGIN
  SELECT * INTO a FROM nina.audit_action WHERE action = p_action AND caller = p_caller;
  IF NOT FOUND THEN RAISE EXCEPTION 'acao de auditoria desconhecida ou nao permitida a este chamador' USING ERRCODE = 'NN061'; END IF;
  IF p_result <> ALL (a.results) THEN RAISE EXCEPTION 'resultado nao permitido para a acao' USING ERRCODE = 'NN061'; END IF;
  IF jsonb_typeof(p_metadata) <> 'object' OR octet_length(p_metadata::text) > 2048 THEN
    RAISE EXCEPTION 'metadata invalido' USING ERRCODE = 'NN062';
  END IF;
  FOR k IN SELECT jsonb_object_keys(p_metadata) LOOP
    IF k <> ALL (a.allowed_keys) THEN RAISE EXCEPTION 'chave de metadata nao permitida para a acao' USING ERRCODE = 'NN062'; END IF;
  END LOOP;
  IF NOT nina.jsonb_pii_ok(p_metadata) THEN RAISE EXCEPTION 'metadata com PII' USING ERRCODE = 'NN062'; END IF;
  RETURN a.is_critical;
END $$;

-- Evento do USUARIO AUTENTICADO: o ator e SEMPRE o usuario do contexto (nao ha parametro de ator); acao/chaves vem do
-- catalogo; severidade (is_critical) vem do catalogo; ip_hash e HMAC calculado pela API com pepper (o banco so valida o tamanho).
CREATE FUNCTION nina.audit(p_action text, p_entity_type text DEFAULT NULL, p_entity_id uuid DEFAULT NULL, p_baby uuid DEFAULT NULL,
                           p_device uuid DEFAULT NULL, p_request_id text DEFAULT NULL, p_result text DEFAULT 'SUCCESS',
                           p_ip_hash bytea DEFAULT NULL, p_metadata jsonb DEFAULT '{}'::jsonb) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_critical boolean; v_id bigint;
BEGIN
  IF v_user IS NULL OR NOT EXISTS (SELECT 1 FROM nina.app_user WHERE id = v_user) THEN
    RAISE EXCEPTION 'auditoria exige usuario valido no contexto (nina.user_id)' USING ERRCODE = 'NN060';
  END IF;
  v_critical := nina.audit_check_args(p_action, p_result, p_metadata, 'USER');
  IF p_baby IS NOT NULL AND NOT nina.can_read_baby(p_baby) THEN
    RAISE EXCEPTION 'auditoria de bebe sem vinculo ativo' USING ERRCODE = 'NN060';
  END IF;
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, baby_id, device_id, request_id,
                                result, ip_hash, metadata_safe, is_critical)
  VALUES (v_user, 'USER', p_action, p_entity_type, p_entity_id, p_baby, p_device, p_request_id, p_result, p_ip_hash,
          coalesce(p_metadata, '{}'::jsonb), v_critical)
  RETURNING id INTO v_id;
  RETURN v_id;
END $$;

-- Tentativa SEM sessao (login/verificacao/reauth falhos): so acoes PRE_AUTH, sempre FAILURE e nunca critica; o alvo (se a
-- conta existe) e o "ator" da tentativa. Residual aceito: quem executa SQL como nina_app pode registrar falhas em nome
-- de qualquer conta; nao forja sucesso, exclusao nem evento critico.
CREATE FUNCTION nina.audit_auth_attempt(p_action text, p_target_user uuid DEFAULT NULL, p_device uuid DEFAULT NULL,
                                        p_request_id text DEFAULT NULL, p_ip_hash bytea DEFAULT NULL,
                                        p_metadata jsonb DEFAULT '{}'::jsonb) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_actor uuid; v_id bigint;
BEGIN
  PERFORM nina.audit_check_args(p_action, 'FAILURE', coalesce(p_metadata, '{}'::jsonb), 'PRE_AUTH');
  SELECT id INTO v_actor FROM nina.app_user WHERE id = p_target_user;
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, device_id, request_id, result, ip_hash, metadata_safe)
  VALUES (v_actor, CASE WHEN v_actor IS NULL THEN 'SYSTEM' ELSE 'USER' END, p_action,
          CASE WHEN v_actor IS NULL THEN NULL ELSE 'user' END, v_actor, p_device, p_request_id, 'FAILURE', p_ip_hash,
          coalesce(p_metadata, '{}'::jsonb))
  RETURNING id INTO v_id;
  RETURN v_id;
END $$;

-- Eventos SYSTEM/ADMIN/SUPPORT: so o worker (GRANT EXECUTE), so acoes PRIVILEGED do catalogo.
CREATE FUNCTION nina.audit_privileged(p_actor_type text, p_action text, p_actor uuid DEFAULT NULL, p_entity_type text DEFAULT NULL,
                                      p_entity_id uuid DEFAULT NULL, p_baby uuid DEFAULT NULL, p_result text DEFAULT 'SUCCESS',
                                      p_metadata jsonb DEFAULT '{}'::jsonb) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_critical boolean; v_id bigint;
BEGIN
  IF p_actor_type NOT IN ('SYSTEM', 'ADMIN', 'SUPPORT') THEN
    RAISE EXCEPTION 'actor_type invalido para evento privilegiado' USING ERRCODE = 'NN061';
  END IF;
  v_critical := nina.audit_check_args(p_action, p_result, coalesce(p_metadata, '{}'::jsonb), 'PRIVILEGED');
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, baby_id, result, metadata_safe, is_critical)
  VALUES (p_actor, p_actor_type, p_action, p_entity_type, p_entity_id, p_baby, p_result, coalesce(p_metadata, '{}'::jsonb), v_critical)
  RETURNING id INTO v_id;
  RETURN v_id;
END $$;

-- Leitura da PROPRIA trilha (RF-054/LGPD art. 18): so eventos em que o usuario e o ator, sem ip_hash nem request_id.
CREATE FUNCTION nina.my_audit_events(p_limit integer DEFAULT 50, p_before bigint DEFAULT NULL)
RETURNS TABLE (id bigint, occurred_at timestamptz, action text, entity_type text, entity_id uuid, result text, device_id uuid, metadata_safe jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT e.id, e.occurred_at, e.action, e.entity_type, e.entity_id, e.result, e.device_id, e.metadata_safe
    FROM nina.audit_event e
   WHERE e.actor_user_id = nina.current_user_id() AND nina.current_user_id() IS NOT NULL
     AND (p_before IS NULL OR e.id < p_before)
   ORDER BY e.id DESC
   LIMIT least(greatest(coalesce(p_limit, 50), 1), 200) $$;

-- Verificacao e ancora da cadeia (SEC-032)
CREATE FUNCTION nina.verify_audit_chain() RETURNS TABLE (ok boolean, broken_chain_seq bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE r record; v_prev bytea; v_prev_seq bigint;
BEGIN
  SELECT c.row_hash, c.purged_through_seq INTO v_prev, v_prev_seq FROM nina.audit_chain_checkpoint c
   WHERE c.purged_through_seq < coalesce((SELECT min(chain_seq) FROM nina.audit_event WHERE chain_seq IS NOT NULL), 9223372036854775807)
   ORDER BY c.purged_through_seq DESC LIMIT 1;
  FOR r IN SELECT * FROM nina.audit_event WHERE chain_seq IS NOT NULL ORDER BY chain_seq LOOP
    IF (v_prev_seq IS NOT NULL AND r.chain_seq <> v_prev_seq + 1)
       OR r.prev_hash IS DISTINCT FROM v_prev
       OR r.row_hash IS DISTINCT FROM nina.audit_row_hash(r.prev_hash, r.chain_seq, r.occurred_at, r.actor_user_id, r.action,
                                                          r.entity_type, r.entity_id, r.baby_id, r.result, r.metadata_safe) THEN
      RETURN QUERY SELECT false, r.chain_seq; RETURN;
    END IF;
    v_prev := r.row_hash; v_prev_seq := r.chain_seq;
  END LOOP;
  RETURN QUERY SELECT true, NULL::bigint;
END $$;

CREATE FUNCTION nina.audit_chain_head() RETURNS TABLE (chain_seq bigint, row_hash bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT e.chain_seq, e.row_hash FROM nina.audit_event e WHERE e.chain_seq IS NOT NULL ORDER BY e.chain_seq DESC LIMIT 1 $$;

CREATE FUNCTION nina.log_config_change() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  v_row jsonb := to_jsonb(coalesce(NEW, OLD));
  v_actor uuid := nina.current_user_id();
  v_key text;
BEGIN
  IF v_actor IS NULL AND NOT nina.guard_ok('migration') THEN
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
    WHEN 'invites.max_per_day' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}') !~ '^[0-9]+$'
         OR (NEW.value #>> '{}')::numeric NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'invites.max_per_day deve ser int entre 1 e 200' USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'invites.max_failed_attempts', 'auth.email_code_max_attempts' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}') !~ '^[0-9]+$'
         OR (NEW.value #>> '{}')::numeric NOT BETWEEN 1 AND 10 THEN
        RAISE EXCEPTION '% deve ser int entre 1 e 10', NEW.param_key USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'audit.retention_months' THEN     -- pisos de SR-005: nunca abaixo de 12 (comum) / 60 (critica e consentimento)
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}') !~ '^[0-9]+$'
         OR (NEW.value #>> '{}')::numeric NOT BETWEEN 12 AND 240 THEN
        RAISE EXCEPTION 'audit.retention_months deve ser int entre 12 e 240' USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'audit.critical_retention_months', 'consent.retention_months' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}') !~ '^[0-9]+$'
         OR (NEW.value #>> '{}')::numeric NOT BETWEEN 60 AND 240 THEN
        RAISE EXCEPTION '% deve ser int entre 60 e 240', NEW.param_key USING ERRCODE = 'check_violation';
      END IF;
    WHEN 'erasure_ledger.retention_days' THEN
      IF NEW.value_type <> 'INT' OR jsonb_typeof(NEW.value) <> 'number' OR (NEW.value #>> '{}') !~ '^[0-9]+$'
         OR (NEW.value #>> '{}')::numeric NOT BETWEEN 35 AND 3650 THEN
        RAISE EXCEPTION 'erasure_ledger.retention_days deve ser int entre 35 e 3650 (>= ciclo de backup)' USING ERRCODE = 'check_violation';
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
  baby_ids       uuid[] NOT NULL DEFAULT '{}',           -- SR-011: bebes cujo conteudo o arquivo contem (erase_baby invalida o arquivo)
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
  reauth_jti_hash     bytea CHECK (octet_length(reauth_jti_hash) = 32),    -- SR-016: prova da reautenticacao (nina.reauth_jti)
  policy_applied      text CHECK (policy_applied IN ('CASCADE', 'BLOCK', 'TRANSFER_OWNERSHIP')),   -- preenchido por erase_user
  completed_at  timestamptz,
  cancelled_at  timestamptz,
  CONSTRAINT deletion_confirmation_ck CHECK ((confirmed_at IS NULL) = (confirmation_method IS NULL) AND (confirmed_at IS NULL OR reauth_jti_hash IS NOT NULL)),
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
     OR (NOT (OLD.confirmed_at IS NULL AND nina.guard_ok('deletion_confirm'))
         AND (NEW.confirmed_at IS DISTINCT FROM OLD.confirmed_at OR NEW.confirmation_method IS DISTINCT FROM OLD.confirmation_method
              OR NEW.reauth_jti_hash IS DISTINCT FROM OLD.reauth_jti_hash)) THEN
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
  request_type        text NOT NULL CHECK (request_type IN ('ACCESS', 'CORRECTION', 'EXPORT', 'ANONYMIZATION')),
  -- SR-007: ANONYMIZATION nasce SCHEDULED (janela de arrependimento, igual a da exclusao de conta); IN_PROGRESS = em atendimento
  status              text NOT NULL DEFAULT 'REQUESTED' CHECK (status IN ('REQUESTED', 'SCHEDULED', 'IN_PROGRESS', 'COMPLETED', 'REJECTED', 'CANCELLED')),
  requested_at        timestamptz NOT NULL DEFAULT now(),
  due_at              timestamptz NOT NULL,                       -- requested_at + privacy.request_response_days (15)
  scheduled_for       timestamptz,                                -- ANONYMIZATION: requested_at + privacy.deletion_grace_days; fulfill nunca antes
  identity_verified_at timestamptz,                               -- verificacao de identidade (privacy-spec 5)
  verification_method text CHECK (verification_method IN ('REAUTHENTICATION')),
  reauth_jti_hash     bytea CHECK (octet_length(reauth_jti_hash) = 32),   -- prova da reautenticacao (nina.reauth_jti)
  completed_at        timestamptz,
  cancelled_at        timestamptz,
  closed_reason       text CHECK (length(closed_reason) <= 64),   -- codigo curto, sem PII
  CONSTRAINT privacy_verification_ck CHECK ((identity_verified_at IS NULL) = (verification_method IS NULL)),
  CONSTRAINT privacy_erasure_verified_ck CHECK (request_type <> 'ANONYMIZATION' OR (identity_verified_at IS NOT NULL AND reauth_jti_hash IS NOT NULL)),
  CONSTRAINT privacy_schedule_ck CHECK ((request_type = 'ANONYMIZATION') = (scheduled_for IS NOT NULL))
);
CREATE UNIQUE INDEX privacy_request_open_uq ON nina.privacy_request (user_id, request_type) WHERE status IN ('REQUESTED', 'SCHEDULED', 'IN_PROGRESS');
CREATE INDEX privacy_request_due_ix ON nina.privacy_request (due_at) WHERE status IN ('REQUESTED', 'SCHEDULED', 'IN_PROGRESS');
COMMENT ON TABLE nina.privacy_request IS 'ADR-0010 item 3: caminho de DSAR para nao-Owner. ANONYMIZATION anonimiza dados pessoais proprios (fulfill_privacy_erasure) e nunca apaga dado de bebe.';

-- -----------------------------------------------------------------------------
-- 9. Exclusão (ADR-0008) e purga/retenção
--    Todas as funcoes abaixo sao SECURITY DEFINER (search_path fixo): emitem as fichas (guard_arm) que liberam as unicas
--    mudancas privilegiadas (autoria, vinculo, propriedade) e dispensam o worker de DML direto nas tabelas de tenant.
-- -----------------------------------------------------------------------------

-- Lock de uma purga (NR-04): linha da tabela privada de controle, SEM esperar. Se outra execucao a segura, a purga FALHA com
-- PURGE_LOCK_BUSY (NN032): o agendador do worker ve o erro e alerta, em vez de a retencao parar em silencio.
CREATE FUNCTION nina.take_purge_lock(p_name text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  PERFORM 1 FROM nina.control_lock WHERE name = p_name FOR UPDATE NOWAIT;
  IF NOT FOUND THEN RAISE EXCEPTION 'lock de purga % inexistente', p_name USING ERRCODE = 'NN032'; END IF;
EXCEPTION WHEN lock_not_available THEN
  RAISE EXCEPTION 'PURGE_LOCK_BUSY: a purga % ja esta em execucao (ou travada); nada foi apagado', p_name USING ERRCODE = 'NN032';
END $$;

-- Fila de exclusoes que sobrevive aos backups (SEC-064/SR-011): so ids, sem PII. Apos um restore, reapply_erasure_ledger()
-- reexecuta as exclusoes que o backup ressuscitou. Retencao >= ciclo de backup (35 dias): 'erasure_ledger.retention_days'.
CREATE TABLE nina.erasure_ledger (
  id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  entity_type text NOT NULL CHECK (entity_type IN ('BABY', 'USER')),
  entity_id   uuid NOT NULL,
  erased_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE (entity_type, entity_id)
);

-- Apaga o CONTEUDO do bebe de imediato e deixa casca-tombstone (sem PII) que
-- propaga a exclusao aos dispositivos; a casca some na purga (>= 90 dias).
CREATE FUNCTION nina.erase_baby(p_baby uuid, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
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
  -- SR-011: exportacoes que contem o bebe deixam de existir (o arquivo no storage e removido pelo consumidor do outbox)
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  SELECT 'EXPORT', e.id, 'ExportFileInvalidated' FROM nina.data_export_request e
   WHERE p_baby = ANY (e.baby_ids) AND e.file_ref IS NOT NULL;
  UPDATE nina.data_export_request SET status = 'EXPIRED', file_ref = NULL
   WHERE p_baby = ANY (baby_ids) AND (file_ref IS NOT NULL OR status IN ('REQUESTED', 'PROCESSING'));
  -- NR-07: TODOS os vinculos (inclusive o do Owner) sao encerrados; o check de INV-09 ignora bebe excluido e a casca fica ilegivel
  -- (baby_role/readable_babies ignoram bebe com deleted_at).
  PERFORM nina.guard_arm('membership');
  UPDATE nina.caregiver_membership
     SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'BABY_DELETED',
         invited_email = NULL, invite_token_hash = NULL
   WHERE baby_id = p_baby AND status IN ('PENDING', 'ACTIVE');
  PERFORM nina.guard_disarm('membership');
  PERFORM nina.guard_arm('baby_delete');
  UPDATE nina.baby
     SET deleted_at = now(), display_name = NULL, birth_date = NULL, due_date = NULL,
         sex = NULL, photo_ref = NULL, field_versions = '{}'::jsonb, last_modified_by = p_actor
   WHERE id = p_baby;                                     -- triggers: version, change_log 'DELETE', tombstone
  PERFORM nina.guard_disarm('baby_delete');
  INSERT INTO nina.erasure_ledger (entity_type, entity_id) VALUES ('BABY', p_baby) ON CONFLICT DO NOTHING;
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, is_critical)
  VALUES (p_actor, 'baby.erased', 'BABY', p_baby, p_baby, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('BABY', p_baby, 'BabyDeleted');
END $$;

-- Exclusao de bebe pelo Owner (NR-07, contrato 1.0.1: DELETE /babies/{id}). UNICO caminho do app: reautenticacao BABY_DELETE comprovada e
-- vinculada ao bebe, reconhecimento explicito quando ha outros cuidadores ativos, auditoria com o jti (hash) e aviso a todos os
-- cuidadores ativos (e ao Owner) pelo outbox, e so entao o apagamento (erase_baby). O UPDATE direto de baby.deleted_at e negado
-- (coluna fora do GRANT + gatilho NN052). A exclusao continua IMEDIATA (a janela de arrependimento e decisao de produto, V2-05).
CREATE FUNCTION nina.delete_baby(p_baby uuid, p_reauth_jti_hash bytea, p_acknowledge_others boolean DEFAULT false) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_others integer;
BEGIN
  IF v_user IS NULL OR NOT EXISTS (SELECT 1 FROM nina.caregiver_membership m JOIN nina.baby b ON b.id = m.baby_id AND b.deleted_at IS NULL
                                    WHERE m.baby_id = p_baby AND m.user_id = v_user AND m.role = 'OWNER' AND m.status = 'ACTIVE') THEN
    RAISE EXCEPTION 'vinculo inexistente ou sem permissao' USING ERRCODE = 'NN059';        -- uniforme: bebe alheio, inexistente ou sem ser Owner
  END IF;
  PERFORM 1 FROM nina.baby WHERE id = p_baby AND deleted_at IS NULL FOR UPDATE;
  SELECT count(*) INTO v_others FROM nina.caregiver_membership m WHERE m.baby_id = p_baby AND m.status = 'ACTIVE' AND m.user_id <> v_user;
  IF v_others > 0 AND NOT coalesce(p_acknowledge_others, false) THEN
    RAISE EXCEPTION 'OWNER_DECISION_REQUIRED: o bebe tem outros cuidadores ativos; confirme explicitamente (acknowledge_other_caregivers)' USING ERRCODE = 'NN007';
  END IF;
  IF NOT nina.reauth_bind(p_reauth_jti_hash, 'BABY_DELETE', 'BABY', p_baby) THEN
    RAISE EXCEPTION 'REAUTH_REQUIRED: exclusao do bebe exige reautenticacao comprovada' USING ERRCODE = 'NN014';
  END IF;
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
  VALUES (v_user, 'baby.deleted_by_owner', 'BABY', p_baby, p_baby,
          jsonb_build_object('other_active_members', v_others, 'jti', encode(p_reauth_jti_hash, 'hex')), true);
  IF v_others > 0 THEN    -- SR-016: o Owner e TODOS os cuidadores ativos sao avisados (o worker envia; o outbox nao leva PII)
    INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload)
    SELECT 'USER', m.user_id, 'SharedBabyDeletedNotice', jsonb_build_object('baby_id', p_baby)
      FROM nina.caregiver_membership m WHERE m.baby_id = p_baby AND m.status = 'ACTIVE';
  END IF;
  PERFORM nina.erase_baby(p_baby, v_user);
END $$;

-- E o Owner ativo de ao menos um bebe vivo? (so ele exclui a conta - ADR-0010 item 3)
CREATE FUNCTION nina.is_active_baby_owner(p_user uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS
$$ SELECT EXISTS (SELECT 1 FROM nina.caregiver_membership m
                    JOIN nina.baby b ON b.id = m.baby_id AND b.deleted_at IS NULL
                   WHERE m.user_id = p_user AND m.role = 'OWNER' AND m.status = 'ACTIVE') $$;

-- Remove/anonimiza os dados pessoais PROPRIOS de um usuario, sem tocar em dados de bebe (ADR-0010 item 3).
-- Comum a erase_user (depois de tratar os bebes do Owner) e a fulfill_privacy_erasure (nao-Owner).
-- Devolve so contagens (usadas na auditoria, sem PII).
--  * consentimentos: append-only; os ainda concedidos ganham linha REVOKED (source SYSTEM). A prova minima
--    (ids, finalidade, versao, datas) permanece ligada a app_user ja anonimizado (privacy-spec 4, F12).
--  * autoria: created_by/last_modified_by que apontam para o usuario viram NULL nos dados de bebe (inclusive
--    tombstones), sem nova versao e sem change_log (ficha 'authorship_scrub', ver triggers de sync). change_log.actor_user_id idem.
--  * vinculos de cuidador, preferencias/jobs de notificacao, tokens de push, recuperacao, codigos de e-mail, ledger de
--    reautenticacao, identidades, credencial, sessoes/refresh: apagados. Entitlement: membro removido. Exports: file_ref zerado.
--  * app_user: anonimizado (status DELETED, sem e-mail/nome/locale/fuso).
CREATE FUNCTION nina.scrub_user_personal_data(p_user uuid, p_actor uuid DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  n_consent integer; n_auth integer := 0; n_tmp integer; n_member integer; n_push integer; n_sess integer; t text;
BEGIN
  INSERT INTO nina.consent_record (user_id, subject_baby_id, purpose_key, policy_version, text_hash, locale, status, source, platform)
  SELECT c.user_id, c.subject_baby_id, c.purpose_key, c.policy_version, c.text_hash, c.locale, 'REVOKED', 'SYSTEM', 'SERVER'
    FROM nina.consent_current c WHERE c.user_id = p_user AND c.status = 'GRANTED';
  GET DIAGNOSTICS n_consent = ROW_COUNT;

  PERFORM nina.guard_arm('authorship_scrub');
  PERFORM set_config('nina.scrub_user', p_user::text, true);
  FOREACH t IN ARRAY ARRAY['baby', 'sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'wake_event', 'sleep_schedule_preference'] LOOP
    EXECUTE format('UPDATE nina.%I SET created_by = created_by WHERE created_by = $1 OR last_modified_by = $1', t) USING p_user;
    GET DIAGNOSTICS n_tmp = ROW_COUNT; n_auth := n_auth + n_tmp;
  END LOOP;
  PERFORM nina.guard_disarm('authorship_scrub');
  PERFORM set_config('nina.scrub_user', '', true);
  UPDATE nina.change_log SET actor_user_id = NULL WHERE actor_user_id = p_user;

  PERFORM nina.guard_arm('membership');
  UPDATE nina.caregiver_membership SET invited_by = NULL WHERE invited_by = p_user;
  PERFORM nina.guard_disarm('membership');
  DELETE FROM nina.caregiver_membership WHERE user_id = p_user;
  GET DIAGNOSTICS n_member = ROW_COUNT;
  DELETE FROM nina.notification_job WHERE user_id = p_user;
  DELETE FROM nina.notification_preference WHERE user_id = p_user;
  DELETE FROM nina.device_push_token WHERE user_id = p_user;
  GET DIAGNOSTICS n_push = ROW_COUNT;
  DELETE FROM nina.recovery_request WHERE user_id = p_user;
  DELETE FROM nina.email_verification_code WHERE user_id = p_user;
  DELETE FROM nina.email_change_request WHERE user_id = p_user;
  DELETE FROM nina.reauth_jti WHERE user_id = p_user;
  DELETE FROM nina.user_identity WHERE user_id = p_user;
  DELETE FROM nina.user_credential WHERE user_id = p_user;
  DELETE FROM nina.auth_session WHERE user_id = p_user;              -- cascata: refresh_token
  GET DIAGNOSTICS n_sess = ROW_COUNT;
  UPDATE nina.family_entitlement_member SET removed_at = now() WHERE user_id = p_user AND removed_at IS NULL;
  UPDATE nina.data_export_request SET file_ref = NULL, status = 'EXPIRED' WHERE user_id = p_user AND file_ref IS NOT NULL;
  UPDATE nina.app_user SET status = 'DELETED', email = NULL, display_name = NULL, email_verified_at = NULL, locale = NULL,
         timezone = NULL, deleted_at = now() WHERE id = p_user;
  INSERT INTO nina.erasure_ledger (entity_type, entity_id) VALUES ('USER', p_user) ON CONFLICT DO NOTHING;
  RETURN jsonb_build_object('consents_revoked', n_consent, 'authorship_rows', n_auth, 'memberships_removed', n_member,
                            'push_devices', n_push, 'sessions', n_sess);
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
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
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
      PERFORM nina.guard_arm('membership'); PERFORM nina.guard_arm('ownership'); PERFORM nina.guard_arm('baby_family');
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
      PERFORM nina.guard_disarm('membership'); PERFORM nina.guard_disarm('ownership'); PERFORM nina.guard_disarm('baby_family');
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
        -- SR-016: cada cuidador ativo e avisado (o worker envia; o outbox nao leva PII)
        INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload)
        SELECT 'USER', m.user_id, 'SharedBabyDeletedNotice', jsonb_build_object('baby_id', r.baby_id)
          FROM nina.caregiver_membership m WHERE m.baby_id = r.baby_id AND m.status = 'ACTIVE' AND m.user_id <> p_user;
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
-- Abre o pedido do PROPRIO usuario (nina.user_id). SR-007: a verificacao e OBRIGATORIA (sem DEFAULT) e comprovada pelo
-- livro-razao de reautenticacao (escopo PRIVACY_REQUEST, consumido nos ultimos 10 min, uso unico), nao por texto livre.
-- ANONYMIZATION nao e aceita de Owner ativo de bebe (NN013) e nasce SCHEDULED: so e cumprida apos scheduled_for
-- (= requested_at + privacy.deletion_grace_days), cancelavel ate la, como a exclusao de conta.
CREATE FUNCTION nina.open_privacy_request(p_type text, p_verification text, p_reauth_jti_hash bytea) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_id uuid := gen_random_uuid(); v_due timestamptz; v_sched timestamptz;
BEGIN
  IF v_user IS NULL OR NOT EXISTS (SELECT 1 FROM nina.app_user WHERE id = v_user AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'requisicao de privacidade exige usuario ativo no contexto (nina.user_id)' USING ERRCODE = 'NN015';
  END IF;
  IF p_type IS NULL OR p_type NOT IN ('ACCESS', 'CORRECTION', 'EXPORT', 'ANONYMIZATION') THEN
    RAISE EXCEPTION 'tipo de requisicao invalido' USING ERRCODE = 'NN015';
  END IF;
  IF p_type = 'ANONYMIZATION' AND nina.is_active_baby_owner(v_user) THEN
    RAISE EXCEPTION 'OWNER_MUST_USE_ACCOUNT_DELETION: Owner ativo exclui a conta pelo fluxo de exclusao (ADR-0010)' USING ERRCODE = 'NN013';
  END IF;
  IF p_verification IS DISTINCT FROM 'REAUTHENTICATION'
     OR NOT nina.reauth_bind(p_reauth_jti_hash, 'PRIVACY_REQUEST', 'PRIVACY_REQUEST', v_id) THEN
    RAISE EXCEPTION 'IDENTITY_NOT_VERIFIED: requisicao exige reautenticacao comprovada' USING ERRCODE = 'NN014';
  END IF;
  v_due := now() + make_interval(days => nina.param_int('privacy.request_response_days', 15));
  IF p_type = 'ANONYMIZATION' THEN
    v_sched := now() + make_interval(days => nina.param_int('privacy.deletion_grace_days', 7));
  END IF;
  INSERT INTO nina.privacy_request (id, user_id, request_type, status, due_at, scheduled_for, identity_verified_at, verification_method, reauth_jti_hash)
  VALUES (v_id, v_user, p_type, CASE WHEN p_type = 'ANONYMIZATION' THEN 'SCHEDULED' ELSE 'REQUESTED' END, v_due, v_sched, now(),
          'REAUTHENTICATION', p_reauth_jti_hash);
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (v_user, 'privacy.request_opened', 'PRIVACY_REQUEST', v_id,
          jsonb_build_object('request_type', p_type, 'due_at', v_due), true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload)   -- o worker avisa o e-mail da conta
  VALUES ('PRIVACY_REQUEST', v_id, 'PrivacyRequestOpened', jsonb_build_object('request_type', p_type));
  RETURN v_id;
END $$;

CREATE FUNCTION nina.cancel_privacy_request(p_request uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id();
BEGIN
  UPDATE nina.privacy_request SET status = 'CANCELLED', cancelled_at = now(), closed_reason = 'USER_CANCELLED'
   WHERE id = p_request AND user_id = v_user AND status IN ('REQUESTED', 'SCHEDULED');
  IF NOT FOUND THEN
    RAISE EXCEPTION 'requisicao inexistente, de outro usuario ou ja encerrada' USING ERRCODE = 'NN015';
  END IF;
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (v_user, 'privacy.request_cancelled', 'PRIVACY_REQUEST', p_request, jsonb_build_object('request_id', p_request), true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('PRIVACY_REQUEST', p_request, 'PrivacyRequestCancelled');
END $$;

-- Worker: encerra ACCESS/CORRECTION/EXPORT (cumpridos pela API) como COMPLETED ou REJECTED. ANONYMIZATION so por fulfill.
CREATE FUNCTION nina.close_privacy_request(p_request uuid, p_status text, p_reason text DEFAULT NULL, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_req nina.privacy_request%ROWTYPE;
BEGIN
  IF p_status NOT IN ('COMPLETED', 'REJECTED') THEN
    RAISE EXCEPTION 'status de encerramento invalido: %', p_status USING ERRCODE = 'NN015';
  END IF;
  SELECT * INTO v_req FROM nina.privacy_request WHERE id = p_request AND status IN ('REQUESTED', 'SCHEDULED', 'IN_PROGRESS') FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'requisicao inexistente ou ja encerrada' USING ERRCODE = 'NN015'; END IF;
  IF v_req.request_type = 'ANONYMIZATION' AND p_status = 'COMPLETED' THEN
    RAISE EXCEPTION 'ANONYMIZATION so e concluida por fulfill_privacy_erasure' USING ERRCODE = 'NN015';
  END IF;
  UPDATE nina.privacy_request SET status = p_status, completed_at = now(), closed_reason = left(p_reason, 64) WHERE id = p_request;
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'SYSTEM' ELSE 'ADMIN' END, 'privacy.request_closed', 'PRIVACY_REQUEST', p_request,
          jsonb_build_object('request_type', v_req.request_type, 'status', p_status, 'on_time', now() <= v_req.due_at), true);
END $$;

-- Worker: cumpre ANONYMIZATION de nao-Owner. Anonimiza os dados pessoais proprios (scrub_user_personal_data) e NAO apaga
-- nada de bebe. Recusa Owner ativo (NN013), pedido sem identidade verificada (NN014) e execucao antes de scheduled_for (NN008).
CREATE FUNCTION nina.fulfill_privacy_erasure(p_request uuid, p_actor uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_req nina.privacy_request%ROWTYPE; v_scrub jsonb;
BEGIN
  SELECT * INTO v_req FROM nina.privacy_request WHERE id = p_request FOR UPDATE;
  IF NOT FOUND OR v_req.request_type <> 'ANONYMIZATION' THEN
    RAISE EXCEPTION 'requisicao ANONYMIZATION inexistente' USING ERRCODE = 'NN015';
  END IF;
  IF v_req.status = 'COMPLETED' THEN RETURN; END IF;                  -- idempotente
  IF v_req.status NOT IN ('REQUESTED', 'SCHEDULED', 'IN_PROGRESS') THEN
    RAISE EXCEPTION 'requisicao encerrada (%)', v_req.status USING ERRCODE = 'NN015';
  END IF;
  IF v_req.identity_verified_at IS NULL THEN
    RAISE EXCEPTION 'IDENTITY_NOT_VERIFIED' USING ERRCODE = 'NN014';
  END IF;
  IF now() < v_req.scheduled_for THEN
    RAISE EXCEPTION 'DELETION_GRACE_NOT_ELAPSED: anonimizacao so apos % (janela de arrependimento)', v_req.scheduled_for USING ERRCODE = 'NN008';
  END IF;
  PERFORM 1 FROM nina.app_user WHERE id = v_req.user_id AND status <> 'DELETED' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'usuario ja excluido' USING ERRCODE = 'NN015'; END IF;
  IF nina.is_active_baby_owner(v_req.user_id) THEN
    RAISE EXCEPTION 'OWNER_MUST_USE_ACCOUNT_DELETION' USING ERRCODE = 'NN013';
  END IF;
  v_scrub := nina.scrub_user_personal_data(v_req.user_id, p_actor);
  UPDATE nina.privacy_request SET status = 'COMPLETED', completed_at = now(), closed_reason = 'FULFILLED' WHERE id = p_request;
  UPDATE nina.privacy_request SET status = 'CANCELLED', cancelled_at = now(), closed_reason = 'USER_ERASED'
   WHERE user_id = v_req.user_id AND status IN ('REQUESTED', 'SCHEDULED', 'IN_PROGRESS');   -- demais pedidos abertos perdem o objeto
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (p_actor, CASE WHEN p_actor IS NULL THEN 'SYSTEM' ELSE 'ADMIN' END, 'privacy.erasure_completed', 'USER', v_req.user_id,
          jsonb_build_object('request_id', p_request, 'on_time', now() <= v_req.due_at) || v_scrub, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('USER', v_req.user_id, 'PersonalDataErased');
END $$;

-- Restore de backup (SEC-064/SR-011): reaplica as exclusoes que o backup ressuscitou. Idempotente; so o worker executa.
CREATE FUNCTION nina.reapply_erasure_ledger() RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE r record; n_baby integer := 0; n_user integer := 0;
BEGIN
  FOR r IN SELECT * FROM nina.erasure_ledger ORDER BY id LOOP
    IF r.entity_type = 'BABY' THEN
      IF EXISTS (SELECT 1 FROM nina.baby WHERE id = r.entity_id AND deleted_at IS NULL) THEN
        PERFORM nina.erase_baby(r.entity_id, NULL); n_baby := n_baby + 1;
      END IF;
    ELSIF EXISTS (SELECT 1 FROM nina.app_user WHERE id = r.entity_id AND status <> 'DELETED') THEN
      PERFORM nina.scrub_user_personal_data(r.entity_id, NULL); n_user := n_user + 1;
    END IF;
  END LOOP;
  IF n_baby + n_user > 0 THEN
    INSERT INTO nina.audit_event (actor_type, action, entity_type, metadata_safe, is_critical)
    VALUES ('SYSTEM', 'backup.erasure_reapplied', 'SYSTEM', jsonb_build_object('babies', n_baby, 'users', n_user), true);
  END IF;
  RETURN jsonb_build_object('babies', n_baby, 'users', n_user);
END $$;

-- Job de limpeza de sync (tombstones 90 dias). Idempotente, em lotes, com lock
-- consultivo; agendar diariamente no worker (nao depende de pg_cron).
CREATE FUNCTION nina.purge_expired_sync_data(p_batch integer DEFAULT 5000)
RETURNS TABLE (change_log_deleted bigint, tombstones_deleted bigint, mutations_deleted bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  v_log_days integer := nina.param_int('sync.changelog_retention_days', 90);
  v_cl bigint := 0; v_ts bigint := 0; v_mu bigint := 0; r record;
BEGIN
  PERFORM nina.take_purge_lock('purge_sync');         -- NR-04: ocupado = ERRO explicito (nunca "0 linhas" silencioso)

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
      WHEN 'SLEEP_SESSION'              THEN DELETE FROM nina.sleep_session WHERE baby_id = r.baby_id AND id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'FEEDING_SESSION'            THEN DELETE FROM nina.feeding_session WHERE baby_id = r.baby_id AND id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'PUMPING_SESSION'            THEN DELETE FROM nina.pumping_session WHERE baby_id = r.baby_id AND id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'DIAPER_EVENT'               THEN DELETE FROM nina.diaper_event WHERE baby_id = r.baby_id AND id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'WAKE_EVENT'                 THEN DELETE FROM nina.wake_event WHERE baby_id = r.baby_id AND id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'SLEEP_SCHEDULE_PREFERENCE'  THEN DELETE FROM nina.sleep_schedule_preference WHERE baby_id = r.baby_id AND id = r.entity_id AND deleted_at IS NOT NULL;
      WHEN 'BABY'                       THEN DELETE FROM nina.baby WHERE id = r.entity_id AND deleted_at IS NOT NULL;  -- cascata remove membros e head
      ELSE NULL;
    END CASE;
    DELETE FROM nina.tombstone WHERE baby_id = r.baby_id AND entity_type = r.entity_type AND entity_id = r.entity_id;
    v_ts := v_ts + 1;
  END LOOP;

  -- 3) idempotencia de push alem da janela de cursor nao tem mais utilidade
  DELETE FROM nina.sync_mutation WHERE ctid IN (
    SELECT ctid FROM nina.sync_mutation
     WHERE received_at < now() - make_interval(days => v_log_days) LIMIT p_batch);
  GET DIAGNOSTICS v_mu = ROW_COUNT;

  RETURN QUERY SELECT v_cl, v_ts, v_mu;
END $$;

-- Retencao operacional (privacy-spec secao 4). Prazos em app_parameter.
CREATE FUNCTION nina.purge_expired_operational_data(p_batch integer DEFAULT 5000) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v jsonb := '{}'::jsonb; n bigint;
BEGIN
  PERFORM nina.take_purge_lock('purge_operational');  -- NR-04

  DELETE FROM nina.auth_session WHERE id IN (SELECT id FROM nina.auth_session
     WHERE absolute_expires_at < now() - interval '30 days' OR revoked_at < now() - interval '30 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('auth_session', n);

  DELETE FROM nina.recovery_request WHERE id IN (SELECT id FROM nina.recovery_request
     WHERE expires_at < now() - interval '7 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('recovery_request', n);

  DELETE FROM nina.email_verification_code WHERE id IN (SELECT id FROM nina.email_verification_code
     WHERE expires_at < now() - interval '7 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('email_verification_code', n);

  DELETE FROM nina.email_change_request WHERE id IN (SELECT id FROM nina.email_change_request
     WHERE expires_at < now() - interval '7 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('email_change_request', n);

  DELETE FROM nina.reauth_jti WHERE jti_hash IN (SELECT jti_hash FROM nina.reauth_jti
     WHERE expires_at < now() - interval '1 day' AND bound_entity_id IS NULL LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('reauth_jti', n);

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

  DELETE FROM nina.data_export_request WHERE id IN (SELECT id FROM nina.data_export_request
     WHERE status IN ('EXPIRED', 'FAILED') AND coalesce(expires_at, requested_at) < now() - interval '90 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('exports_deleted', n);

  DELETE FROM nina.outbox_message WHERE id IN (SELECT id FROM nina.outbox_message
     WHERE processed_at < now() - interval '7 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('outbox', n);

  DELETE FROM nina.notification_job WHERE id IN (SELECT id FROM nina.notification_job
     WHERE status IN ('SENT', 'CANCELLED', 'SKIPPED_QUIET_HOURS', 'DEAD_LETTER') AND updated_at < now() - interval '30 days' LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('notification_job', n);

  DELETE FROM nina.erasure_ledger WHERE id IN (SELECT id FROM nina.erasure_ledger
     WHERE erased_at < now() - make_interval(days => greatest(nina.param_int('erasure_ledger.retention_days', 90), 35)) LIMIT p_batch);
  GET DIAGNOSTICS n = ROW_COUNT; v := v || jsonb_build_object('erasure_ledger', n);
  RETURN v;
END $$;

-- Purga da AUDITORIA (SR-005). Unico caminho de DELETE em audit_event: piso de idade FIXO (nao rebaixavel por parametro) —
-- 12 meses para eventos comuns e 60 meses para os encadeados (criticos/negados/de sistema), lote limitado, o proprio ato
-- e auditado ANTES (contagem e faixa de id) e a cadeia ganha um checkpoint (o hash do ultimo removido) para continuar verificavel.
CREATE FUNCTION nina.purge_audit(p_older_than interval, p_batch integer DEFAULT 5000) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  v_floor interval := make_interval(months => greatest(nina.param_int('audit.retention_months', 12), 12));
  v_floor_chain interval := make_interval(months => greatest(nina.param_int('audit.critical_retention_months', 60), 60));
  v_cut timestamptz; v_cut_chain timestamptz; v_ids bigint[]; v_n bigint := 0; v_min bigint; v_max bigint;
  v_seq bigint; v_hash bytea;
BEGIN
  IF p_older_than IS NULL OR p_older_than < v_floor THEN
    RAISE EXCEPTION 'PURGE_BELOW_FLOOR: a auditoria so pode ser purgada apos %', v_floor USING ERRCODE = 'NN031';
  END IF;
  IF p_batch IS NULL OR p_batch < 1 OR p_batch > 100000 THEN
    RAISE EXCEPTION 'lote invalido' USING ERRCODE = 'NN031';
  END IF;
  PERFORM nina.take_purge_lock('purge_audit');
  v_cut := now() - p_older_than;
  v_cut_chain := now() - greatest(p_older_than, v_floor_chain);
  -- eventos encadeados saem como PREFIXO da cadeia (ordem de chain_seq), para que a verificacao recomece do checkpoint
  SELECT array_agg(id) INTO v_ids FROM (
    (SELECT id FROM nina.audit_event
      WHERE chain_seq IS NOT NULL AND occurred_at < v_cut_chain
        AND chain_seq < coalesce((SELECT min(c.chain_seq) FROM nina.audit_event c
                                   WHERE c.chain_seq IS NOT NULL AND c.occurred_at >= v_cut_chain), 9223372036854775807)
      ORDER BY chain_seq LIMIT p_batch)
    UNION ALL
    (SELECT id FROM nina.audit_event WHERE chain_seq IS NULL AND occurred_at < v_cut ORDER BY id LIMIT p_batch)) x;
  IF v_ids IS NULL THEN RETURN 0; END IF;
  SELECT count(*), min(id), max(id) INTO v_n, v_min, v_max FROM nina.audit_event WHERE id = ANY (v_ids);
  SELECT chain_seq, row_hash INTO v_seq, v_hash FROM nina.audit_event
   WHERE id = ANY (v_ids) AND chain_seq IS NOT NULL ORDER BY chain_seq DESC LIMIT 1;
  INSERT INTO nina.audit_event (actor_type, action, entity_type, metadata_safe, is_critical)
  VALUES ('SYSTEM', 'audit.purged', 'SYSTEM',
          jsonb_build_object('deleted', v_n, 'min_id', v_min, 'max_id', v_max, 'older_than_days', (extract(epoch FROM p_older_than) / 86400)::integer), true);
  IF v_seq IS NOT NULL THEN
    INSERT INTO nina.audit_chain_checkpoint (purged_through_seq, row_hash) VALUES (v_seq, v_hash);
  END IF;
  PERFORM nina.guard_arm('purge_audit');
  DELETE FROM nina.audit_event WHERE id = ANY (v_ids);
  PERFORM nina.guard_disarm('purge_audit');
  RETURN v_n;
END $$;

-- Purga de consentimento: SO registros ja substituidos (nao e o estado vigente) e mais antigos que o prazo probatorio
-- (piso fixo de 60 meses, DJ-06). O estado corrente de cada usuario/finalidade nunca sai.
CREATE FUNCTION nina.purge_consent(p_older_than interval, p_batch integer DEFAULT 5000) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  v_floor interval := make_interval(months => greatest(nina.param_int('consent.retention_months', 60), 60));
  v_n bigint := 0; v_ids uuid[];
BEGIN
  IF p_older_than IS NULL OR p_older_than < v_floor THEN
    RAISE EXCEPTION 'PURGE_BELOW_FLOOR: o consentimento so pode ser purgado apos %', v_floor USING ERRCODE = 'NN031';
  END IF;
  IF p_batch IS NULL OR p_batch < 1 OR p_batch > 100000 THEN RAISE EXCEPTION 'lote invalido' USING ERRCODE = 'NN031'; END IF;
  PERFORM nina.take_purge_lock('purge_consent');
  SELECT array_agg(consent_id) INTO v_ids FROM (
    SELECT c.consent_id FROM nina.consent_record c
     WHERE c.recorded_at < now() - p_older_than
       AND EXISTS (SELECT 1 FROM nina.consent_record n
                    WHERE n.user_id = c.user_id AND n.purpose_key = c.purpose_key
                      AND n.subject_baby_id IS NOT DISTINCT FROM c.subject_baby_id AND n.seq > c.seq)
     ORDER BY c.seq LIMIT p_batch) x;
  IF v_ids IS NULL THEN RETURN 0; END IF;
  v_n := cardinality(v_ids);
  INSERT INTO nina.audit_event (actor_type, action, entity_type, metadata_safe, is_critical)
  VALUES ('SYSTEM', 'consent.purged', 'SYSTEM', jsonb_build_object('deleted', v_n), true);
  PERFORM nina.guard_arm('purge_consent');
  DELETE FROM nina.consent_record WHERE consent_id = ANY (v_ids);
  PERFORM nina.guard_disarm('purge_consent');
  RETURN v_n;
END $$;

-- -----------------------------------------------------------------------------
-- 10. Vinculos: convite, aceite, saida, remocao, papel e propriedade (SR-002, SR-004, SR-009, SR-010)
--     O app NAO tem UPDATE/DELETE em caregiver_membership: toda mudanca de estado passa por estas funcoes
--     (SECURITY DEFINER, search_path fixo, EXECUTE so para nina_app), que emitem as fichas lidas por membership_guard.
-- -----------------------------------------------------------------------------

-- Reautenticacao comprovada (SR-013/SR-016/NR-03): o jti foi EMITIDO pela API (reauth_issue, MAC) e consumido pelo Identity
-- (consume_reauth_jti); aqui se verifica que existe, e do usuario do contexto, foi consumido NESTE escopo, recentemente, e AINDA nao
-- foi vinculado, e o vincula ao recurso (uso unico real). Um jti inventado por SQL nao existe no livro-razao.
CREATE FUNCTION nina.reauth_bind(p_jti_hash bytea, p_scope text, p_entity_type text, p_entity_id uuid,
                                 p_max_age interval DEFAULT interval '10 minutes') RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id();
BEGIN
  IF v_user IS NULL OR p_jti_hash IS NULL THEN RETURN false; END IF;
  UPDATE nina.reauth_jti SET bound_entity_type = p_entity_type, bound_entity_id = p_entity_id
   WHERE jti_hash = p_jti_hash AND user_id = v_user AND consumed_scope = p_scope AND bound_entity_id IS NULL
     AND consumed_at IS NOT NULL AND consumed_at >= now() - p_max_age;
  RETURN FOUND;
END $$;

-- Aceita o convite (token por hash). Devolve um CODIGO em vez de lancar excecao para que o contador de tentativas falhas
-- persista (uma excecao desfaria o UPDATE). Convite de outro destinatario responde NOT_FOUND (nao revela existencia, SEC-051).
CREATE FUNCTION nina.accept_invitation(p_token_hash bytea, p_policy_version text, p_text_hash text, p_locale text,
                                       p_platform text DEFAULT 'SERVER', p_app_version text DEFAULT NULL)
RETURNS TABLE (result text, baby_id uuid, membership_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE
  v_user uuid := nina.current_user_id(); u nina.app_user%ROWTYPE; m nina.caregiver_membership%ROWTYPE;
  v_max integer := nina.param_int('invites.max_failed_attempts', 5); v_ver text;
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'aceite exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  SELECT * INTO u FROM nina.app_user WHERE id = v_user AND status = 'ACTIVE';
  IF NOT FOUND OR u.email_verified_at IS NULL THEN                       -- SEC-016: e-mail verificado
    RETURN QUERY SELECT 'EMAIL_NOT_VERIFIED'::text, NULL::uuid, NULL::uuid; RETURN;
  END IF;
  SELECT * INTO m FROM nina.caregiver_membership c WHERE c.invite_token_hash = p_token_hash AND c.status = 'PENDING' FOR UPDATE;
  IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM nina.baby b WHERE b.id = m.baby_id AND b.deleted_at IS NULL) THEN
    RETURN QUERY SELECT 'NOT_FOUND'::text, NULL::uuid, NULL::uuid; RETURN;
  END IF;
  IF m.invite_expires_at IS NULL OR m.invite_expires_at <= now() THEN
    RETURN QUERY SELECT 'EXPIRED'::text, NULL::uuid, NULL::uuid; RETURN;
  END IF;
  IF (m.user_id IS NOT NULL AND m.user_id <> v_user) OR (m.invited_email IS NOT NULL AND m.invited_email <> u.email_normalized) THEN
    PERFORM nina.guard_arm('membership');
    UPDATE nina.caregiver_membership c SET invite_failed_attempts = c.invite_failed_attempts + 1,
           invite_expires_at = CASE WHEN c.invite_failed_attempts + 1 >= v_max THEN now() ELSE c.invite_expires_at END
     WHERE c.id = m.id;
    PERFORM nina.guard_disarm('membership');
    RETURN QUERY SELECT 'NOT_FOUND'::text, NULL::uuid, NULL::uuid; RETURN;
  END IF;
  IF EXISTS (SELECT 1 FROM nina.caregiver_membership c
              WHERE c.baby_id = m.baby_id AND c.user_id = v_user AND c.status IN ('PENDING', 'ACTIVE') AND c.id <> m.id) THEN
    RETURN QUERY SELECT 'ALREADY_MEMBER'::text, NULL::uuid, NULL::uuid; RETURN;
  END IF;
  SELECT cp.current_version INTO v_ver FROM nina.consent_purpose cp WHERE cp.purpose_key = 'caregiver_data_ack';
  IF v_ver IS NULL OR p_policy_version IS DISTINCT FROM v_ver OR p_text_hash IS NULL OR p_text_hash !~ '^[0-9a-f]{64}$' OR p_locale IS NULL THEN
    RETURN QUERY SELECT 'CONSENT_REQUIRED'::text, NULL::uuid, NULL::uuid; RETURN;
  END IF;
  PERFORM nina.guard_arm('membership');
  UPDATE nina.caregiver_membership c
     SET user_id = v_user, status = 'ACTIVE', accepted_at = now(), invite_used_at = now(), invite_token_hash = NULL, invited_email = NULL
   WHERE c.id = m.id;
  PERFORM nina.guard_disarm('membership');
  INSERT INTO nina.consent_record (user_id, subject_baby_id, purpose_key, policy_version, text_hash, locale, status, source, app_version, platform)
  VALUES (v_user, m.baby_id, 'caregiver_data_ack', v_ver, p_text_hash, p_locale, 'GRANTED', 'INVITE_ACCEPT', p_app_version,
          CASE WHEN p_platform IN ('IOS', 'ANDROID', 'WEB', 'SERVER') THEN p_platform ELSE 'SERVER' END);
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
  VALUES (v_user, 'baby.invitation_accepted', 'MEMBERSHIP', m.id, m.baby_id, jsonb_build_object('role', m.role), true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type)
  VALUES ('BABY', m.baby_id, 'CaregiverJoined');
  RETURN QUERY SELECT 'ACCEPTED'::text, m.baby_id, m.id;
END $$;

CREATE FUNCTION nina.decline_invitation(p_token_hash bytea) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); u nina.app_user%ROWTYPE; m nina.caregiver_membership%ROWTYPE;
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'recusa exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  SELECT * INTO u FROM nina.app_user WHERE id = v_user AND status = 'ACTIVE';
  SELECT * INTO m FROM nina.caregiver_membership c WHERE c.invite_token_hash = p_token_hash AND c.status = 'PENDING' FOR UPDATE;
  IF NOT FOUND OR u.id IS NULL
     OR (m.user_id IS NOT NULL AND m.user_id <> v_user) OR (m.invited_email IS NOT NULL AND m.invited_email <> u.email_normalized) THEN
    RETURN 'NOT_FOUND';
  END IF;
  PERFORM nina.guard_arm('membership');
  UPDATE nina.caregiver_membership c
     SET status = 'DECLINED', invite_used_at = now(), invite_token_hash = NULL, invited_email = NULL, user_id = coalesce(c.user_id, v_user)
   WHERE c.id = m.id;
  PERFORM nina.guard_disarm('membership');
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, metadata_safe)
  VALUES (v_user, 'baby.invitation_declined', 'MEMBERSHIP', m.id, m.baby_id, '{}'::jsonb);
  RETURN 'DECLINED';
END $$;

-- Sair do bebe (membro nao-Owner). O Owner precisa transferir a propriedade antes (NN058).
CREATE FUNCTION nina.leave_baby(p_baby uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); m nina.caregiver_membership%ROWTYPE;
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'saida exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  SELECT * INTO m FROM nina.caregiver_membership c WHERE c.baby_id = p_baby AND c.user_id = v_user AND c.status = 'ACTIVE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'sem vinculo ativo com o bebe' USING ERRCODE = 'NN015'; END IF;
  IF m.role = 'OWNER' THEN
    RAISE EXCEPTION 'OWNER_MUST_TRANSFER: o Owner transfere a propriedade antes de sair' USING ERRCODE = 'NN058';
  END IF;
  PERFORM nina.guard_arm('membership');
  UPDATE nina.caregiver_membership c SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'LEFT' WHERE c.id = m.id;
  PERFORM nina.guard_disarm('membership');
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
  VALUES (v_user, 'baby.member_left', 'MEMBERSHIP', m.id, p_baby, '{}'::jsonb, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type) VALUES ('BABY', p_baby, 'CaregiverLeft');
END $$;

-- Owner remove um cuidador ativo OU revoga um convite pendente. Nunca o proprio Owner (SEC-007).
CREATE FUNCTION nina.remove_member(p_membership uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); m nina.caregiver_membership%ROWTYPE;
BEGIN
  SELECT * INTO m FROM nina.caregiver_membership c WHERE c.id = p_membership FOR UPDATE;
  IF NOT FOUND OR nina.baby_role(m.baby_id) IS DISTINCT FROM 'OWNER' THEN
    RAISE EXCEPTION 'vinculo inexistente ou sem permissao' USING ERRCODE = 'NN059';
  END IF;
  IF m.status NOT IN ('PENDING', 'ACTIVE') OR m.role = 'OWNER' OR m.user_id IS NOT DISTINCT FROM v_user THEN
    RAISE EXCEPTION 'vinculo nao removivel (Owner, proprio ou ja encerrado)' USING ERRCODE = 'NN051';
  END IF;
  PERFORM nina.guard_arm('membership');
  UPDATE nina.caregiver_membership c SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'OWNER_REMOVED',
         invite_token_hash = NULL, invited_email = NULL WHERE c.id = m.id;
  PERFORM nina.guard_disarm('membership');
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
  VALUES (v_user, 'baby.member_removed', 'MEMBERSHIP', m.id, m.baby_id, jsonb_build_object('was', m.status), true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type) VALUES ('BABY', m.baby_id, 'CaregiverRemoved');
END $$;

CREATE FUNCTION nina.set_member_role(p_membership uuid, p_role text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); m nina.caregiver_membership%ROWTYPE;
BEGIN
  IF p_role IS NULL OR p_role NOT IN ('CAREGIVER', 'READ_ONLY') THEN
    RAISE EXCEPTION 'papel invalido (OWNER so por transferencia)' USING ERRCODE = 'NN051';
  END IF;
  SELECT * INTO m FROM nina.caregiver_membership c WHERE c.id = p_membership FOR UPDATE;
  IF NOT FOUND OR nina.baby_role(m.baby_id) IS DISTINCT FROM 'OWNER' THEN
    RAISE EXCEPTION 'vinculo inexistente ou sem permissao' USING ERRCODE = 'NN059';
  END IF;
  IF m.status <> 'ACTIVE' OR m.role = 'OWNER' OR m.user_id IS NOT DISTINCT FROM v_user THEN
    RAISE EXCEPTION 'papel nao alteravel (Owner, proprio ou vinculo inativo)' USING ERRCODE = 'NN051';
  END IF;
  PERFORM nina.guard_arm('membership');
  UPDATE nina.caregiver_membership c SET role = p_role WHERE c.id = m.id;
  PERFORM nina.guard_disarm('membership');
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
  VALUES (v_user, 'baby.member_role_changed', 'MEMBERSHIP', m.id, m.baby_id, jsonb_build_object('role', p_role), true);
END $$;

-- Transferencia de propriedade (SR-004/SR-010): atomica; o Owner anterior vira CAREGIVER (contrato), o destino (vinculo ACTIVE
-- do mesmo bebe) vira OWNER, e baby.family_id acompanha a titularidade (o ex-Owner deixa de ler o perfil, SR-010).
-- Exige reautenticacao comprovada no livro-razao (escopo OWNERSHIP_TRANSFER), vinculada a este bebe.
CREATE FUNCTION nina.transfer_ownership(p_baby uuid, p_new_owner_membership uuid, p_reauth_jti_hash bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_old nina.caregiver_membership%ROWTYPE; v_new nina.caregiver_membership%ROWTYPE; v_family uuid;
BEGIN
  SELECT * INTO v_old FROM nina.caregiver_membership c
   WHERE c.baby_id = p_baby AND c.user_id = v_user AND c.role = 'OWNER' AND c.status = 'ACTIVE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'somente o Owner ativo transfere a propriedade' USING ERRCODE = 'NN059'; END IF;
  SELECT * INTO v_new FROM nina.caregiver_membership c
   WHERE c.id = p_new_owner_membership AND c.baby_id = p_baby AND c.status = 'ACTIVE' AND c.role <> 'OWNER' AND c.user_id <> v_user FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'destino invalido (precisa de vinculo ativo, nao-Owner, do mesmo bebe)' USING ERRCODE = 'NN051'; END IF;
  IF NOT nina.reauth_bind(p_reauth_jti_hash, 'OWNERSHIP_TRANSFER', 'BABY', p_baby) THEN
    RAISE EXCEPTION 'REAUTH_REQUIRED: transferencia exige reautenticacao comprovada' USING ERRCODE = 'NN014';
  END IF;
  PERFORM nina.guard_arm('membership'); PERFORM nina.guard_arm('ownership'); PERFORM nina.guard_arm('baby_family');
  UPDATE nina.caregiver_membership c SET role = 'CAREGIVER' WHERE c.id = v_old.id;       -- primeiro rebaixa (indice de Owner unico)
  UPDATE nina.caregiver_membership c SET role = 'OWNER' WHERE c.id = v_new.id;
  SELECT f.id INTO v_family FROM nina.family f WHERE f.owner_user_id = v_new.user_id;
  IF v_family IS NULL THEN INSERT INTO nina.family (owner_user_id) VALUES (v_new.user_id) RETURNING id INTO v_family; END IF;
  UPDATE nina.baby b SET family_id = v_family, last_modified_by = v_user WHERE b.id = p_baby;
  PERFORM nina.guard_disarm('membership'); PERFORM nina.guard_disarm('ownership'); PERFORM nina.guard_disarm('baby_family');
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, baby_id, metadata_safe, is_critical)
  VALUES (v_user, 'baby.ownership_transferred', 'BABY', p_baby, p_baby, jsonb_build_object('reason', 'owner_request'), true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type) VALUES ('BABY', p_baby, 'BabyOwnershipTransferred');
END $$;

-- Referencias de usuario dos membros de um bebe legivel (id + nome exibido): unico modo de o app ver o display_name
-- de outro usuario (autoria `created_by {id, display_name}` e lista de cuidadores) sem abrir app_user.
CREATE FUNCTION nina.baby_member_refs(p_baby uuid)
RETURNS TABLE (membership_id uuid, user_id uuid, display_name text, role text, status text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT m.id, m.user_id, u.display_name, m.role, m.status
    FROM nina.caregiver_membership m JOIN nina.app_user u ON u.id = m.user_id
   WHERE m.baby_id = p_baby AND m.status = 'ACTIVE' AND nina.can_read_baby(p_baby) $$;

-- -----------------------------------------------------------------------------
-- 11. Conta e identidade: fluxos PRE-AUTENTICACAO e funcoes estreitas (SR-003)
--     app_user/user_credential/user_identity/refresh_token/recovery_request tem RLS por nina.user_id. Os fluxos que rodam
--     ANTES de haver usuario (login, cadastro, recuperacao, login social) consultam por CHAVE DE BUSCA (e-mail, hash do token,
--     subject do provedor) por funcoes que devolvem uma unica linha, sem listar nem expor linhas alheias.
--     Residual aceito: quem executa SQL arbitrario como nina_app ainda obtem a linha de quem tiver o e-mail/hash conhecido
--     (o hash de senha e verificado pela API, que precisa dele). A defesa e: sem enumeracao, sem escrita cruzada, RLS no resto.
-- -----------------------------------------------------------------------------
CREATE FUNCTION nina.auth_lookup_user_by_email(p_email text)
RETURNS TABLE (id uuid, email text, email_verified_at timestamptz, locale text, timezone text, status text,
               created_at timestamptz, password_hash text, display_name text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT u.id, u.email, u.email_verified_at, u.locale, u.timezone::text, u.status, u.created_at, c.password_hash, u.display_name
    FROM nina.app_user u LEFT JOIN nina.user_credential c ON c.user_id = u.id
   WHERE u.email_normalized = lower(btrim(p_email)) $$;

CREATE FUNCTION nina.auth_lookup_identity(p_provider text, p_subject text)
RETURNS TABLE (provider text, provider_subject text, linked_at timestamptz, user_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT i.provider, i.provider_subject, i.linked_at, i.user_id FROM nina.user_identity i
   WHERE i.provider = p_provider AND i.provider_subject = p_subject $$;

-- Consome (uso unico) o token de recuperacao/verificacao pelo HASH; devolve o usuario dono ou NULL.
-- NR-13: `p_now` vem do chamador (a API usa relogio injetavel nos testes), mas nunca pode RETROCEDER mais de 2 min em relacao ao
-- banco: quem tem SQL e o hash nao "estica" a validade de um segredo expirado. Avancar so encurta a validade.
CREATE FUNCTION nina.clamp_now(p_now timestamptz) RETURNS timestamptz
LANGUAGE sql STABLE AS $$ SELECT greatest(coalesce(p_now, now()), now() - interval '2 minutes') $$;

CREATE FUNCTION nina.auth_consume_recovery(p_hash bytea, p_now timestamptz) RETURNS uuid
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  UPDATE nina.recovery_request SET used_at = nina.clamp_now(p_now)
   WHERE token_hash = p_hash AND used_at IS NULL AND expires_at > nina.clamp_now(p_now)
  RETURNING user_id $$;

-- Emissao do token de reautenticacao (NR-03). So a API chama com um MAC valido; o jti passa a existir no livro-razao com usuario,
-- sessao, escopos e validade. Mensagem canonica do MAC (proposito 'reauth.issue'):
--   usuario|sessao|hex(jti_hash)|escopos ordenados (ordinal) separados por virgula|emissao (epoch s)|expiracao (epoch s)
CREATE FUNCTION nina.reauth_issue(p_jti_hash bytea, p_session uuid, p_scopes text[], p_issued timestamptz, p_expires timestamptz, p_mac bytea)
RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_scopes text[] := coalesce(p_scopes, ARRAY[]::text[]); v_sorted text;
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'reautenticacao exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  SELECT coalesce(string_agg(x, ',' ORDER BY x COLLATE "C"), '') INTO v_sorted FROM unnest(v_scopes) x;
  IF p_jti_hash IS NULL OR p_session IS NULL OR p_issued IS NULL OR p_expires IS NULL
     OR NOT nina.mac_ok('reauth.issue',
          v_user::text || '|' || p_session::text || '|' || encode(p_jti_hash, 'hex') || '|' || v_sorted || '|'
          || floor(extract(epoch FROM p_issued))::bigint::text || '|' || floor(extract(epoch FROM p_expires))::bigint::text, p_mac) THEN
    RAISE EXCEPTION 'SERVER_SIGNATURE_INVALID: reautenticacao so e emitida pelo servidor' USING ERRCODE = 'NN071';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM nina.auth_session s WHERE s.id = p_session AND s.user_id = v_user AND s.revoked_at IS NULL) THEN
    RAISE EXCEPTION 'SERVER_SIGNATURE_INVALID: sessao inexistente, de outro usuario ou revogada' USING ERRCODE = 'NN071';
  END IF;
  INSERT INTO nina.reauth_jti (jti_hash, user_id, session_id, scopes, issued_at, expires_at)
  VALUES (p_jti_hash, v_user, p_session, v_scopes, p_issued, p_expires);       -- jti repetido: unique_violation (23505)
END $$;

-- Consome (uso unico) um jti EMITIDO: do usuario e da sessao do contexto, ainda nao consumido, valido e dentro do escopo (escopos
-- vazios = transicao v1.x, qualquer operacao sensivel, uma vez). false para jti inexistente/forjado, reapresentado, expirado, de
-- outra sessao ou de outro escopo (a reapresentacao conta em attempts).
CREATE FUNCTION nina.consume_reauth_jti(p_jti_hash bytea, p_scope text, p_session uuid) RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id();
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'reautenticacao exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  IF p_scope IS NULL OR p_scope NOT IN ('ACCOUNT_PASSWORD_CHANGE', 'ACCOUNT_EMAIL_CHANGE', 'IDENTITY_LINK', 'IDENTITY_UNLINK',
       'DATA_EXPORT_REQUEST', 'DATA_EXPORT_DOWNLOAD', 'ACCOUNT_DELETE', 'PRIVACY_REQUEST', 'BABY_DELETE', 'OWNERSHIP_TRANSFER') THEN
    RETURN false;
  END IF;
  UPDATE nina.reauth_jti SET consumed_at = now(), consumed_scope = p_scope, attempts = least(attempts + 1, 32767)
   WHERE jti_hash = p_jti_hash AND user_id = v_user AND session_id = p_session AND consumed_at IS NULL
     AND (cardinality(scopes) = 0 OR p_scope = ANY (scopes)) AND expires_at > now() - interval '1 minute';
  IF FOUND THEN RETURN true; END IF;
  UPDATE nina.reauth_jti SET attempts = least(attempts + 1, 32767) WHERE jti_hash = p_jti_hash AND user_id = v_user;
  RETURN false;
END $$;

-- Token de push (SR-021 O4): idempotente por (usuario, dispositivo, plataforma). Se o MESMO token (plataforma+token) ja
-- pertence a outro usuario cuja sessao naquele dispositivo nao esta mais ativa (aparelho trocado de mao), o vinculo antigo e
-- removido e o token passa ao usuario atual; se o dono anterior ainda tem sessao ativa nele, e conflito (23505) sem revelar quem.
CREATE FUNCTION nina.register_push_token(p_device uuid, p_platform text, p_token text, p_environment text, p_locale text,
                                         p_app_version text, p_os_authorized boolean, p_now timestamptz)
RETURNS TABLE (created_at timestamptz, last_seen_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); o nina.device_push_token%ROWTYPE;
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'registro de push exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  IF NOT EXISTS (SELECT 1 FROM nina.auth_session s WHERE s.user_id = v_user AND s.device_id = p_device
                  AND s.revoked_at IS NULL AND s.absolute_expires_at > p_now) THEN
    RAISE EXCEPTION 'dispositivo sem sessao ativa do usuario' USING ERRCODE = 'NN015';
  END IF;
  SELECT * INTO o FROM nina.device_push_token t
   WHERE t.platform = p_platform AND t.token = p_token AND NOT (t.user_id = v_user AND t.device_id = p_device) FOR UPDATE;
  IF FOUND THEN
    IF EXISTS (SELECT 1 FROM nina.auth_session s WHERE s.user_id = o.user_id AND s.device_id = o.device_id
                AND s.revoked_at IS NULL AND s.absolute_expires_at > p_now) THEN
      RAISE EXCEPTION 'PUSH_TOKEN_CONFLICT' USING ERRCODE = 'unique_violation';
    END IF;
    DELETE FROM nina.device_push_token t WHERE t.id = o.id;
  END IF;
  RETURN QUERY
  WITH up AS (
    INSERT INTO nina.device_push_token AS t (user_id, device_id, platform, token, environment, locale, app_version,
                                             os_notifications_authorized, created_at, last_seen_at)
    VALUES (v_user, p_device, p_platform, p_token, p_environment, p_locale, p_app_version, p_os_authorized, p_now, p_now)
    ON CONFLICT (user_id, device_id, platform) DO UPDATE
       SET token = EXCLUDED.token, environment = EXCLUDED.environment, locale = EXCLUDED.locale,
           app_version = EXCLUDED.app_version, os_notifications_authorized = EXCLUDED.os_notifications_authorized,
           last_seen_at = EXCLUDED.last_seen_at
    RETURNING t.created_at AS c_at, t.last_seen_at AS s_at)
  SELECT up.c_at, up.s_at FROM up;
END $$;

-- Codigo de verificacao do e-mail atual (BE-001/NR-09): emite (invalida o anterior) e confere com contador de tentativas.
-- NR-09: o app NAO escreve email_verified_at (nem INSERT nem UPDATE): so email_code_verify (e register_social_user, atestado pelo
-- provedor) o definem. Para nao bastar "emitir um codigo que eu mesmo conheco e verifica-lo", a EMISSAO exige o MAC do servidor
-- (proposito 'email.code', mensagem usuario|hex(code_hash)|expiracao epoch s): so a API, que gera o codigo e o envia ao e-mail, assina.
-- Devolve NULL (sem emitir) se a conta ja esta verificada/inativa ou se o ultimo codigo tem menos de p_min_interval (reenvio).
CREATE FUNCTION nina.email_code_issue(p_code_hash bytea, p_now timestamptz, p_expires timestamptz, p_min_interval interval, p_mac bytea) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_id uuid; u nina.app_user%ROWTYPE;
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'codigo exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  IF p_code_hash IS NULL OR p_expires IS NULL OR p_now IS NULL
     OR NOT nina.mac_ok('email.code', v_user::text || '|' || encode(p_code_hash, 'hex') || '|' || floor(extract(epoch FROM p_expires))::bigint::text, p_mac) THEN
    RAISE EXCEPTION 'SERVER_SIGNATURE_INVALID: o codigo de verificacao so e emitido pelo servidor' USING ERRCODE = 'NN071';
  END IF;
  SELECT * INTO u FROM nina.app_user x WHERE x.id = v_user AND x.status = 'ACTIVE' FOR UPDATE;
  IF NOT FOUND OR u.email_verified_at IS NOT NULL THEN RETURN NULL; END IF;
  IF p_min_interval IS NOT NULL AND EXISTS (SELECT 1 FROM nina.email_verification_code e WHERE e.user_id = v_user AND e.created_at > p_now - p_min_interval) THEN
    RETURN NULL;
  END IF;
  UPDATE nina.email_verification_code SET invalidated_at = p_now WHERE user_id = v_user AND used_at IS NULL AND invalidated_at IS NULL;
  INSERT INTO nina.email_verification_code (user_id, code_hash, created_at, expires_at, max_attempts)
  VALUES (v_user, p_code_hash, p_now, p_expires, nina.param_int('auth.email_code_max_attempts', 5)) RETURNING id INTO v_id;
  RETURN v_id;
END $$;

-- Pre-autenticacao, por e-mail. Devolve o usuario quando o codigo confere (e marca o e-mail como verificado); senao NULL,
-- SEM excecao (o contador de tentativas persiste; esgotado, o codigo e invalidado). Resposta uniforme para conta inexistente.
CREATE FUNCTION nina.email_code_verify(p_email text, p_code_hash bytea, p_now timestamptz) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE u nina.app_user%ROWTYPE; c nina.email_verification_code%ROWTYPE; v_now timestamptz := nina.clamp_now(p_now);
BEGIN
  SELECT * INTO u FROM nina.app_user x WHERE x.email_normalized = lower(btrim(p_email)) AND x.status = 'ACTIVE';
  IF NOT FOUND THEN RETURN NULL; END IF;
  SELECT * INTO c FROM nina.email_verification_code e
   WHERE e.user_id = u.id AND e.used_at IS NULL AND e.invalidated_at IS NULL AND e.expires_at > v_now FOR UPDATE;
  IF NOT FOUND THEN RETURN NULL; END IF;
  IF c.code_hash = p_code_hash THEN
    UPDATE nina.email_verification_code SET used_at = v_now WHERE id = c.id;
    UPDATE nina.app_user SET email_verified_at = coalesce(email_verified_at, v_now) WHERE id = u.id;
    RETURN u.id;
  END IF;
  UPDATE nina.email_verification_code SET attempts = attempts + 1,
         invalidated_at = CASE WHEN attempts + 1 >= max_attempts THEN v_now END WHERE id = c.id;
  RETURN NULL;
END $$;

-- Cadastro/login social (NR-09): cria a conta JA verificada (o provedor atestou o e-mail) e a identidade. O app nao grava
-- email_verified_at; o atestado do provedor chega assinado pela API (proposito 'social.register', mensagem
-- usuario|provedor|subject|e-mail normalizado). Exige o contexto = p_id (a RLS das demais tabelas segue valendo).
CREATE FUNCTION nina.register_social_user(p_id uuid, p_email text, p_display_name text, p_locale text, p_timezone text,
                                          p_provider text, p_subject text, p_now timestamptz, p_mac bytea) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_now timestamptz := nina.clamp_now(p_now);
BEGIN
  IF v_user IS NULL OR p_id IS DISTINCT FROM v_user THEN
    RAISE EXCEPTION 'cadastro social exige o proprio usuario no contexto' USING ERRCODE = 'NN015';
  END IF;
  IF p_email IS NULL OR p_provider IS NULL OR p_subject IS NULL
     OR NOT nina.mac_ok('social.register', p_id::text || '|' || p_provider || '|' || p_subject || '|' || lower(btrim(p_email)), p_mac) THEN
    RAISE EXCEPTION 'SERVER_SIGNATURE_INVALID: a verificacao social so e atestada pelo servidor' USING ERRCODE = 'NN071';
  END IF;
  INSERT INTO nina.app_user (id, email, email_verified_at, display_name, locale, timezone, created_at, updated_at)
  VALUES (p_id, p_email, v_now, p_display_name, p_locale, p_timezone, v_now, v_now);
  INSERT INTO nina.user_identity (user_id, provider, provider_subject, email_at_link, linked_at)
  VALUES (p_id, p_provider, p_subject, p_email, v_now);
END $$;

-- Troca de e-mail (RF-050): pendencia + codigo enviado ao NOVO endereco; so a confirmacao altera app_user.email.
-- NR-09: o codigo enviado ao NOVO endereco tambem e assinado pelo servidor (proposito 'email.change', mensagem
-- usuario|hex(code_hash)|novo e-mail normalizado|expiracao epoch s); sem isso quem tem SQL emitiria o proprio codigo e "confirmaria"
-- qualquer endereco de terceiros.
CREATE FUNCTION nina.email_change_create(p_new_email text, p_code_hash bytea, p_now timestamptz, p_expires timestamptz,
                                         p_reauth_jti_hash bytea, p_mac bytea) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_id uuid := gen_random_uuid();
BEGIN
  IF v_user IS NULL OR NOT EXISTS (SELECT 1 FROM nina.app_user WHERE id = v_user AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'troca de e-mail exige usuario ativo no contexto' USING ERRCODE = 'NN015';
  END IF;
  IF p_new_email IS NULL OR p_code_hash IS NULL OR p_expires IS NULL
     OR NOT nina.mac_ok('email.change', v_user::text || '|' || encode(p_code_hash, 'hex') || '|' || lower(btrim(p_new_email)) || '|'
                                        || floor(extract(epoch FROM p_expires))::bigint::text, p_mac) THEN
    RAISE EXCEPTION 'SERVER_SIGNATURE_INVALID: o codigo de troca de e-mail so e emitido pelo servidor' USING ERRCODE = 'NN071';
  END IF;
  IF NOT nina.reauth_bind(p_reauth_jti_hash, 'ACCOUNT_EMAIL_CHANGE', 'EMAIL_CHANGE_REQUEST', v_id) THEN
    RAISE EXCEPTION 'REAUTH_REQUIRED: troca de e-mail exige reautenticacao comprovada' USING ERRCODE = 'NN014';
  END IF;
  UPDATE nina.email_change_request SET invalidated_at = p_now WHERE user_id = v_user AND confirmed_at IS NULL AND invalidated_at IS NULL;
  INSERT INTO nina.email_change_request (id, user_id, new_email, code_hash, created_at, expires_at, max_attempts)
  VALUES (v_id, v_user, btrim(p_new_email), p_code_hash, p_now, p_expires, nina.param_int('auth.email_code_max_attempts', 5));
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe)
  VALUES (v_user, 'auth.email_change_requested', 'user', v_user, '{}'::jsonb);
  RETURN v_id;
END $$;

CREATE FUNCTION nina.email_change_confirm(p_code_hash bytea, p_now timestamptz) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); r nina.email_change_request%ROWTYPE; v_now timestamptz := nina.clamp_now(p_now);
BEGIN
  IF v_user IS NULL THEN RAISE EXCEPTION 'confirmacao exige usuario no contexto' USING ERRCODE = 'NN015'; END IF;
  SELECT * INTO r FROM nina.email_change_request e
   WHERE e.user_id = v_user AND e.confirmed_at IS NULL AND e.invalidated_at IS NULL AND e.expires_at > v_now FOR UPDATE;
  IF NOT FOUND THEN RETURN 'INVALID'; END IF;
  IF r.code_hash <> p_code_hash THEN
    UPDATE nina.email_change_request SET attempts = attempts + 1,
           invalidated_at = CASE WHEN attempts + 1 >= max_attempts THEN v_now END WHERE id = r.id;
    RETURN 'INVALID';
  END IF;
  BEGIN
    UPDATE nina.app_user SET email = r.new_email, email_verified_at = v_now WHERE id = v_user;
  EXCEPTION WHEN unique_violation THEN
    RETURN 'EMAIL_IN_USE';
  END;
  UPDATE nina.email_change_request SET confirmed_at = v_now WHERE id = r.id;
  INSERT INTO nina.audit_event (actor_user_id, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (v_user, 'auth.email_changed', 'user', v_user, '{}'::jsonb, true);
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type) VALUES ('USER', v_user, 'EmailChanged');
  RETURN 'CHANGED';
END $$;

-- Exclusao de conta (ADR-0009/0010, SR-016, NR-03/NR-14): o pedido so nasce por aqui, SEMPRE com reautenticacao ACCOUNT_DELETE
-- comprovada no livro-razao (emitida pela API) e vinculada ao pedido; o app nao escreve confirmed_at. Ao criar o pedido o
-- titular e os demais cuidadores ativos dos bebes que serao apagados sao avisados pelo outbox (o worker envia; sem PII).
CREATE FUNCTION nina.request_account_deletion(p_reauth_jti_hash bytea) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_id uuid := gen_random_uuid();
BEGIN
  IF v_user IS NULL OR NOT nina.is_active_baby_owner(v_user) THEN
    RAISE EXCEPTION 'NOT_BABY_OWNER: somente Owner ativo de um bebe exclui a conta; use privacy_request (ADR-0010)' USING ERRCODE = 'NN012';
  END IF;
  IF NOT nina.reauth_bind(p_reauth_jti_hash, 'ACCOUNT_DELETE', 'ACCOUNT_DELETION_REQUEST', v_id) THEN
    RAISE EXCEPTION 'REAUTH_REQUIRED: exclusao de conta exige reautenticacao comprovada' USING ERRCODE = 'NN014';
  END IF;
  INSERT INTO nina.account_deletion_request (id, user_id, grace_days, scheduled_for, confirmed_at, confirmation_method, reauth_jti_hash)
  VALUES (v_id, v_user, 1, now(), now(), 'REAUTHENTICATION', p_reauth_jti_hash);   -- janela/estado: trigger account_deletion_guard
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload)
  VALUES ('USER', v_user, 'AccountDeletionRequested', jsonb_build_object('request_id', v_id));
  INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload)
  SELECT 'USER', m.user_id, 'SharedBabyDeletionScheduled', jsonb_build_object('baby_id', o.baby_id)
    FROM nina.caregiver_membership o
    JOIN nina.baby b ON b.id = o.baby_id AND b.deleted_at IS NULL
    JOIN nina.caregiver_membership m ON m.baby_id = o.baby_id AND m.status = 'ACTIVE' AND m.user_id <> v_user
   WHERE o.user_id = v_user AND o.role = 'OWNER' AND o.status = 'ACTIVE';
  RETURN v_id;
END $$;

CREATE FUNCTION nina.confirm_account_deletion(p_request uuid, p_reauth_jti_hash bytea) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_user uuid := nina.current_user_id(); v_n integer;
BEGIN
  -- verifica o pedido ANTES de consumir o vinculo do jti (um pedido invalido nao queima a reautenticacao)
  IF v_user IS NULL OR NOT EXISTS (SELECT 1 FROM nina.account_deletion_request r
       WHERE r.id = p_request AND r.user_id = v_user AND r.status = 'SCHEDULED' AND r.confirmed_at IS NULL) THEN
    RAISE EXCEPTION 'pedido inexistente, de outro usuario ou ja confirmado/encerrado' USING ERRCODE = 'NN011';
  END IF;
  IF NOT nina.reauth_bind(p_reauth_jti_hash, 'ACCOUNT_DELETE', 'ACCOUNT_DELETION_REQUEST', p_request) THEN
    RAISE EXCEPTION 'REAUTH_REQUIRED: confirmacao sem reautenticacao comprovada' USING ERRCODE = 'NN014';
  END IF;
  PERFORM nina.guard_arm('deletion_confirm');
  UPDATE nina.account_deletion_request SET confirmed_at = now(), confirmation_method = 'REAUTHENTICATION', reauth_jti_hash = p_reauth_jti_hash
   WHERE id = p_request AND user_id = v_user AND status = 'SCHEDULED' AND confirmed_at IS NULL;
  GET DIAGNOSTICS v_n = ROW_COUNT;
  PERFORM nina.guard_disarm('deletion_confirm');
  IF v_n <> 1 THEN RAISE EXCEPTION 'pedido inexistente, de outro usuario ou ja confirmado/encerrado' USING ERRCODE = 'NN011'; END IF;
END $$;

-- Concessao manual de plano (SR-003.3): ato administrativo, so nina_config_admin; exige ator (nina.user_id) e e auditado.
CREATE FUNCTION nina.grant_manual_entitlement(p_family uuid, p_plan_code text, p_valid_until timestamptz, p_reason text) RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
DECLARE v_actor uuid := nina.current_user_id(); v_plan smallint; v_owner uuid; v_id uuid;
BEGIN
  IF v_actor IS NULL THEN RAISE EXCEPTION 'concessao manual exige ator (nina.user_id)' USING ERRCODE = 'NN040'; END IF;
  IF p_reason IS NULL OR length(btrim(p_reason)) = 0 OR length(p_reason) > 200 THEN
    RAISE EXCEPTION 'motivo obrigatorio (<= 200)' USING ERRCODE = 'NN040';
  END IF;
  SELECT id INTO v_plan FROM nina.plan WHERE code = p_plan_code AND is_active;
  SELECT owner_user_id INTO v_owner FROM nina.family WHERE id = p_family;
  IF v_plan IS NULL OR v_owner IS NULL THEN RAISE EXCEPTION 'plano ou familia inexistente' USING ERRCODE = 'NN020'; END IF;
  INSERT INTO nina.family_entitlement (family_id, plan_id, source, status, valid_until)
  VALUES (p_family, v_plan, 'MANUAL_GRANT', 'ACTIVE', p_valid_until) RETURNING id INTO v_id;
  INSERT INTO nina.family_entitlement_member (entitlement_id, user_id, member_role) VALUES (v_id, v_owner, 'HOLDER');
  INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, metadata_safe, is_critical)
  VALUES (v_actor, 'ADMIN', 'entitlement.manual_granted', 'ENTITLEMENT', v_id, jsonb_build_object('plan', p_plan_code, 'reason', p_reason), true);
  RETURN v_id;
END $$;

-- Previa do convite para o CONVIDADO (so ele nao enxerga a linha por RLS): papel, convidante e inicial do bebe, sem nome completo
-- (SEC-051). Mesma regra de destinatario do aceite; qualquer divergencia = nenhuma linha (resposta uniforme).
CREATE FUNCTION nina.inspect_invitation(p_token_hash bytea)
RETURNS TABLE (role text, inviter_display_name text, baby_initial text, invite_expires_at timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = nina, pg_temp AS $$
  SELECT m.role, i.display_name, upper(left(b.display_name, 1)), m.invite_expires_at
    FROM nina.caregiver_membership m
    JOIN nina.baby b ON b.id = m.baby_id AND b.deleted_at IS NULL
    LEFT JOIN nina.app_user i ON i.id = m.invited_by
    JOIN nina.app_user me ON me.id = nina.current_user_id() AND me.status = 'ACTIVE'
   WHERE m.invite_token_hash = p_token_hash AND m.status = 'PENDING' AND m.invite_expires_at > now()
     AND (m.user_id IS NULL OR m.user_id = me.id) AND (m.invited_email IS NULL OR m.invited_email = me.email_normalized) $$;

-- -----------------------------------------------------------------------------
-- 12. Row-Level Security (defesa em profundidade - SEC-003)
--     Contexto: SET LOCAL nina.user_id = '<uuid>' no inicio de cada transacao (set_config(..., true)).
--     Sem contexto => nenhuma linha visivel. nina_worker tem politica propria. TODAS as tabelas do schema tem RLS, exceto os
--     5 catalogos de referencia sem dado de tenant (plan, feature_flag, plan_feature, app_parameter, consent_purpose), que o app so le.
--     FORCE ROW LEVEL SECURITY nao e usado: os helpers/funcoes SECURITY DEFINER rodam como dono e precisam enxergar tudo; a
--     compensacao e o app nunca ser dono/superuser (verificado por teste de catalogo) e o dono nao ser membro de nina_*.
-- -----------------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
  -- baby-scoped (R-03: conjuntos avaliados 1x por comando): leitura por qualquer membro ativo, escrita por owner/caregiver
  FOREACH t IN ARRAY ARRAY['sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'wake_event', 'sleep_schedule_preference', 'sleep_prediction'] LOOP
    EXECUTE format('ALTER TABLE nina.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY app_select ON nina.%I FOR SELECT TO nina_app USING (baby_id IN (SELECT nina.readable_babies()))', t);
    EXECUTE format('CREATE POLICY app_insert ON nina.%I FOR INSERT TO nina_app WITH CHECK (baby_id IN (SELECT nina.writable_babies()))', t);
    EXECUTE format('CREATE POLICY app_update ON nina.%I FOR UPDATE TO nina_app USING (baby_id IN (SELECT nina.writable_babies())) WITH CHECK (baby_id IN (SELECT nina.writable_babies()))', t);
  END LOOP;
  -- somente leitura para o app (escritas feitas por triggers definer / worker)
  FOREACH t IN ARRAY ARRAY['change_log', 'tombstone'] LOOP
    EXECUTE format('ALTER TABLE nina.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY app_select ON nina.%I FOR SELECT TO nina_app USING (baby_id IN (SELECT nina.readable_babies()))', t);
  END LOOP;
  -- por usuario (SELECT proprio); demais comandos abaixo
  FOREACH t IN ARRAY ARRAY['notification_preference', 'notification_job', 'auth_session', 'device_push_token', 'consent_record',
                           'data_export_request', 'account_deletion_request', 'privacy_request', 'app_user_placeholder'] LOOP
    CONTINUE WHEN t = 'app_user_placeholder';
    EXECUTE format('ALTER TABLE nina.%I ENABLE ROW LEVEL SECURITY', t);
    EXECUTE format('CREATE POLICY app_select ON nina.%I FOR SELECT TO nina_app USING (user_id = nina.current_user_id())', t);
  END LOOP;
  -- tabelas sem nenhuma politica para o app (so funcoes definer / worker): RLS ligada = fail-closed
  FOREACH t IN ARRAY ARRAY['baby_sync_head', 'config_change', 'audit_event', 'schema_migration', 'guard_secret', 'audit_action',
                           'audit_chain_checkpoint', 'erasure_ledger', 'email_verification_code', 'email_change_request', 'reauth_jti',
                           'server_key', 'control_lock', 'outbox_event_type'] LOOP
    EXECUTE format('ALTER TABLE nina.%I ENABLE ROW LEVEL SECURITY', t);
  END LOOP;
END $$;

-- sync_mutation: o proprio usuario SEMPRE enxerga as suas (colisao de mutation_id nunca fica invisivel por perda de acesso, R-04)
ALTER TABLE nina.sync_mutation ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.sync_mutation FOR SELECT TO nina_app
  USING (baby_id IN (SELECT nina.readable_babies()) OR user_id = nina.current_user_id());
CREATE POLICY app_insert ON nina.sync_mutation FOR INSERT TO nina_app
  WITH CHECK (baby_id IN (SELECT nina.writable_babies()) AND user_id = nina.current_user_id());

CREATE POLICY app_insert ON nina.notification_preference FOR INSERT TO nina_app
  WITH CHECK (user_id = nina.current_user_id() AND baby_id IN (SELECT nina.readable_babies()));
CREATE POLICY app_update ON nina.notification_preference FOR UPDATE TO nina_app
  USING (user_id = nina.current_user_id()) WITH CHECK (user_id = nina.current_user_id());
CREATE POLICY app_delete ON nina.notification_preference FOR DELETE TO nina_app USING (user_id = nina.current_user_id());

CREATE POLICY app_insert ON nina.auth_session FOR INSERT TO nina_app WITH CHECK (user_id = nina.current_user_id());
CREATE POLICY app_update ON nina.auth_session FOR UPDATE TO nina_app
  USING (user_id = nina.current_user_id()) WITH CHECK (user_id = nina.current_user_id());

-- token de push: escrita so por nina.register_push_token (que trata a troca de dono do aparelho); o usuario remove os seus
CREATE POLICY app_delete ON nina.device_push_token FOR DELETE TO nina_app USING (user_id = nina.current_user_id());

-- consentimento: so os proprios, nunca origem SYSTEM/INVITE_ACCEPT (reservadas ao banco); consentimento de bebe exige vinculo ativo (O5)
CREATE POLICY app_insert ON nina.consent_record FOR INSERT TO nina_app
  WITH CHECK (user_id = nina.current_user_id() AND source IN ('ONBOARDING', 'SETTINGS', 'PROMPT')
              AND (subject_baby_id IS NULL OR subject_baby_id IN (SELECT nina.readable_babies())));

CREATE POLICY app_insert ON nina.data_export_request FOR INSERT TO nina_app
  WITH CHECK (user_id = nina.current_user_id() AND status = 'REQUESTED' AND file_ref IS NULL AND expires_at IS NULL AND completed_at IS NULL
              AND baby_ids <@ ARRAY(SELECT nina.readable_babies()));

-- exclusao de conta: o pedido nasce por nina.request_account_deletion (confirmacao comprovada); o app so CANCELA o proprio.
-- O trigger account_deletion_guard limita a SCHEDULED/BLOCKED -> CANCELLED e a janela.
CREATE POLICY app_update ON nina.account_deletion_request FOR UPDATE TO nina_app
  USING (user_id = nina.current_user_id() AND status IN ('SCHEDULED', 'BLOCKED'))
  WITH CHECK (user_id = nina.current_user_id() AND status = 'CANCELLED');

-- Identity (SR-003): cada usuario ve/escreve SO as proprias linhas; fluxos pre-autenticacao usam as funcoes auth_* (secao 11)
ALTER TABLE nina.app_user ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.app_user FOR SELECT TO nina_app USING (id = nina.current_user_id());
CREATE POLICY app_insert ON nina.app_user FOR INSERT TO nina_app
  WITH CHECK (id = nina.current_user_id() AND status = 'ACTIVE' AND deleted_at IS NULL AND email_verified_at IS NULL);   -- NR-09: nasce NAO verificado
CREATE POLICY app_update ON nina.app_user FOR UPDATE TO nina_app
  USING (id = nina.current_user_id() AND status = 'ACTIVE') WITH CHECK (id = nina.current_user_id() AND status = 'ACTIVE');

ALTER TABLE nina.user_credential ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.user_credential FOR SELECT TO nina_app USING (user_id = nina.current_user_id());
CREATE POLICY app_insert ON nina.user_credential FOR INSERT TO nina_app WITH CHECK (user_id = nina.current_user_id());
CREATE POLICY app_update ON nina.user_credential FOR UPDATE TO nina_app
  USING (user_id = nina.current_user_id()) WITH CHECK (user_id = nina.current_user_id());

ALTER TABLE nina.user_identity ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.user_identity FOR SELECT TO nina_app USING (user_id = nina.current_user_id());
CREATE POLICY app_insert ON nina.user_identity FOR INSERT TO nina_app WITH CHECK (user_id = nina.current_user_id());
CREATE POLICY app_delete ON nina.user_identity FOR DELETE TO nina_app USING (user_id = nina.current_user_id());

-- Nao desvincular o ULTIMO metodo de login (sem senha e sem outra identidade a conta ficaria inacessivel)
CREATE FUNCTION nina.user_identity_unlink_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = nina, pg_temp AS $$
BEGIN
  IF OLD.user_id = nina.current_user_id()
     AND NOT EXISTS (SELECT 1 FROM nina.user_credential c WHERE c.user_id = OLD.user_id)
     AND NOT EXISTS (SELECT 1 FROM nina.user_identity i WHERE i.user_id = OLD.user_id AND i.id <> OLD.id) THEN
    RAISE EXCEPTION 'LAST_LOGIN_METHOD: nao e possivel remover o ultimo metodo de acesso' USING ERRCODE = 'NN055';
  END IF;
  RETURN OLD;
END $$;
CREATE TRIGGER user_identity_unlink_guard BEFORE DELETE ON nina.user_identity FOR EACH ROW EXECUTE FUNCTION nina.user_identity_unlink_guard();

-- refresh_token acompanha a sessao do usuario (a politica de auth_session ja filtra o subselect)
ALTER TABLE nina.refresh_token ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.refresh_token FOR SELECT TO nina_app
  USING (EXISTS (SELECT 1 FROM nina.auth_session s WHERE s.id = refresh_token.session_id));
CREATE POLICY app_insert ON nina.refresh_token FOR INSERT TO nina_app
  WITH CHECK (EXISTS (SELECT 1 FROM nina.auth_session s WHERE s.id = refresh_token.session_id));
CREATE POLICY app_update ON nina.refresh_token FOR UPDATE TO nina_app
  USING (EXISTS (SELECT 1 FROM nina.auth_session s WHERE s.id = refresh_token.session_id))
  WITH CHECK (EXISTS (SELECT 1 FROM nina.auth_session s WHERE s.id = refresh_token.session_id));

ALTER TABLE nina.recovery_request ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.recovery_request FOR SELECT TO nina_app USING (user_id = nina.current_user_id());
CREATE POLICY app_insert ON nina.recovery_request FOR INSERT TO nina_app WITH CHECK (user_id = nina.current_user_id());
CREATE POLICY app_update ON nina.recovery_request FOR UPDATE TO nina_app
  USING (user_id = nina.current_user_id()) WITH CHECK (user_id = nina.current_user_id());

-- Family / baby (SR-001): a familia so e visivel/criavel pelo titular; o perfil do bebe so por vinculo ATIVO, exceto no instante
-- de criacao (bebe da propria familia que ainda nao teve Owner). A titularidade e family_id sao imutaveis (triggers).
ALTER TABLE nina.family ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.family FOR SELECT TO nina_app USING (owner_user_id = nina.current_user_id());
CREATE POLICY app_insert ON nina.family FOR INSERT TO nina_app WITH CHECK (owner_user_id = nina.current_user_id());

ALTER TABLE nina.baby ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.baby FOR SELECT TO nina_app
  USING (id IN (SELECT nina.readable_babies())
         -- instante de criacao: bebe da PROPRIA familia que ainda nunca teve Owner. Nao le a tabela baby (a linha recem-inserida
         -- nao e visivel a uma funcao STABLE no mesmo comando, o que quebraria INSERT ... RETURNING).
         OR (family_id IN (SELECT f.id FROM nina.family f) AND NOT nina.baby_ever_had_owner(id)));
CREATE POLICY app_insert ON nina.baby FOR INSERT TO nina_app WITH CHECK (nina.is_family_owner(family_id));
CREATE POLICY app_update ON nina.baby FOR UPDATE TO nina_app
  USING (nina.baby_role(id) = 'OWNER') WITH CHECK (nina.baby_role(id) = 'OWNER');

-- membership (SR-002/SR-009): o usuario ve os proprios vinculos, o Owner os do bebe. INSERT: Owner so cria CONVITE pendente
-- (nunca ACTIVE, nunca OWNER); bootstrap do primeiro Owner. NAO ha politica nem grant de UPDATE/DELETE: so as funcoes da secao 10.
ALTER TABLE nina.caregiver_membership ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.caregiver_membership FOR SELECT TO nina_app
  USING ((user_id = nina.current_user_id() AND NOT nina.own_link_on_deleted_baby(baby_id)) OR nina.baby_role(baby_id) = 'OWNER');
CREATE POLICY app_insert ON nina.caregiver_membership FOR INSERT TO nina_app
  WITH CHECK (
    (nina.baby_role(baby_id) = 'OWNER' AND status = 'PENDING' AND role IN ('CAREGIVER', 'READ_ONLY')
       AND invited_by = nina.current_user_id() AND invite_token_hash IS NOT NULL
       AND invite_expires_at > now() AND invite_expires_at <= now() + interval '30 days'
       AND accepted_at IS NULL AND revoked_at IS NULL AND revoked_reason IS NULL AND invite_used_at IS NULL
       -- NR-13: convite so por e-mail (user_id nulo): sem convite nao solicitado a um usuario existente, sem oraculo de UUID por FK
       AND invite_failed_attempts = 0 AND user_id IS NULL AND invited_email IS NOT NULL)
    OR
    (user_id = nina.current_user_id() AND role = 'OWNER' AND status = 'ACTIVE' AND invited_by IS NULL AND invite_token_hash IS NULL
       AND revoked_at IS NULL AND nina.can_bootstrap_owner(baby_id)));

-- outbox: INSERT-only para o app (sem SELECT/UPDATE: nao expoe aggregate_id de outros tenants). NR-11: so por 4 colunas (GRANT) e
-- so tipos "do proprio usuario": o app enfileira avisos de seguranca PARA SI; as demais mensagens (exclusao, convites, exportacao...)
-- nascem das funcoes definer. Nunca event_key, processed_at, attempts nem available_at.
ALTER TABLE nina.outbox_message ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_insert ON nina.outbox_message FOR INSERT TO nina_app
  WITH CHECK (event_type = 'SecurityNoticeRequested' AND aggregate_type = 'USER' AND aggregate_id = nina.current_user_id());

-- assinatura / entitlement: leitura pelo titular (e pelos membros do entitlement); nenhuma escrita pelo app
ALTER TABLE nina.subscription ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.subscription FOR SELECT TO nina_app USING (family_id IN (SELECT f.id FROM nina.family f));
ALTER TABLE nina.family_entitlement ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.family_entitlement FOR SELECT TO nina_app
  USING (family_id IN (SELECT f.id FROM nina.family f) OR nina.is_entitlement_member(id));
ALTER TABLE nina.family_entitlement_member ENABLE ROW LEVEL SECURITY;
CREATE POLICY app_select ON nina.family_entitlement_member FOR SELECT TO nina_app
  USING (user_id = nina.current_user_id() OR nina.owns_entitlement(entitlement_id));

-- config_admin le o historico de configuracao (a tabela tem RLS ligada)
CREATE POLICY config_admin_read ON nina.config_change FOR SELECT TO nina_config_admin USING (true);

-- politica do worker em todas as tabelas com RLS (os PRIVILEGIOS, abaixo, e que limitam o que ele faz em cada uma)
DO $$
DECLARE t text;
BEGIN
  FOR t IN SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'nina' AND c.relkind = 'r' AND c.relrowsecurity LOOP
    EXECUTE format('CREATE POLICY worker_all ON nina.%I FOR ALL TO nina_worker USING (true) WITH CHECK (true)', t);
  END LOOP;
END $$;

-- -----------------------------------------------------------------------------
-- 13. Privilégios (minimo necessario; SR-003/SR-012). A matriz papel x tabela x operacao e verificada por teste de snapshot.
-- -----------------------------------------------------------------------------
REVOKE ALL ON ALL TABLES IN SCHEMA nina FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA nina FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA nina FROM PUBLIC;

-- ---- nina_app: leitura/escrita onde o modelo exige, por COLUNA quando ha campos que o app nao pode mudar
GRANT SELECT ON nina.plan, nina.feature_flag, nina.plan_feature, nina.app_parameter, nina.consent_purpose, nina.consent_current TO nina_app;
GRANT SELECT ON nina.app_user TO nina_app;
-- NR-09: email_verified_at NAO esta no INSERT nem no UPDATE do app: so email_code_verify/register_social_user/email_change_confirm.
GRANT INSERT (id, email, display_name, locale, timezone, created_at, updated_at) ON nina.app_user TO nina_app;
GRANT UPDATE (display_name, locale, timezone) ON nina.app_user TO nina_app;   -- e-mail/status/verificacao: so por funcoes definer
GRANT SELECT, INSERT ON nina.user_credential TO nina_app;
GRANT UPDATE (password_hash, hash_algorithm, changed_at) ON nina.user_credential TO nina_app;
GRANT SELECT, INSERT, DELETE ON nina.user_identity TO nina_app;
GRANT SELECT, INSERT ON nina.auth_session TO nina_app;
GRANT UPDATE (last_seen_at, revoked_at, revoked_reason) ON nina.auth_session TO nina_app;
GRANT SELECT, INSERT ON nina.refresh_token TO nina_app;
GRANT UPDATE (used_at) ON nina.refresh_token TO nina_app;
GRANT SELECT, INSERT ON nina.recovery_request TO nina_app;
GRANT UPDATE (used_at) ON nina.recovery_request TO nina_app;
GRANT SELECT, DELETE ON nina.device_push_token TO nina_app;
GRANT SELECT, INSERT ON nina.family TO nina_app;
GRANT SELECT, INSERT ON nina.baby TO nina_app;
-- NR-06/NR-07: sem deleted_at (exclusao so por nina.delete_baby) e sem last_modified_by (fixado pelo gatilho a partir do contexto).
GRANT UPDATE (display_name, birth_date, due_date, sex, timezone, photo_ref, field_versions) ON nina.baby TO nina_app;
GRANT SELECT, INSERT ON nina.caregiver_membership TO nina_app;                       -- sem UPDATE/DELETE (SR-002)
DO $$
DECLARE t text; cols text;
BEGIN
  FOREACH t IN ARRAY ARRAY['sleep_session', 'feeding_session', 'pumping_session', 'diaper_event', 'wake_event', 'sleep_schedule_preference', 'sleep_prediction'] LOOP
    EXECUTE format('GRANT SELECT, INSERT ON nina.%I TO nina_app', t);
    SELECT string_agg(quote_ident(a.attname), ', ' ORDER BY a.attnum) INTO cols
      FROM pg_attribute a
     WHERE a.attrelid = ('nina.' || t)::regclass AND a.attnum > 0 AND NOT a.attisdropped AND a.attgenerated = ''
       AND a.attname NOT IN ('id', 'baby_id', 'version', 'created_at', 'updated_at', 'created_by', 'last_modified_by', 'computed_at');
    EXECUTE format('GRANT UPDATE (%s) ON nina.%I TO nina_app', cols, t);
  END LOOP;
END $$;
GRANT SELECT ON nina.change_log, nina.tombstone TO nina_app;
GRANT SELECT, INSERT ON nina.sync_mutation TO nina_app;
GRANT SELECT, INSERT, DELETE ON nina.notification_preference TO nina_app;
GRANT UPDATE (enabled, lead_time_minutes, quiet_hours_start, quiet_hours_end) ON nina.notification_preference TO nina_app;
GRANT SELECT ON nina.notification_job TO nina_app;                                   -- criado/atualizado pelo worker
GRANT INSERT (aggregate_type, aggregate_id, event_type, payload) ON nina.outbox_message TO nina_app;   -- INSERT-only, so estas colunas (NR-11)
GRANT SELECT ON nina.subscription, nina.family_entitlement, nina.family_entitlement_member TO nina_app;
GRANT SELECT, INSERT ON nina.consent_record TO nina_app;
GRANT SELECT, INSERT ON nina.data_export_request TO nina_app;
GRANT SELECT ON nina.account_deletion_request TO nina_app;
GRANT UPDATE (status) ON nina.account_deletion_request TO nina_app;                   -- cancelamento (trigger valida a janela)
GRANT SELECT ON nina.privacy_request TO nina_app;                                     -- escrita so via funcoes definer

GRANT EXECUTE ON FUNCTION nina.current_user_id(), nina.param_int(text, integer), nina.param_text(text, text),
  nina.sleep_overlaps(uuid), nina.sleep_overlaps(uuid, uuid), nina.night_awakenings(uuid), nina.night_awakenings(uuid, uuid),
  nina.age_calculation(date, date, date) TO nina_app;
-- funcoes puras usadas por CHECK (o EXECUTE e verificado no INSERT, com o papel do chamador): outbox_message.payload
GRANT EXECUTE ON FUNCTION nina.jsonb_pii_ok(jsonb, integer), nina.pii_key(text) TO nina_app, nina_worker;
-- helpers de RLS (executados pelo planner com o papel corrente)
GRANT EXECUTE ON FUNCTION nina.baby_role(uuid), nina.can_read_baby(uuid), nina.can_write_baby(uuid), nina.is_family_owner(uuid),
  nina.can_bootstrap_owner(uuid), nina.baby_ever_had_owner(uuid), nina.readable_babies(), nina.writable_babies(), nina.own_link_on_deleted_baby(uuid),
  nina.owns_entitlement(uuid), nina.is_entitlement_member(uuid) TO nina_app;
-- funcoes de negocio do app (todas SECURITY DEFINER, ator = nina.user_id)
GRANT EXECUTE ON FUNCTION nina.my_plan_code(), nina.my_has_feature(text), nina.sync_head(uuid), nina.my_audit_events(integer, bigint),
  nina.baby_member_refs(uuid), nina.audit(text, text, uuid, uuid, uuid, text, text, bytea, jsonb),
  nina.audit_auth_attempt(text, uuid, uuid, text, bytea, jsonb),
  nina.auth_lookup_user_by_email(text), nina.auth_lookup_identity(text, text), nina.auth_consume_recovery(bytea, timestamptz),
  nina.reauth_issue(bytea, uuid, text[], timestamptz, timestamptz, bytea), nina.consume_reauth_jti(bytea, text, uuid),
  nina.delete_baby(uuid, bytea, boolean), nina.register_social_user(uuid, text, text, text, text, text, text, timestamptz, bytea),
  nina.register_push_token(uuid, text, text, text, text, text, boolean, timestamptz),
  nina.email_code_issue(bytea, timestamptz, timestamptz, interval, bytea), nina.email_code_verify(text, bytea, timestamptz),
  nina.email_change_create(text, bytea, timestamptz, timestamptz, bytea, bytea), nina.email_change_confirm(bytea, timestamptz),
  nina.inspect_invitation(bytea), nina.accept_invitation(bytea, text, text, text, text, text), nina.decline_invitation(bytea),
  nina.leave_baby(uuid), nina.remove_member(uuid), nina.set_member_role(uuid, text), nina.transfer_ownership(uuid, uuid, bytea),
  nina.request_account_deletion(bytea), nina.confirm_account_deletion(uuid, bytea),
  nina.open_privacy_request(text, text, bytea), nina.cancel_privacy_request(uuid) TO nina_app;

-- ---- nina_worker: leitura ampla (menos segredos) e DML SO nas tabelas operacionais; nenhum DML em tabelas append-only
GRANT SELECT ON ALL TABLES IN SCHEMA nina TO nina_worker;
REVOKE SELECT ON nina.user_credential, nina.refresh_token, nina.recovery_request, nina.email_verification_code,
  nina.email_change_request, nina.reauth_jti, nina.guard_secret, nina.server_key, nina.control_lock FROM nina_worker;
GRANT INSERT, UPDATE, DELETE ON nina.outbox_message, nina.notification_job, nina.sleep_prediction TO nina_worker;
GRANT INSERT, UPDATE ON nina.data_export_request, nina.subscription, nina.family_entitlement, nina.family_entitlement_member TO nina_worker;
GRANT UPDATE, DELETE ON nina.device_push_token TO nina_worker;
GRANT EXECUTE ON FUNCTION nina.current_user_id(), nina.param_int(text, integer), nina.param_text(text, text),
  nina.user_plan_code(uuid), nina.user_has_feature(uuid, text), nina.is_active_baby_owner(uuid), nina.age_calculation(date, date, date),
  nina.erase_baby(uuid, uuid), nina.erase_user(uuid, uuid), nina.scrub_user_personal_data(uuid, uuid),
  nina.fulfill_privacy_erasure(uuid, uuid), nina.close_privacy_request(uuid, text, text, uuid),
  nina.purge_expired_sync_data(integer), nina.purge_expired_operational_data(integer), nina.purge_audit(interval, integer),
  nina.purge_consent(interval, integer), nina.reapply_erasure_ledger(), nina.audit_privileged(text, text, uuid, text, uuid, uuid, text, jsonb),
  nina.verify_audit_chain(), nina.audit_chain_head() TO nina_worker;

-- ---- nina_config_admin: configuracao editada somente por ele (auditada por trigger); sem INSERT em auditoria (SR-006)
GRANT SELECT, INSERT, UPDATE, DELETE ON nina.plan, nina.feature_flag, nina.plan_feature, nina.app_parameter TO nina_config_admin;
GRANT SELECT ON nina.config_change TO nina_config_admin;
GRANT EXECUTE ON FUNCTION nina.current_user_id(), nina.param_int(text, integer), nina.param_text(text, text),
  nina.grant_manual_entitlement(uuid, text, timestamptz, text) TO nina_config_admin;

-- -----------------------------------------------------------------------------
-- 14. Seeds (ADR-0005, ADR-0003, privacy-spec 3.2/4). Gates nascem sem flags: bloqueado por padrão.
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
  ('privacy.deletion_grace_days',       '7'::jsonb,     'INT',    'ADR-0010 item 6: janela de arrependimento da exclusao de conta e da anonimizacao, em dias (1..30). scheduled_for = requested_at + este valor (congelado no pedido); erase_user/fulfill_privacy_erasure nao executam antes'),
  ('privacy.request_response_days',     '15'::jsonb,    'INT',    'ADR-0010 item 3 / privacy-spec 5: prazo de atendimento de requisicao de privacidade (DSAR), em dias (1..60); due_at. Validacao juridica: DJ-07'),
  ('invites.max_per_day',               '20'::jsonb,    'INT',    'SEC-004: limite de convites criados por Owner nas ultimas 24 h (1..200)'),
  ('invites.max_failed_attempts',       '5'::jsonb,     'INT',    'SEC-004: tentativas de aceite por destinatario errado antes de o convite expirar (1..10)'),
  ('auth.email_code_max_attempts',      '5'::jsonb,     'INT',    'SEC-041: tentativas de codigo de e-mail antes de invalidar o codigo (1..10)'),
  ('audit.retention_months',            '12'::jsonb,    'INT',    'SR-005: idade minima (meses) para purgar auditoria comum; piso fixo 12'),
  ('audit.critical_retention_months',   '60'::jsonb,    'INT',    'SR-005: idade minima (meses) para purgar auditoria encadeada (critica/negada/de sistema); piso fixo 60. Validar DJ-06'),
  ('consent.retention_months',          '60'::jsonb,    'INT',    'SR-005: idade minima (meses) para purgar consentimento SUBSTITUIDO; piso fixo 60. Validar DJ-06'),
  ('erasure_ledger.retention_days',     '90'::jsonb,    'INT',    'SR-011: retencao do ledger de exclusoes (>= ciclo de backup de 35 dias)');

INSERT INTO nina.consent_purpose (purpose_key, description, is_required, scope, current_version, in_mvp) VALUES
  ('terms_of_use',        'Aceite dos Termos de Uso',                                   true,  'USER', '1.0.0', true),
  ('privacy_policy',      'Ciencia da Politica de Privacidade',                         true,  'USER', '1.0.0', true),
  ('child_data_guardian', 'Declaracao de responsavel legal e tratamento de dados do bebe (art. 14)', true, 'BABY', '1.0.0', true),
  -- provisorio (privacy-spec F4/DJ-01): fora de /legal/documents (in_mvp=false) porque PurposeKey do contrato 1.0.1 nao a lista
  ('caregiver_data_ack',  'Ciencia do cuidador convidado sobre o tratamento de dados do bebe (aceite do convite)', true, 'BABY', '1.0.0', false),
  ('analytics_product',   'Analytics de produto',                                       false, 'USER', '1.0.0', true),
  ('marketing_email',     'E-mails promocionais',                                       false, 'USER', '1.0.0', false),
  ('push_notifications',  'Registro de token e envio de push',                          false, 'USER', '1.0.0', true);

-- Catalogo de auditoria (SR-006): so estas acoes podem ser gravadas por nina.audit / audit_auth_attempt / audit_privileged.
INSERT INTO nina.audit_action (action, is_critical, caller, results, allowed_keys) VALUES
  ('auth.register',                 false, 'USER',       ARRAY['SUCCESS'], ARRAY['method']),
  ('auth.email_verified',           false, 'USER',       ARRAY['SUCCESS'], ARRAY['method', 'platform']),
  ('auth.login',                    false, 'USER',       ARRAY['SUCCESS'], ARRAY['method', 'platform']),
  ('auth.logout',                   false, 'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('auth.password_changed',         true,  'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('auth.password_reset_requested', false, 'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('auth.password_reset',           true,  'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('auth.reauthenticated',          false, 'USER',       ARRAY['SUCCESS'], ARRAY['jti', 'scopes']),
  ('auth.reauth_consumed',          false, 'USER',       ARRAY['SUCCESS'], ARRAY['jti', 'scope']),
  ('auth.refresh_reuse_detected',   true,  'USER',       ARRAY['DENIED'],  ARRAY[]::text[]),
  ('auth.session_revoked',          false, 'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('auth.sessions_revoked',         false, 'USER',       ARRAY['SUCCESS'], ARRAY['count']),
  ('auth.identity_linked',          true,  'USER',       ARRAY['SUCCESS'], ARRAY['provider']),
  ('auth.identity_unlinked',        true,  'USER',       ARRAY['SUCCESS'], ARRAY['provider']),
  ('consent.granted',               false, 'USER',       ARRAY['SUCCESS'], ARRAY['purpose_key', 'version']),
  ('consent.revoked',               false, 'USER',       ARRAY['SUCCESS'], ARRAY['purpose_key', 'version']),
  ('push_token.registered',         false, 'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('push_token.removed',            false, 'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('sync.conflict_resolved',        false, 'USER',       ARRAY['SUCCESS'], ARRAY['entity_type', 'fields', 'resolution', 'base_version', 'version']),
  ('data_export.requested',         true,  'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('data_export.downloaded',        true,  'USER',       ARRAY['SUCCESS'], ARRAY[]::text[]),
  ('auth.login_failed',             false, 'PRE_AUTH',   ARRAY['FAILURE'], ARRAY[]::text[]),
  ('auth.email_verify_failed',      false, 'PRE_AUTH',   ARRAY['FAILURE'], ARRAY[]::text[]),
  ('auth.reauth_failed',            false, 'PRE_AUTH',   ARRAY['FAILURE'], ARRAY[]::text[]),
  ('support.access_granted',        true,  'PRIVILEGED', ARRAY['SUCCESS', 'DENIED'], ARRAY['reason_code']),
  ('retention.job_run',             false, 'PRIVILEGED', ARRAY['SUCCESS', 'FAILURE'], ARRAY['job', 'count']);

INSERT INTO nina.outbox_event_type (event_type, aggregate_type, allowed_keys) VALUES
  ('BabyDeleted',                 'BABY',            ARRAY[]::text[]),
  ('BabyOwnershipTransferred',    'BABY',            ARRAY[]::text[]),
  ('CaregiverJoined',             'BABY',            ARRAY[]::text[]),
  ('CaregiverLeft',               'BABY',            ARRAY[]::text[]),
  ('CaregiverRemoved',            'BABY',            ARRAY[]::text[]),
  ('ExportFileInvalidated',       'EXPORT',          ARRAY[]::text[]),
  ('SharedBabyDeletedNotice',     'USER',            ARRAY['baby_id']),
  ('SharedBabyDeletionScheduled', 'USER',            ARRAY['baby_id']),
  ('AccountDeletionRequested',    'USER',            ARRAY['request_id']),
  ('AccountDeleted',              'USER',            ARRAY[]::text[]),
  ('PersonalDataErased',          'USER',            ARRAY[]::text[]),
  ('EmailChanged',                'USER',            ARRAY[]::text[]),
  ('SecurityNoticeRequested',     'USER',            ARRAY['notice']),
  ('PrivacyRequestOpened',        'PRIVACY_REQUEST', ARRAY['request_type']),
  ('PrivacyRequestCancelled',     'PRIVACY_REQUEST', ARRAY[]::text[]);

INSERT INTO nina.schema_migration (version, description) VALUES ('0001', 'init: modelo fisico MVP (DB-001) + convencoes ADR-0009/0010 + hardening SECURITY-REVIEW-001 e reteste (NR-03..NR-13) + sync (ARCH-003)');

SELECT nina.guard_disarm('migration');

COMMIT;
