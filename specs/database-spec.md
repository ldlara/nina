# Especificação de Banco de Dados — Nina (DB-001)

Status: rascunho para revisão (ARCH-001/ARCH-003/API-001) · Data: 2026-10-08
Fontes: `specs/domain-model.md`, `specs/product-spec.md`, `specs/privacy-security-spec.md`, ADR-0003, 0005, 0007, 0008, `docs/project/architecture.md`.
DDL executável: `backend/db/migrations/0001_init.sql` (PostgreSQL 15+; validado em 16.15, ver seção 14).
Marcação: **[D]** decisão de ADR; **[P]** proposta desta spec a validar; **[Q]** pergunta aberta (seção 13).

## 1. Princípios

1. PostgreSQL é a fonte canônica; o cliente nunca decide versão, entitlement nem autorização (RB-009).
2. Schema único `nina`, um módulo lógico por grupo de tabelas (ADR-0002). Sem extensões externas (apenas `gen_random_uuid()` nativo).
3. Tempo: toda coluna de instante é `timestamptz` (armazenado em UTC); fuso é dado contextual em colunas `tz`/`timezone` com domínio `nina.iana_tz` (valida nome IANA conhecido pelo servidor). Conexões usam `timezone=UTC`. Mudar o fuso do bebê não reescreve eventos (INV-17).
4. IDs: `uuid`. Entidades sincronizáveis recebem o UUID do cliente; demais usam `gen_random_uuid()`.
5. Invariantes de dados vão para o banco quando possível (CHECK, índices únicos parciais, triggers); regras que dependem de contexto de negócio ficam na API, mas o banco é a última barreira.
6. Dados de bebê são isolados por `baby_id` (RLS, seção 9). Segredos/tokens só como hash (`bytea` SHA-256), exceto token de push (necessário em claro ao provedor; recomenda-se cifra em nível de campo, SEC-021).
7. Texto livre (`notes`) e nome do bebê são C3: nunca em log, auditoria nem analytics (RB-011).

## 2. Mapa de tabelas

| Módulo | Tabelas |
|---|---|
| Identity | `app_user`, `user_credential`, `user_identity`, `auth_session`, `refresh_token`, `recovery_request`, `device_push_token` |
| Family | `family`, `baby`, `caregiver_membership` |
| Tracking | `sleep_session`, `feeding_session`, `pumping_session`, `diaper_event` |
| SleepIntelligence | `sleep_schedule_preference`, `sleep_prediction` |
| Sync | `baby_sync_head`, `change_log`, `tombstone`, `sync_mutation` |
| Notifications | `notification_preference`, `notification_job`, `outbox_message` |
| Subscriptions / Config (ADR-0005) | `plan`, `feature_flag`, `plan_feature`, `app_parameter`, `subscription`, `family_entitlement`, `family_entitlement_member`, `config_change` |
| Privacy | `consent_purpose`, `consent_record` (+ view `consent_current`), `audit_event`, `data_export_request`, `account_deletion_request` |
| Infra | `schema_migration` |

Papéis de banco (NOLOGIN; a infra concede membership aos logins): `nina_app` (API/BFF, sujeito a RLS), `nina_worker` (jobs, outbox, purga, exclusão), `nina_config_admin` (edição de flags/planos/parâmetros). A aplicação não pode conectar como dono das tabelas nem superuser (ambos ignoram RLS).

## 3. Identity

- `app_user`: `email` + `email_normalized` (coluna gerada `lower(btrim(email))`, único parcial). Estados `active | pending_deletion | deleted`. Após exclusão, a linha **permanece anonimizada** (email/locale/timezone nulos, `deleted_at`) para manter integridade de consentimento, auditoria e registros fiscais sem reter PII. O CHECK `app_user_email_ck` impõe isso.
- `user_credential`: hash adaptativo (`argon2id`/`bcrypt`), 1:1 com usuário. Nunca logado.
- `user_identity`: Google/Apple, único por `(provider, provider_subject)`. **Não há fusão automática por e-mail** (ADR-0007); a vinculação é um INSERT explícito após confirmação.
- `auth_session` (RF-054): dispositivo, plataforma, `absolute_expires_at` (30 dias), `revoked_at/reason`. `refresh_token`: hash, uso único (`used_at`), rotativo; reuso de token já usado → revogar a sessão (`reuse_detected`) — lógica na API, dado no banco.
- `recovery_request`: hash do token, expira, uso único. `device_push_token`: único por `(platform, token)` e por `(user, device, platform)`.
- Revogação de acesso a um bebê (INV-14, RB-015) **não** exige revogar tokens: o acesso é recalculado a cada requisição via `caregiver_membership` (RLS). Remover o vínculo corta API e pull de sync imediatamente; o cliente descarta dados locais ao receber 403/404 uniforme.

## 4. Family e vínculos

- `family` **[P]**: agrupador de entitlement (ADR-0005: "o entitlement pertence à família"). Uma família por titular (`owner_user_id` único), criada no cadastro. O domain-model não tem essa entidade (D-04 resolvida pelo ADR-0005); ver [Q1].
- `baby`: `family_id`, `display_name`, `birth_date`, `due_date` (independentes, INV-15), `sex`, `timezone`, `photo_ref`. É sincronizável (colunas `version/created_at/updated_at/deleted_at/created_by/last_modified_by`). `birth_date` no futuro é rejeitada por trigger (INV-16; perfil pré-natal é D-11). Quando `deleted_at` não é nulo, os campos de conteúdo **devem** ser nulos (CHECK `baby_content_ck`): é a "casca-tombstone" sem PII.
- `caregiver_membership`: convite = linha `pending` (sem entidade Invitation). `role ∈ owner | caregiver | read_only`, `status ∈ pending | active | revoked | declined`. Convite guarda apenas o **hash** do token (≥128 bits gerados na API), `invite_expires_at` (7 dias).
  - INV-10: índice único parcial `(baby_id, user_id)` para `pending|active`.
  - INV-09 (no máximo um): índice único parcial de Owner ativo por bebê. (Pelo menos um): *constraint trigger* `DEFERRABLE INITIALLY DEFERRED` exige Owner ativo ao fim da transação para bebê não excluído (SEC-007). Transferir propriedade = rebaixar e promover na mesma transação.
  - O dono (`Owner`) é derivado do vínculo; não há `baby.owner_id` redundante [P] (evita duas fontes de verdade).

## 5. Tracking

Colunas comuns de sync: `id` (UUID do cliente), `baby_id`, `version`, `created_at`, `updated_at`, `deleted_at`, `created_by`, `last_modified_by`. `version` é **sempre** atribuída por trigger (seção 6); valor enviado pelo cliente é ignorado. Eventos guardam instante UTC + `tz` (RB-014).

| Tabela | Constraints principais |
|---|---|
| `sleep_session` | `end_at >= start_at` (INV-01); índice único parcial: no máximo **uma sessão aberta por bebê** (INV-02) `WHERE end_at IS NULL AND deleted_at IS NULL`; `sleep_type ∈ nap|night`; `source ∈ timer|manual`; `method_or_place` texto ≤60 (D-08) |
| `feeding_session` | CHECK de forma (INV-07): `breast` exige `side` e `end_at`, proíbe `volume_ml`/`milk_type`; `bottle` proíbe `side`; `volume_ml` em (0, 5000]; `milk_type` texto ≤40 |
| `pumping_session` | `end_at >= start_at`; `volume_ml` > 0; `side` opcional |
| `diaper_event` | `occurred_at`, `diaper_type` texto ≤40 (D-08 aberta) |
| `sleep_schedule_preference` | 1 por bebê (`baby_id` único); sincronizável; metas em hora local |
| `sleep_prediction` | **Derivada, não sincronizada** (INV-05); `confidence` 0..1, `explanation_key` + `explanation_params` (localizável), `model_version`, `inputs_version`; retenção 90 dias |

Decisões e lacunas:
- Sobreposição de sono (INV-03/D-05) **não** é imposta no banco: com edição offline multi-dispositivo, uma `EXCLUDE` rejeitaria dados legítimos. Se D-05 resolver por "rejeitar", usar `btree_gist` (`EXCLUDE USING gist (baby_id WITH =, tstzrange(start_at, coalesce(end_at,'infinity')) WITH &&) WHERE (deleted_at IS NULL)`).
- Duração e wake window nunca são persistidas (INV-04).
- Dois dispositivos offline iniciando sono geram violação do índice de sessão aberta no push: a API trata como conflito (resolução LWW por campo + auditoria, ADR-0003; a validar no spike ARCH-003).
- Índices de linha do tempo: `(baby_id, start_at DESC) WHERE deleted_at IS NULL` (e `occurred_at` na fralda).

## 6. Sincronização (ADR-0003)

### 6.1 Change log e versão

- `change_log (baby_id, sync_sequence, entity_type, entity_id, op, actor_user_id, device_id, changed_at)`, PK `(baby_id, sync_sequence)`. Não contém conteúdo (apenas "qual entidade mudou e se foi upsert/delete"); o pull lê o estado atual da entidade pelo `id`. Isso evita vazamento de conteúdo por tombstone/feed.
- `entity.version` = `sync_sequence` da última mudança da entidade (INV-19). Servidor é o único atribuidor.
- Escrita automática: triggers `BEFORE` (`sync_stamp_*`: valida imutabilidade de `id/baby_id`, bloqueia alterar entidade com tombstone, limpa `notes` ao excluir, grava `version`/`updated_at`) e `AFTER` (`sync_log_change`: insere `change_log` e, em exclusão, `tombstone`). Tudo na mesma transação da mudança; triggers são `SECURITY DEFINER`, então `nina_app` não tem escrita direta em `change_log`, `tombstone` nem `baby_sync_head`.
- `entity_type` suportados: `baby`, `sleep_session`, `feeding_session`, `pumping_session`, `diaper_event`, `sleep_schedule_preference`. `caregiver_membership`, preferências de notificação e previsões **não** estão no feed (viajam por endpoints próprios/derivação).

### 6.2 Como evitar lacunas visíveis fora de ordem de commit **[P]**

Problema: com `SEQUENCE` global, a transação A pode obter 10, a B obter 11 e B comitar antes de A; um cliente que leu até 11 perde 10 para sempre.

Solução adotada: **contador serializado por bebê**, `baby_sync_head(baby_id, last_sequence, purged_through)`.
- Cada mutação executa `INSERT ... ON CONFLICT DO UPDATE SET last_sequence = last_sequence + 1 RETURNING` (função `nina.next_sync_sequence`). O `UPDATE` mantém **lock de linha até o commit/rollback**; escritores concorrentes do mesmo bebê são serializados.
- Consequências: (i) a ordem de numeração = ordem de commit; (ii) rollback desfaz o incremento, logo **não há lacunas** (contíguo `1..N`); (iii) um leitor que vê `sync_sequence = k` comitado vê todos os `< k`. O pull pode usar `WHERE sync_sequence > :cursor ORDER BY sync_sequence` sem horizonte de segurança.
- Custo: contenção por bebê, aceitável (2 a 3 cuidadores; transações curtas). Regra: uma transação que toque vários bebês deve ordená-los por `baby_id` (evita deadlock); o push típico é por bebê.
- Cursor opaco = mapa `{baby_id → sync_sequence}` serializado/assinado pela API (clientes não interpretam). Bebê novo no mapa começa em 0 (carga completa); bebê sem vínculo ativo é ignorado (INV-14).
- Teste de validação (seção 14): 8 conexões concorrentes com commits e rollbacks aleatórios geraram 1.223 mudanças com sequência contígua `1..1223`, sem inversão.

Alternativas descartadas, caso o spike ARCH-003 prefira sequência global: (A) `bigserial` global + leitura apenas até o menor `xmin` ativo (`pg_snapshot_xmin(pg_current_snapshot())`), com cursor limitado ao horizonte seguro; mais complexo e atrasa visibilidade; (B) `pg_advisory_xact_lock` global no commit, serializa todos os bebês. O ADR-0003 fala em `sync_sequence` "monotonicamente crescente" sem dizer global; a escolha por bebê é coerente com INV-19 (versão por bebê) — ver [Q2].

### 6.3 Tombstones e retenção de 90 dias [D]

- Exclusão = `UPDATE ... SET deleted_at = now()` (a API não faz `DELETE`; `nina_app` nem tem privilégio). O trigger cria `tombstone(baby_id, entity_type, entity_id, deleted_at, version, expires_at)`; `expires_at = deleted_at + sync.tombstone_retention_days` (parâmetro, padrão 90).
- Update tardio sobre entidade excluída → `NN002` (INV-20, RF-047-A4); a API traduz para "ignored_tombstone".
- A linha da entidade fica como tombstone (conteúdo livre limpo) até a purga.
- **Job de limpeza** `nina.purge_expired_sync_data(p_batch)` (executar diariamente pelo worker .NET, papel `nina_worker`; não depende de pg_cron). Idempotente, em lotes, com lock consultivo para evitar execuções concorrentes:
  1. Apaga `change_log` mais antigo que `sync.changelog_retention_days` (90) e eleva `baby_sync_head.purged_through` ao maior `sync_sequence` apagado por bebê.
  2. Remove a linha física da entidade e o `tombstone` **somente** quando `expires_at < now()` **e** `version <= purged_through` (o evento de delete já saiu do feed). Para `entity_type = 'baby'` apaga a casca do bebê (cascata remove vínculos e `baby_sync_head`).
  3. Apaga `sync_mutation` mais antigo que a janela.
- Cursor expirado: cliente com cursor `< purged_through` do bebê recebe "cursor expirado" e faz resync completo (API-001). Como `changed_at` usa `clock_timestamp()` dentro do lock, a ordem temporal acompanha a sequência por bebê.
- Rotina separada `nina.purge_expired_operational_data` aplica as demais retenções (sessões, recuperação, push inativo, previsões, convites vencidos, exports, outbox, jobs de notificação).

### 6.4 Mutações do cliente

`sync_mutation(mutation_id PK, baby_id, user_id, device_id, entity_type, entity_id, op, base_version, client_created_at, outcome, result_version)` registra o resultado de cada mutação sem payload. O push faz `INSERT ... ON CONFLICT (mutation_id) DO NOTHING`; conflito ⇒ já aplicada ⇒ responde o `result_version` guardado (INV-18). Conflitos resolvidos automaticamente geram `audit_event` `sync.conflict_resolved` (INV-21).

## 7. Entitlements, flags e parâmetros (ADR-0005) [D]

Tudo em tabelas, editável sem deploy, por `nina_config_admin`, e **toda alteração é auditada** (seção 8.3).

- `plan(id, code, rank, max_premium_members, limits jsonb)`. Seeds: `free` (rank 0, 1 membro) e `premium` (rank 1, **2 membros: titular + 1 adicional**).
- `feature_flag(flag_key, status)`: `gated` (padrão: depende de `plan_feature`; **bloqueada por padrão**), `open` (todos), `disabled` (ninguém, kill switch). Uma funcionalidade que pode ser premium nasce com linha `gated` e sem `plan_feature`; liberar para um plano = inserir `plan_feature(plan_id, flag_key, enabled, limit_value)`. Flag inexistente = bloqueado (`user_has_feature` retorna false).
- `app_parameter(param_key, value jsonb, value_type, schema_version, version, updated_by)`: parâmetros editáveis (política de idade corrigida D-01, tabelas de referência do motor de sono, limites). CHECK garante tipo JSON coerente; `version` incrementa a cada update. Seeds: `sync.tombstone_retention_days=90`, `sync.changelog_retention_days=90`, `prediction.retention_days=90`, `push.token_inactivity_days=60`, `privacy.owner_deletion_policy="block"`. As chaves da idade corrigida e das tabelas de sono **não** foram semeadas (D-01 e ADR-0004 abertos; serão adicionadas em migração do motor de sono).
- `subscription`: por família; `UNIQUE(store, original_transaction_ref)` (INV-26: restaurar reaproveita a assinatura). Sem dados de pagamento. Estados do domínio-model.
- `family_entitlement` (efetivo, derivado no servidor — INV-25): um vivo (`active|grace`) por família (índice único parcial); `source ∈ subscription | manual_grant`; janela `valid_from/valid_until`.
- `family_entitlement_member(entitlement_id, user_id, member_role holder|additional)`: trigger impõe (i) titular = `family.owner_user_id`; (ii) membros ativos ≤ `plan.max_premium_members` (premium: titular + 1 adicional); índice único: um titular ativo. Que o adicional seja cuidador de algum bebê da família **não** é imposto no banco (depende de dados mutáveis) — validar na API [Q4].
- Resolução: `nina.user_plan_code(user)` devolve o melhor plano vigente (`rank`) ou `free`; `nina.user_has_feature(user, flag)` aplica `status` + `plan_feature`. Clientes consultam via BFF e nunca decidem (RB-009).
- Quem edita em produção: papel `nina_config_admin`; o trigger exige `nina.user_id` (ator) na sessão e aceita `nina.change_reason` opcional. A lista de pessoas autorizadas é decisão de operação [Q5].

## 8. Privacidade e auditoria

### 8.1 Consentimentos (append-only) [D]

- `consent_purpose` (catálogo, semeado com `terms_of_use`, `privacy_policy`, `child_data_guardian`, `analytics_product`, `marketing_email`, `push_notifications`).
- `consent_record`: apenas INSERT. Colunas: `user_id`, `subject_baby_id` (sem FK, para não reter vínculo com criança excluída), `purpose_key`, `policy_version`, `text_hash` (SHA-256 hex), `locale`, `status ∈ granted | revoked`, `recorded_at`, `source`, `app_version`, `platform`. **Revogação = nova linha `revoked`** (segue privacy-spec 3.3; diverge da redação de INV-27 "adiciona `revoked_at`"; ver [Q6]). `superseded` foi omitido porque seria UPDATE; é derivado.
- `consent_current` (view `security_invoker`): último registro por `(user, bebê, finalidade)` via `DISTINCT ON ... seq DESC`.
- Imutabilidade: trigger `forbid_mutation` bloqueia UPDATE, DELETE e TRUNCATE (SQLSTATE `NN030`). DELETE só é aceito para um job de retenção futuro (papel `nina_worker` + `SET LOCAL nina.retention_purge='on'`), pensado para o fim do prazo probatório (ex.: 5 anos, a validar no jurídico). `nina_app` tem só SELECT/INSERT.
- Na exclusão de conta, consentimentos permanecem (prova mínima, F12) sem PII, pois `app_user` fica anonimizado.

### 8.2 Auditoria (append-only) [D]

- `audit_event`: `actor_user_id` (sem FK; pseudônimo após exclusão), `actor_type`, `action` (`[a-z0-9_.]`), `entity_type/entity_id`, `baby_id` (sem FK), `device_id`, `request_id`, `result`, `ip_hash` (SHA-256, nunca IP puro), `metadata_safe jsonb`.
- `metadata_safe`: objeto, ≤2 KB, e CHECK rejeita chaves de topo proibidas (`email`, `name`, `display_name`, `nickname`, `notes`, `password`, `token`, `birth_date`, `photo`). Não substitui revisão: a API monta metadados seguros por allowlist (INV-28).
- Eventos críticos (`is_critical`) formam **hash encadeado** (`chain_seq`, `prev_hash`, `row_hash = sha256(...)`), serializado por advisory lock (SEC-032). Verificação: reexecutar o hash em ordem de `chain_seq`.
- `nina_app` só INSERT (não lê auditoria); leitura por papel de auditoria/`nina_worker`. Retenção: 12 meses online, arquivo até 5 anos (a validar, DJ-06); remoção/arquivamento pelo mesmo mecanismo de retenção do item 8.1. Particionamento por mês fica como evolução quando houver volume [P].
- Cobertura mínima (SEC-030): login/falha, sessões, convite/aceite/revogação, mudança de papel, remoção de cuidador, transferência de propriedade, consentimento, export, exclusão, ações administrativas. O banco já grava: `baby.erased`, `account.erased`, `config.changed`.

### 8.3 Auditoria de configuração

`config_change(table_name, op, record_key, old_row, new_row, actor_user_id, reason)` (append-only) é preenchida por trigger em `plan`, `feature_flag`, `plan_feature` e `app_parameter`, além de um `audit_event` crítico `config.changed`. Sem ator na sessão a alteração é rejeitada (`NN040`), exceto durante migração (`nina.migration=on`).

### 8.4 DSAR

`data_export_request` (status, `file_ref` temporário, `expires_at` 7 dias, `schema_version`) e `account_deletion_request` (status `requested|scheduled|blocked|completed|cancelled`, `scheduled_for` para a janela de arrependimento D-07, `block_reason`). Um pedido de exclusão aberto por usuário (índice único parcial).

## 9. Isolamento por bebê — Row-Level Security (SEC-003)

- Contexto por transação: `SET LOCAL nina.user_id = '<uuid>'` (e opcionalmente `nina.device_id`) no início de cada transação (seguro com *connection pooling*; nunca `SET` de sessão). Sem contexto, nenhuma linha é visível.
- Funções `SECURITY DEFINER` com `search_path` fixo: `baby_role`, `can_read_baby`, `can_write_baby`, `is_family_owner`, `can_bootstrap_owner`.
- Políticas para `nina_app`:
  - Tabelas de bebê (`sleep_session`, `feeding_session`, `pumping_session`, `diaper_event`, `sleep_schedule_preference`, `sleep_prediction`): SELECT por membro ativo (qualquer papel); INSERT/UPDATE por `owner|caregiver` (INV-08, INV-13). `read_only` nunca escreve.
  - `baby`: SELECT por membro ou dono da família (necessário ao criar); INSERT apenas na própria família; UPDATE só por Owner [Q7].
  - `caregiver_membership`: usuário vê os próprios vínculos; Owner vê e gerencia os do bebê; autoinclusão como Owner só no bootstrap (bebê da própria família sem Owner).
  - `change_log`, `tombstone`, `sync_mutation`: leitura por membro; `sync_mutation` INSERT por quem escreve.
  - Tabelas por usuário (`notification_*`, `auth_session`, `device_push_token`, `consent_record`, `data_export_request`, `account_deletion_request`): `user_id = nina.user_id`.
- `nina_worker` tem política `USING (true)` própria (jobs de outbox, purga, exclusão operam em todos os bebês), em vez de `BYPASSRLS`, que exigiria superuser.
- Limites: tabelas de identidade (`app_user`, credenciais, identidade externa, recuperação, refresh) não têm RLS porque são consultadas antes da autenticação; protegem-se por privilégios e pela camada Identity. Como não usamos `FORCE ROW LEVEL SECURITY` (o helper definer precisa ler membership), o dono das tabelas ignora RLS: a aplicação **deve** usar `nina_app`.
- RLS é defesa em profundidade; a API continua filtrando por `baby_id` e verificando papel (SEC-001/003). Desempenho: política chama função STABLE com lookup por índice `(user_id, baby_id) WHERE status='active'`; medir no spike ARCH-003.

## 10. Exclusão em cascata (ADR-0008)

Princípio: **não apagar dados de outros cuidadores por padrão**. Funções executadas pelo worker (`nina_worker`); a API só registra o pedido (`account_deletion_request`) e autoriza.

- `nina.erase_baby(baby_id, actor)` (idempotente): apaga de imediato todo o conteúdo (eventos, preferências, previsões, jobs, mutações, feed e tombstones dos filhos), revoga vínculos remanescentes e transforma `baby` em **casca-tombstone** (campos nulos, `deleted_at`). O trigger gera `change_log` `delete` e `tombstone` do bebê; os dispositivos recebem o delete e apagam o banco local (privacy-spec 5.5). A casca some na purga (>= 90 dias, depois que o delete saiu do feed). Auditoria `baby.erased` + outbox `BabyDeleted`.
- `nina.erase_user(user_id, actor)` (idempotente), política `privacy.owner_deletion_policy`:
  - `block` (padrão e única implementada): **recusa** (`NN004`, `OWNER_HAS_OTHER_CAREGIVERS`) se o usuário é Owner ativo de bebê com outro membro ativo; a API marca o pedido `blocked` e pede transferência de propriedade (RB-007). Nenhum dado de outros cuidadores é tocado.
  - Outros valores (`transfer_oldest`, `delete_all`) levantam `NN005` (não implementado) até a decisão do **ponto aberto crítico do ADR-0008/DJ-09** [Q3].
  - Se aprovado: para bebês em que o usuário é o único membro ativo, executa `erase_baby`; remove seus vínculos como cuidador em bebês de terceiros (os eventos que ele criou **permanecem**, pois pertencem ao bebê; `created_by` continua apontando para a linha anonimizada); apaga credencial, identidades, sessões/refresh, tokens de push, recuperações, preferências e jobs de notificação; encerra membership de entitlement; zera `file_ref` de exports; anonimiza `app_user`; fecha o pedido; grava `account.erased` e outbox `AccountDeleted`.
  - Preservados após exclusão: `app_user` anonimizado, `consent_record`, `audit_event`, `subscription`/`family`/`family_entitlement` (obrigação fiscal; minimização a validar, D-07/DJ-06).
- FKs: dados de bebê `ON DELETE CASCADE` a partir de `baby`; `created_by`/`last_modified_by` `ON DELETE SET NULL`; consentimento e auditoria sem cascata (append-only).
- Backups: ciclo de 35 dias; restore deve reaplicar a fila de exclusões (SEC-064). Nada no banco implementa isso; é item de infraestrutura (CLOUD-001) [Q8].

## 11. Índices (resumo e justificativa)

| Índice | Uso |
|---|---|
| `change_log` PK `(baby_id, sync_sequence)` | pull incremental (`> cursor`) |
| `change_log (changed_at)` | purga por idade |
| `tombstone (expires_at)` | purga |
| `sleep_one_open_uq` parcial | INV-02 + "sessão em aberto" |
| `*_timeline_ix (baby_id, start_at DESC) WHERE deleted_at IS NULL` | timeline e gráficos |
| `membership (user_id, baby_id) WHERE status='active'` | RLS e listagem de bebês |
| `membership_one_owner_uq`, `membership_baby_user_uq` | INV-09, INV-10 |
| `app_user_email_uq (email_normalized)` | login e unicidade |
| `notification_job.event_key` único, `(scheduled_at) WHERE status IN (scheduled, failed)` | idempotência (INV-22) e varredura do scheduler |
| `outbox_pending_ix (available_at, id) WHERE pendente` | consumo com `FOR UPDATE SKIP LOCKED` |
| `consent (user_id, purpose_key, seq DESC)` | estado corrente |
| `audit (occurred_at)`, `(actor_user_id, occurred_at)`, `(baby_id, occurred_at)` | consulta e retenção |

## 12. Migração e operação

- Arquivo único e transacional `0001_init.sql`; registra `schema_migration('0001')`. Não é re-executável (sem `IF NOT EXISTS`); o runner de migração (ARCH-001) deve controlar versões. O arquivo contém `BEGIN/COMMIT`; se o runner já abre transação, remover as duas linhas.
- A criação de papéis ocorre na migração com guarda `IF NOT EXISTS`; em nuvem gerenciada, a infra pode criar os papéis antes.
- Agendamentos (worker .NET): `purge_expired_sync_data` diário em loop até retornar zeros; `purge_expired_operational_data` diário; outbox contínuo.
- Códigos de erro customizados (SQLSTATE classe `NN`): `NN001` id/baby imutável, `NN002` tombstone, `NN003` bebê excluído, `NN004` owner com outros cuidadores, `NN005` política não implementada, `NN010` bebê sem Owner, `NN020/021` entitlement, `NN030` append-only, `NN040` ator ausente.

## 13. Perguntas e pontos abertos

- **[Q1]** Confirmar a entidade `family` (1 por titular) e que bebê pertence à família do Owner; na transferência de propriedade, `baby.family_id` muda para a família do novo Owner?
- **[Q2]** `sync_sequence` pode ser **por bebê** (esta spec) em vez de global? Cursor vira mapa por bebê. Validar no spike ARCH-003 (contenção, tamanho do cursor).
- **[Q3]** ADR-0008/DJ-09 (crítico): opções (a) excluir tudo, (b) bloquear até transferir, (c) excluir só o vínculo. Implementado só (b) como padrão seguro; (a) e (c) dependem do jurídico e exigem aviso/confirmação explícitos.
- **[Q4]** "Titular + 1 adicional" = 2 membros com premium (assumido). O adicional precisa ser cuidador ativo de bebê da família? E se o titular cancelar: hoje o entitlement expira para todos (sem herança). Preço fora do escopo do banco.
- **[Q5]** Quem pode usar `nina_config_admin` em produção, aprovação em dois olhos e ferramenta de edição (ADR-0005).
- **[Q6]** INV-27 (domain-model) diz "adiciona `revoked_at`"; privacy-spec 3.3 diz "append-only, estado derivado do último registro". Adotada a segunda; alinhar o domain-model.
- **[Q7]** Quem edita o perfil do bebê: só Owner (assumido) ou também Caregiver?
- **[Q8]** Restore de backup e reaplicação da fila de exclusões; janela de arrependimento (D-07) e prazos de retenção de consentimento/auditoria/fiscal (DJ-06).
- **[Q9]** Dúvidas herdadas do domain-model que ainda moldam colunas: D-05 (sobreposição de sono), D-08 (tipos de fralda e método/local de sono hoje texto curto), D-09 (foto: `photo_ref` apenas), D-11 (perfil pré-natal: `birth_date` é NOT NULL), D-12/D-13 (despertares noturnos, corte do dia).
- **[Q10]** Cifra em nível de campo (`display_name`, `notes`, `birth_date`, token de push) é recomendada (SEC-021) mas não implementada aqui; decidir por ADR (impacta busca/índices e RLS).
- **[Q11]** Fora do MVP, não modeladas: fases de desenvolvimento, diário/mídia, conteúdo, IA, `DsarRequest` genérico (apenas export e exclusão).

## 14. Validação executada

Em PostgreSQL 16.15 local (cluster temporário; papéis `nina_app`, `nina_worker`, `nina_config_admin` com `SET ROLE`):
- A migração roda do zero sem erro, repetida em bancos novos.
- Funcional: criação de bebê e bootstrap de Owner via RLS; Eve (sem vínculo) não vê nem escreve; `read_only` lê e não escreve; sem contexto nada é visível; usuário não se autoadiciona a bebê alheio; segunda sessão de sono aberta falha; formas inválidas de `feeding_session` e fuso desconhecido falham; exclusão gera tombstone de 90 dias e limpa `notes`; ressurreição é bloqueada; bebê sem Owner falha no commit; segundo Owner falha; terceiro membro premium e titular errado falham; flag `gated` bloqueada até `plan_feature`, titular e adicional recebem, usuário free não; alteração de configuração sem ator falha e com ator grava `config_change` e `audit_event`; UPDATE/DELETE/TRUNCATE em `audit_event`/`consent_record` falham; DELETE por worker sem flag falha; cadeia de hash de auditoria sem quebras; `nina_app` não escreve em `change_log` nem lê `baby_sync_head`/auditoria.
- Exclusão: `erase_user` do Owner com outro cuidador ativo é recusado; cuidador excluído mantém os eventos do bebê; Owner único gera casca-tombstone sem PII e remove os filhos.
- Purga: com relógio simulado em +100 dias, `purge_expired_sync_data` poda o feed, sobe `purged_through`, remove tombstone e casca do bebê.
- Concorrência (`pgbench`, 8 clientes, commits e rollbacks): sequência `1..1223` contígua, sem inversão temporal.
Não validado: carga/desempenho de RLS em volume, restore de backup, PostgreSQL 15 e agendamento real do worker.
