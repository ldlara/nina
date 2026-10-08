#!/bin/sh
# Bootstrap do PostgreSQL local (SR-018). Roda UMA vez (volume vazio), como o migrator/dono (POSTGRES_USER).
#  1. aplica backend/db/migrations/0001_init.sql com o dono (cria schema nina, papéis NOLOGIN, RLS e grants);
#  2. concede LOGIN + senha aos papéis de aplicação (senhas vêm do .env via ambiente do contêiner).
# A API/BFF conecta SOMENTE como nina_app; o dono/superusuário nunca é usado pela aplicação (ignoraria a RLS).
set -eu

: "${NINA_APP_PASSWORD:?NINA_APP_PASSWORD ausente}"
: "${NINA_WORKER_PASSWORD:?NINA_WORKER_PASSWORD ausente}"
: "${NINA_CONFIG_ADMIN_PASSWORD:?NINA_CONFIG_ADMIN_PASSWORD ausente}"

MIGRATION=/migrations/0001_init.sql
[ -r "$MIGRATION" ] || { echo "migração não encontrada em $MIGRATION" >&2; exit 1; }

echo "[nina-init] aplicando 0001_init.sql como ${POSTGRES_USER}"
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -f "$MIGRATION"

echo "[nina-init] concedendo LOGIN aos papéis de aplicação"
# Senhas passadas como variáveis psql e citadas com :'var' (sem interpolação no shell, sem aparecer em ps).
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v app_pw="$NINA_APP_PASSWORD" \
  -v worker_pw="$NINA_WORKER_PASSWORD" \
  -v config_pw="$NINA_CONFIG_ADMIN_PASSWORD" <<'SQL'
ALTER ROLE nina_app          WITH LOGIN PASSWORD :'app_pw'    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
ALTER ROLE nina_worker       WITH LOGIN PASSWORD :'worker_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
ALTER ROLE nina_config_admin WITH LOGIN PASSWORD :'config_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
REVOKE ALL ON DATABASE nina FROM PUBLIC;
GRANT CONNECT ON DATABASE nina TO nina_app, nina_worker, nina_config_admin;
SQL
echo "[nina-init] ok"
