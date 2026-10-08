# Especificação de Banco de Dados — Nina (DB-001)

Status: rascunho para revisão (ARCH-001/ARCH-003/API-001) · Data: 2026-10-08 · Atualizada com os ADR-0009 e ADR-0010
Fontes: `specs/domain-model.md`, `specs/product-spec.md`, `specs/privacy-security-spec.md`, ADR-0003, 0005, 0007, 0008, 0009, 0010, `docs/project/architecture.md`.
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
| Tracking | `sleep_session`, `wake_event`, `feeding_session`, `pumping_session`, `diaper_event` |
| SleepIntelligence | `sleep_schedule_preference`, `sleep_prediction` |
| Sync | `baby_sync_head`, `change_log`, `tombstone`, `sync_mutation` |
| Notifications | `notification_preference`, `notification_job`, `outbox_message` |
| Subscriptions / Config (ADR-0005) | `plan`, `feature_flag`, `plan_feature`, `app_parameter`, `subscription`, `family_entitlement`, `family_entitlement_member`, `config_change` |
| Privacy | `consent_purpose`, `consent_record` (+ view `consent_current`), `audit_event`, `data_export_request`, `account_deletion_request`, `privacy_request` (ADR-0010) |
| Infra | `schema_migration` |

Papéis de banco (NOLOGIN; a infra concede membership aos logins): `nina_app` (API/BFF, sujeito a RLS), `nina_worker` (jobs, outbox, purga, exclusão), `nina_config_admin` (edição de flags/planos/parâmetros). A aplicação não pode conectar como dono das tabelas nem superuser (ambos ignoram RLS).

## 3. Identity

- `app_user`: `email` + `email_normalized` (coluna gerada `lower(btrim(email))`, único parcial). Estados `ACTIVE | PENDING_DELETION | DELETED` (ADR-0010: enums em MAIÚSCULAS). Após exclusão, a linha **permanece anonimizada** (email/locale/timezone nulos, `deleted_at`) para manter integridade de consentimento, auditoria e registros fiscais sem reter PII. O CHECK `app_user_email_ck` impõe isso.
- `user_credential`: hash adaptativo (`argon2id`/`bcrypt`), 1:1 com usuário. Nunca logado.
- `user_identity`: Google/Apple, único por `(provider, provider_subject)`. **Não há fusão automática por e-mail** (ADR-0007); a vinculação é um INSERT explícito após confirmação.
- `auth_session` (RF-054): dispositivo, plataforma, `absolute_expires_at` (30 dias), `revoked_at/reason`. `refresh_token`: hash, uso único (`used_at`), rotativo; reuso de token já usado → revogar a sessão (`reuse_detected`) — lógica na API, dado no banco.
- `recovery_request`: hash do token, expira, uso único. `device_push_token`: único por `(platform, token)` e por `(user, device, platform)`.
- Revogação de acesso a um bebê (INV-14, RB-015) **não** exige revogar tokens: o acesso é recalculado a cada requisição via `caregiver_membership` (RLS). Remover o vínculo corta API e pull de sync imediatamente; o cliente descarta dados locais ao receber 403/404 uniforme.

## 4. Family e vínculos

- `family` **[P]**: agrupador de entitlement (ADR-0005: "o entitlement pertence à família"). Uma família por titular (`owner_user_id` único), criada no cadastro. O domain-model não tem essa entidade (D-04 resolvida pelo ADR-0005); ver [Q1].
- `baby`: `family_id`, `display_name`, `birth_date`, `due_date` (independentes, INV-15), `sex`, `timezone`, `photo_ref`. É sincronizável (colunas `version/created_at/updated_at/deleted_at/created_by/last_modified_by`). `birth_date` no futuro é rejeitada por trigger (INV-16; perfil pré-natal é D-11). Quando `deleted_at` não é nulo, os campos de conteúdo **devem** ser nulos (CHECK `baby_content_ck`): é a "casca-tombstone" sem PII.
- `caregiver_membership`: convite = linha `PENDING` (sem entidade Invitation). `role ∈ OWNER | CAREGIVER | READ_ONLY`, `status ∈ PENDING | ACTIVE | REVOKED | DECLINED`. Convite guarda apenas o **hash** do token (≥128 bits gerados na API), `invite_expires_at` (7 dias).
  - INV-10: índice único parcial `(baby_id, user_id)` para `pending|active`.
  - INV-09 (no máximo um): índice único parcial de Owner ativo por bebê. (Pelo menos um): *constraint trigger* `DEFERRABLE INITIALLY DEFERRED` exige Owner ativo ao fim da transação para bebê não excluído (SEC-007). Transferir propriedade = rebaixar e promover na mesma transação.
  - O dono (`Owner`) é derivado do vínculo; não há `baby.owner_id` redundante [P] (evita duas fontes de verdade).

## 5. Tracking

Colunas comuns de sync: `id` (UUID do cliente), `baby_id`, `version`, `created_at`, `updated_at`, `deleted_at`, `created_by`, `last_modified_by`. `version` é **sempre** atribuída por trigger (seção 6); valor enviado pelo cliente é ignorado. Eventos guardam instante UTC + `tz` (RB-014).

| Tabela | Constraints principais |
|---|---|
| `sleep_session` | `end_at >= start_at` (INV-01); índice único parcial: no máximo **uma sessão aberta por bebê** (INV-02) `WHERE end_at IS NULL AND deleted_at IS NULL`; `sleep_type ∈ NAP|NIGHT`; `source ∈ TIMER|MANUAL`; `method_or_place` texto ≤60 (D-08); `UNIQUE (id, baby_id)` (alvo da FK composta de `wake_event`); sobreposição conforme a flag `sleep.overlap_policy` (abaixo) |
| `wake_event` **[D, ADR-0009]** | Despertar dentro de uma sessão de sono; sincronizável (`version`, `deleted_at`, `change_log`, tombstone, RLS por bebê). Ver 5.1 |
| `feeding_session` | `feeding_type ∈ BREASTFEEDING|BOTTLE|SOLID|OTHER`; CHECK de forma (INV-07): `BREASTFEEDING` exige `side` e **`end_at` obrigatório** (ADR-0010 item 5; verificado: `feeding_shape_ck` rejeita inserção e UPDATE com `end_at` nulo) e proíbe `volume_ml`/`milk_type`; `BOTTLE` proíbe `side`; `SOLID` proíbe `side`/`volume_ml`/`milk_type`; `OTHER` proíbe `side`/`milk_type`; `side ∈ LEFT|RIGHT|BOTH`; `volume_ml` em (0, 5000]; `milk_type ∈ BREAST_MILK|FORMULA|MIXED|OTHER|UNSPECIFIED|UNKNOWN` **somente quando `BOTTLE`** (nos demais, NULL) |
| `pumping_session` | `end_at >= start_at`; `volume_ml` > 0; `side ∈ LEFT|RIGHT|BOTH` opcional |
| `diaper_event` | `occurred_at`; `diaper_type ∈ WET|DIRTY|MIXED|DRY|UNSPECIFIED` (WET urina, DIRTY fezes, MIXED ambos, DRY verificada sem nada) |
| `sleep_schedule_preference` | 1 por bebê (`baby_id` único); sincronizável; metas em hora local |
| `sleep_prediction` | **Derivada, não sincronizada** (INV-05); `confidence` 0..1, `explanation_key` + `explanation_params` (localizável), `model_version`, `inputs_version`; retenção 90 dias |

### 5.1 Convenções do ADR-0009 no banco

- **Enums como `text` + CHECK** (não `CREATE TYPE ... ENUM`): acrescentar valor é só trocar o CHECK (clientes devem tolerar valores desconhecidos). **Todos os valores fixos em MAIÚSCULAS (ADR-0010 item 2), sem mapeamento na API**: `sleep_type` (`NAP|NIGHT`), `sex` (`FEMALE|MALE|UNSPECIFIED`), `source` de sono (`TIMER|MANUAL`) e de despertar, papéis (`OWNER|CAREGIVER|READ_ONLY`), `status` de vínculo/usuário/assinatura/entitlement/pedidos/jobs, `platform`, `provider`, `store`, `category`, `kind`, `op`/`entity_type` do change log (`UPSERT|DELETE`; `BABY|SLEEP_SESSION|FEEDING_SESSION|PUMPING_SESSION|DIAPER_EVENT|WAKE_EVENT|SLEEP_SCHEDULE_PREFERENCE`), `status` de flag (`GATED|OPEN|DISABLED`), `value_type` de parâmetro (`INT|BOOL|STRING|JSON`), `actor_type`/`result` de auditoria, e **valores das flags de política** (`CASCADE|BLOCK|TRANSFER_OWNERSHIP`, `ACCEPT_AND_WARN|REJECT`). Triggers, funções, RLS e seeds foram ajustados. **Não são enums (ficam em minúsculas)**: chaves de catálogo e identificadores (`plan.code` = `free|premium`, `flag_key`, `param_key`, `purpose_key`, `audit_event.action`, `explanation_key`), nomes de tabela e texto livre de `audit_event.entity_type` (hoje também em MAIÚSCULAS nos eventos do banco, sem CHECK). Migração de valores antigos não se aplica: a migração é inicial (nada em produção).
- **Nulos**: `NULL` = não se aplica ou indisponível; `0` = zero conhecido; `UNSPECIFIED` = usuário não especificou; `UNKNOWN` = deveria existir e o sistema não sabe (reservado a migração de dados antigos; só `milk_type` o aceita hoje). Em `BOTTLE`, `milk_type NULL` é aceito (= indisponível); o CHECK só impõe que fora de `BOTTLE` seja NULL.
- **Idade corrigida nunca é persistida**: não há coluna para ela (só `baby.birth_date` e `baby.due_date`, independentes — INV-15). `nina.age_calculation(birth, due, on)` devolve `(chronological_days, corrected_days, correction_applied)`: `corrected = cronológica − (due − birth)` apenas se `due` não nulo, `birth < due` e a idade cronológica está dentro de `age.corrected_window_months`; caso contrário `corrected_days = NULL` e `correction_applied = false`. Antes do termo o valor corrigido pode ser negativo (não é truncado).
- **`wake_event`**: `id` (uuid do cliente), `baby_id`, `sleep_session_id` (FK composta `(sleep_session_id, baby_id)` → `sleep_session(id, baby_id)`, impede apontar sessão de outro bebê), `started_at`, `ended_at` (nulo = em curso), `tz`, `duration_seconds` (**coluna gerada** a partir de `started_at/ended_at`, nula sem `ended_at`; nunca diverge), `source ∈ MANUAL|INFERRED|IMPORT`, e correção manual: `manually_corrected` + `original_started_at/original_ended_at` (preservam o valor inferido/importado antes do ajuste; só preenchíveis se `manually_corrected`). Excluir (soft delete) uma `sleep_session` exclui seus `wake_event` por trigger, gerando tombstones para os dispositivos. Que o despertar caiba dentro da sessão **não** é imposto no banco (edição offline); validar na API.
- **`nightAwakenings` é derivado** por `nina.night_awakenings(sleep_session_id)`: `NULL` = dados insuficientes (inclui soneca, sessão inexistente/aberta/curta sem despertares); `0` = acompanhamento suficiente e nenhum despertar; `N>0` = despertares registrados (havendo registro, há evidência, então o número é devolvido). "Suficiente" = sessão `NIGHT` encerrada com duração ≥ `sleep.night_awakenings.min_session_minutes` (parâmetro editável, ver seção 7).

Decisões e lacunas:
- **Sobreposição de sono (INV-03/D-05, ADR-0009)**: política em `app_parameter` `sleep.overlap_policy` — `ACCEPT_AND_WARN` (**padrão**: aceita; a API sinaliza usando `nina.sleep_overlaps(session_id)`) ou `REJECT` (trigger `sleep_session_zz_overlap` recusa com `NN006`). A checagem roda após o carimbo de sync, que já segura o lock do contador do bebê, então escritores concorrentes são serializados. Sessão aberta conta como intervalo até o infinito; intervalos adjacentes (fim = início) não se sobrepõem; atualização que não muda `start_at/end_at` não é re-julgada (dados aceitos sob a política anterior continuam editáveis). Uma `EXCLUDE` com `btree_gist` não é usada para manter a política editável sem migração.
- Duração e wake window nunca são persistidas (INV-04).
- Dois dispositivos offline iniciando sono geram violação do índice de sessão aberta no push: a API trata como conflito (resolução LWW por campo + auditoria, ADR-0003; a validar no spike ARCH-003).
- Índices de linha do tempo: `(baby_id, start_at DESC) WHERE deleted_at IS NULL` (e `occurred_at` na fralda).

## 6. Sincronização (ADR-0003)

### 6.1 Change log e versão

- `change_log (baby_id, sync_sequence, entity_type, entity_id, op, actor_user_id, device_id, changed_at)`, PK `(baby_id, sync_sequence)`. Não contém conteúdo (apenas "qual entidade mudou e se foi upsert/delete"); o pull lê o estado atual da entidade pelo `id`. Isso evita vazamento de conteúdo por tombstone/feed.
- `entity.version` = `sync_sequence` da última mudança da entidade (INV-19). Servidor é o único atribuidor.
- Escrita automática: triggers `BEFORE` (`sync_stamp_*`: valida imutabilidade de `id/baby_id`, bloqueia alterar entidade com tombstone, limpa `notes` ao excluir, grava `version`/`updated_at`) e `AFTER` (`sync_log_change`: insere `change_log` e, em exclusão, `tombstone`). Exceção única (ADR-0010): com `nina.authorship_scrub='on'` (só `scrub_user_personal_data`, seção 10) os ponteiros `created_by/last_modified_by` do usuário-alvo são anulados **sem** nova `version` e **sem** `change_log` (autoria não faz parte do contrato; vale também para tombstones). Tudo na mesma transação da mudança; triggers são `SECURITY DEFINER`, então `nina_app` não tem escrita direta em `change_log`, `tombstone` nem `baby_sync_head`.
- `entity_type` suportados (MAIÚSCULAS): `BABY`, `SLEEP_SESSION`, `FEEDING_SESSION`, `PUMPING_SESSION`, `DIAPER_EVENT`, `WAKE_EVENT`, `SLEEP_SCHEDULE_PREFERENCE`; `op` = `UPSERT|DELETE`. `caregiver_membership`, preferências de notificação e previsões **não** estão no feed (viajam por endpoints próprios/derivação).

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
  2. Remove a linha física da entidade e o `tombstone` **somente** quando `expires_at < now()` **e** `version <= purged_through` (o evento de delete já saiu do feed). Para `entity_type = 'BABY'` apaga a casca do bebê (cascata remove vínculos e `baby_sync_head`).
  3. Apaga `sync_mutation` mais antigo que a janela.
- Cursor expirado: cliente com cursor `< purged_through` do bebê recebe "cursor expirado" e faz resync completo (API-001). Como `changed_at` usa `clock_timestamp()` dentro do lock, a ordem temporal acompanha a sequência por bebê.
- Rotina separada `nina.purge_expired_operational_data` aplica as demais retenções (sessões, recuperação, push inativo, previsões, convites vencidos, exports, outbox, jobs de notificação).

### 6.4 Mutações do cliente

`sync_mutation(mutation_id PK, baby_id, user_id, device_id, entity_type, entity_id, op, base_version, client_created_at, outcome, result_version)` registra o resultado de cada mutação sem payload. O push faz `INSERT ... ON CONFLICT (mutation_id) DO NOTHING`; conflito ⇒ já aplicada ⇒ responde o `result_version` guardado (INV-18). Conflitos resolvidos automaticamente geram `audit_event` `sync.conflict_resolved` (INV-21). `op ∈ CREATE|UPDATE|DELETE`, `outcome ∈ APPLIED|MERGED|IGNORED_TOMBSTONE|REJECTED`.

## 7. Entitlements, flags e parâmetros (ADR-0005) [D]

Tudo em tabelas, editável sem deploy, por `nina_config_admin`, e **toda alteração é auditada** (seção 8.3).

- `plan(id, code, rank, max_premium_members, limits jsonb)`. Seeds (`code` é chave de catálogo, minúsculo): `free` (rank 0, 1 membro) e `premium` (rank 1, **2 membros: titular + 1 adicional**).
- `feature_flag(flag_key, status)`: `GATED` (padrão: depende de `plan_feature`; **bloqueada por padrão**), `OPEN` (todos), `DISABLED` (ninguém, kill switch). Uma funcionalidade que pode ser premium nasce com linha `GATED` e sem `plan_feature`; liberar para um plano = inserir `plan_feature(plan_id, flag_key, enabled, limit_value)`. Flag inexistente = bloqueado (`user_has_feature` retorna false).
- `app_parameter(param_key, value jsonb, value_type, schema_version, version, updated_by)`: parâmetros editáveis (política de idade corrigida D-01, tabelas de referência do motor de sono, limites). CHECK garante tipo JSON coerente; `version` incrementa a cada update. Seeds: `sync.tombstone_retention_days=90`, `sync.changelog_retention_days=90`, `prediction.retention_days=90`, `push.token_inactivity_days=60` e, do ADR-0009:

| Chave | Tipo | Padrão | Significado |
|---|---|---|---|
| `privacy.owner_deletion_policy` | string | `"CASCADE"` | `CASCADE` \| `BLOCK` \| `TRANSFER_OWNERSHIP` (seção 10) |
| `sleep.overlap_policy` | string | `"ACCEPT_AND_WARN"` | `ACCEPT_AND_WARN` \| `REJECT` (seção 5.1) |
| `age.corrected_window_months` | int | `24` | janela (idade cronológica, em meses) de aplicação da idade corrigida. **Padrão aceito [D, ADR-0010 item 4]**, editável (validado: 1 a 120) |
| `sleep.night_awakenings.min_session_minutes` | int | `240` | duração mínima de sessão noturna encerrada para o acompanhamento ser "suficiente" (0 em vez de nulo). **Padrão aceito [D, ADR-0010 item 4]**, editável (validado: 0 a 1440) |
| `privacy.deletion_grace_days` | int | `7` | **[D, ADR-0010 item 6]** janela de arrependimento da exclusão de conta, em dias; `scheduled_for = requested_at + valor`. Validado: inteiro de 1 a 30 (seção 8.4/10) |
| `privacy.request_response_days` | int | `15` | prazo de atendimento de requisição de privacidade (`privacy_request.due_at`; privacy-spec §5, validar DJ-07). Validado: inteiro de 1 a 60 |

O trigger `validate_app_parameter` rejeita valores inválidos dessas chaves (SQLSTATE `23514`; inclui valores de política em minúsculas, número fracionário/negativo e tipo errado), então uma edição errada nunca chega à produção. A chave de exclusão manteve o nome `privacy.owner_deletion_policy` (já existente); só o padrão mudou de `block` para `cascade`. As tabelas de referência do motor de sono (ADR-0004) continuam fora do seed.
- `subscription`: por família; `UNIQUE(store, original_transaction_ref)` (INV-26: restaurar reaproveita a assinatura). Sem dados de pagamento. Estados do domínio-model.
- `family_entitlement` (efetivo, derivado no servidor — INV-25): um vivo (`active|grace`) por família (índice único parcial); `source ∈ SUBSCRIPTION | MANUAL_GRANT`; janela `valid_from/valid_until`.
- `family_entitlement_member(entitlement_id, user_id, member_role HOLDER|ADDITIONAL)`: trigger impõe (i) titular = `family.owner_user_id`; (ii) membros ativos ≤ `plan.max_premium_members` (premium: titular + 1 adicional); índice único: um titular ativo. **O adicional não precisa ser cuidador ativo de nenhum bebê** (ADR-0009): o banco não impõe nem a API deve exigir vínculo; qualquer usuário pode ser o adicional, respeitado o limite de membros do plano.
- Resolução: `nina.user_plan_code(user)` devolve o melhor plano vigente (`rank`) ou `free`; `nina.user_has_feature(user, flag)` aplica `status` + `plan_feature`. Clientes consultam via BFF e nunca decidem (RB-009).
- Quem edita em produção: papel `nina_config_admin`; o trigger exige `nina.user_id` (ator) na sessão e aceita `nina.change_reason` opcional. A lista de pessoas autorizadas é decisão de operação [Q5].

## 8. Privacidade e auditoria

### 8.1 Consentimentos (append-only) [D]

- `consent_purpose` (catálogo, semeado com `terms_of_use`, `privacy_policy`, `child_data_guardian`, `analytics_product`, `marketing_email`, `push_notifications`).
- `consent_record`: apenas INSERT. Colunas: `user_id`, `subject_baby_id` (sem FK, para não reter vínculo com criança excluída), `purpose_key`, `policy_version`, `text_hash` (SHA-256 hex), `locale`, `status ∈ GRANTED | REVOKED`, `recorded_at`, `source ∈ ONBOARDING|SETTINGS|PROMPT|INVITE_ACCEPT|SYSTEM`, `app_version`, `platform`. **Revogação = nova linha `REVOKED`** (segue privacy-spec 3.3; diverge da redação de INV-27 "adiciona `revoked_at`"; ver [Q6]). `superseded` foi omitido porque seria UPDATE; é derivado.
- `consent_current` (view `security_invoker`): último registro por `(user, bebê, finalidade)` via `DISTINCT ON ... seq DESC`.
- Imutabilidade: trigger `forbid_mutation` bloqueia UPDATE, DELETE e TRUNCATE (SQLSTATE `NN030`). DELETE só é aceito para um job de retenção futuro (papel `nina_worker` + `SET LOCAL nina.retention_purge='on'`), pensado para o fim do prazo probatório (ex.: 5 anos, a validar no jurídico). `nina_app` tem só SELECT/INSERT.
- Na exclusão de conta e na eliminação de privacidade, os consentimentos ainda concedidos ganham uma linha `REVOKED` (`source = SYSTEM`, append-only; o tratamento cessa) e o histórico permanece como prova mínima (F12: ids, finalidade, versão, datas) ligado a um `app_user` anonimizado, sem PII. Validação jurídica do prazo probatório: DJ-06.

### 8.2 Auditoria (append-only) [D]

- `audit_event`: `actor_user_id` (sem FK; pseudônimo após exclusão), `actor_type`, `action` (`[a-z0-9_.]`), `entity_type/entity_id`, `baby_id` (sem FK), `device_id`, `request_id`, `result`, `ip_hash` (SHA-256, nunca IP puro), `metadata_safe jsonb`.
- `metadata_safe`: objeto, ≤2 KB, e CHECK rejeita chaves de topo proibidas (`email`, `name`, `display_name`, `nickname`, `notes`, `password`, `token`, `birth_date`, `photo`). Não substitui revisão: a API monta metadados seguros por allowlist (INV-28).
- Eventos críticos (`is_critical`) formam **hash encadeado** (`chain_seq`, `prev_hash`, `row_hash = sha256(...)`), serializado por advisory lock (SEC-032). Verificação: reexecutar o hash em ordem de `chain_seq`.
- `nina_app` só INSERT (não lê auditoria); leitura por papel de auditoria/`nina_worker`. Retenção: 12 meses online, arquivo até 5 anos (a validar, DJ-06); remoção/arquivamento pelo mesmo mecanismo de retenção do item 8.1. Particionamento por mês fica como evolução quando houver volume [P].
- Cobertura mínima (SEC-030): login/falha, sessões, convite/aceite/revogação, mudança de papel, remoção de cuidador, transferência de propriedade, consentimento, export, exclusão, ações administrativas. O banco já grava: `baby.erased`, `account.erased`, `config.changed`.

### 8.3 Auditoria de configuração

`config_change(table_name, op, record_key, old_row, new_row, actor_user_id, reason)` (append-only) é preenchida por trigger em `plan`, `feature_flag`, `plan_feature` e `app_parameter`, além de um `audit_event` crítico `config.changed`. Sem ator na sessão a alteração é rejeitada (`NN040`), exceto durante migração (`nina.migration=on`).

### 8.4 DSAR e exclusão de conta (ADR-0009 + ADR-0010)

- `data_export_request` (status, `file_ref` temporário, `expires_at` 7 dias, `schema_version`).
- `account_deletion_request` **(só Owner ativo de um bebê, ADR-0010 item 3)**: `status ∈ SCHEDULED | BLOCKED | COMPLETED | CANCELLED`. O trigger `account_deletion_guard` fixa na inserção `requested_at = now()`, `grace_days = privacy.deletion_grace_days` (congelado no pedido: mudar o parâmetro depois não altera pedidos em curso), **`scheduled_for = requested_at + grace_days`** e `status = SCHEDULED` (valores enviados pelo cliente são ignorados). `id/user_id/requested_at/grace_days/scheduled_for/confirmed_at/confirmation_method` são imutáveis (`NN001`). Cancelamento (`SCHEDULED|BLOCKED → CANCELLED`) é permitido ao próprio usuário **somente antes de `scheduled_for`** (`NN011` depois; um `BLOCKED` pode ser cancelado a qualquer tempo); `COMPLETED` antes de `scheduled_for` é recusado (`NN008`), até para superusuário; estados finais são terminais. `confirmed_at` + `confirmation_method='REAUTHENTICATION'` (par obrigatório por CHECK) registram a confirmação explícita feita na criação; `policy_applied` registra a política usada ao concluir. Um pedido aberto (`SCHEDULED|BLOCKED`) por usuário (índice único parcial). Auditoria crítica: `account.deletion_requested` e `account.deletion_cancelled`.
- `privacy_request` **(ADR-0010 item 3; caminho do não-Owner e demais direitos do titular)**: `request_type ∈ ACCESS | CORRECTION | EXPORT | ERASURE`, `status ∈ REQUESTED | COMPLETED | REJECTED | CANCELLED`, `due_at = requested_at + privacy.request_response_days` (15), `identity_verified_at` + `verification_method='REAUTHENTICATION'` (par por CHECK; **obrigatório em `ERASURE`**), `closed_reason` (código curto, sem PII). Um pedido aberto por `(usuário, tipo)`. `nina_app` só **lê** os próprios pedidos; abre/cancela por `nina.open_privacy_request` / `nina.cancel_privacy_request` (`SECURITY DEFINER`, auditam `privacy.request_opened` / `privacy.request_cancelled`, críticos). O worker encerra `ACCESS/CORRECTION/EXPORT` com `nina.close_privacy_request` (`privacy.request_closed`, com `on_time`) e cumpre `ERASURE` com `nina.fulfill_privacy_erasure` (seção 10.1). Substitui, no banco, a necessidade do `DsarRequest` genérico para esses tipos.

## 9. Isolamento por bebê — Row-Level Security (SEC-003)

- Contexto por transação: `SET LOCAL nina.user_id = '<uuid>'` (e opcionalmente `nina.device_id`) no início de cada transação (seguro com *connection pooling*; nunca `SET` de sessão). Sem contexto, nenhuma linha é visível.
- Funções `SECURITY DEFINER` com `search_path` fixo: `baby_role`, `can_read_baby`, `can_write_baby`, `is_family_owner`, `can_bootstrap_owner`, `can_request_account_deletion`, `open_privacy_request`, `cancel_privacy_request` (e os triggers de sync/auditoria). `baby_role` devolve `OWNER|CAREGIVER|READ_ONLY`.
- Políticas para `nina_app`:
  - Tabelas de bebê (`sleep_session`, `wake_event`, `feeding_session`, `pumping_session`, `diaper_event`, `sleep_schedule_preference`, `sleep_prediction`): SELECT por membro ativo (qualquer papel); INSERT/UPDATE por `OWNER|CAREGIVER` (INV-08, INV-13). `READ_ONLY` nunca escreve.
  - `baby`: SELECT por membro ou dono da família (necessário ao criar); INSERT apenas na própria família; **UPDATE (editar perfil, inclusive excluir) somente pelo Owner do bebê** (ADR-0009). `CAREGIVER` e `READ_ONLY` não editam o perfil.
  - `account_deletion_request` **(ADR-0009/0010: só o Owner exclui a conta)**: INSERT só para o próprio usuário **e** se `nina.can_request_account_deletion()` = `nina.is_active_baby_owner(usuário)`: Owner ativo de ao menos um bebê **vivo**. Foi **removida a permissão para quem não tem vínculo ativo**; `CAREGIVER`, `READ_ONLY` e usuário sem vínculo usam `privacy_request` (seção 8.4). UPDATE pelo próprio usuário só para `CANCELLED` (`USING status IN (SCHEDULED,BLOCKED)` / `WITH CHECK status='CANCELLED'`; janela e imutabilidade no trigger). `privacy_request`: SELECT do próprio usuário; sem INSERT/UPDATE direto.
  - `caregiver_membership`: usuário vê os próprios vínculos; Owner vê e gerencia os do bebê; autoinclusão como Owner só no bootstrap (bebê da própria família sem Owner).
  - `change_log`, `tombstone`, `sync_mutation`: leitura por membro; `sync_mutation` INSERT por quem escreve.
  - Tabelas por usuário (`notification_*`, `auth_session`, `device_push_token`, `consent_record`, `data_export_request`, `account_deletion_request`, `privacy_request`): `user_id = nina.user_id` (as regras adicionais de `account_deletion_request` e `privacy_request` estão acima).
- `nina_worker` tem política `USING (true)` própria (jobs de outbox, purga, exclusão operam em todos os bebês), em vez de `BYPASSRLS`, que exigiria superuser.
- Limites: tabelas de identidade (`app_user`, credenciais, identidade externa, recuperação, refresh) não têm RLS porque são consultadas antes da autenticação; protegem-se por privilégios e pela camada Identity. Como não usamos `FORCE ROW LEVEL SECURITY` (o helper definer precisa ler membership), o dono das tabelas ignora RLS: a aplicação **deve** usar `nina_app`.
- RLS é defesa em profundidade; a API continua filtrando por `baby_id` e verificando papel (SEC-001/003). Desempenho: política chama função STABLE com lookup por índice `(user_id, baby_id) WHERE status='active'`; medir no spike ARCH-003.

## 10. Exclusão de conta e cascata (ADR-0008 + ADR-0009)

Princípio (ADR-0009/0010): a exclusão de conta é **exclusiva do Owner ativo de um bebê**, **em cascata por padrão** (flag) e só executa **depois da janela de arrependimento (7 dias, `privacy.deletion_grace_days`)**. Funções executadas pelo worker (`nina_worker`); a API registra o pedido (com a confirmação) e o banco impõe a janela.

- `nina.erase_baby(baby_id, actor)` (idempotente): apaga de imediato todo o conteúdo (eventos, `wake_event`, preferências, previsões, jobs, mutações, feed e tombstones dos filhos), revoga vínculos remanescentes (`baby_deleted`) e transforma `baby` em **casca-tombstone** (campos nulos, `deleted_at`). O trigger gera `change_log` `delete` e `tombstone` do bebê; os dispositivos recebem o delete e apagam o banco local (privacy-spec 5.5). A casca some na purga (>= 90 dias, depois que o delete saiu do feed). Auditoria `baby.erased` + outbox `BabyDeleted`.
- `nina.erase_user(user_id, actor)` (idempotente se a conta já está `DELETED`), política em `privacy.owner_deletion_policy`. **Pré-condições impostas pelo banco, antes de qualquer escrita** (sem bypass, vale também para superusuário): (i) existe pedido `SCHEDULED` do usuário, senão `NN009` (`DELETION_REQUEST_REQUIRED`; pedido `CANCELLED`/`BLOCKED`/`COMPLETED` não conta); (ii) **`now() >= scheduled_for`, senão `NN008` (`DELETION_GRACE_NOT_ELAPSED`)**; (iii) o usuário ainda é Owner ativo de bebê vivo, senão `NN012` (`NOT_BABY_OWNER`: quem deixou de ser Owner durante a janela usa o fluxo 10.1). O pedido é lido com `FOR UPDATE`, serializando com um cancelamento concorrente (o perdedor falha com `NN009`/`NN011`). Bebês em que o usuário é o **único** membro ativo são sempre apagados (`erase_baby`). Para bebês em que ele é Owner **com outros membros ativos**:
  - **`CASCADE` (padrão)**: apaga o bebê **também para os demais cuidadores** (`erase_baby`). Exige **confirmação registrada**: o pedido aberto (`requested|scheduled`) deve ter `confirmed_at` (reautenticação feita pela UI após aviso explícito); sem ela, `NN007` (`CASCADE_CONFIRMATION_REQUIRED`) e nada é alterado (as pré-checagens rodam antes de qualquer escrita). **Auditoria** por bebê compartilhado: evento crítico `account.cascade_shared_baby_erased` (`metadata_safe`: nº de outros membros ativos, `confirmed_at`, `confirmation_method`, `request_id`; sem PII), além de `baby.erased` e `account.erased` (com a política). Validação jurídica pendente (DJ-09).
  - **`BLOCK`**: recusa (`NN004`, `OWNER_HAS_OTHER_CAREGIVERS`); a API marca o pedido `BLOCKED` e pede transferência de propriedade (RB-007). Nenhum dado de outros cuidadores é tocado.
  - **`TRANSFER_OWNERSHIP`**: promove o cuidador ativo mais antigo (`CAREGIVER` antes de `READ_ONLY`, por `accepted_at`) a Owner, encerra o vínculo do excluído com `revoked_reason='OWNERSHIP_TRANSFERRED'`, move `baby.family_id` para a família do novo Owner (criada se não existir) e preserva os dados do bebê; auditoria `baby.ownership_transferred` + outbox `BabyOwnershipTransferred`. Não exige a confirmação de cascata (nada é apagado).
  - Valor desconhecido da flag: `NN005`.
  - Demais passos: `nina.scrub_user_personal_data` (seção 10.1: vínculos como cuidador em bebês de terceiros, autoria anulada, consentimentos revogados, credencial, identidades, sessões/refresh, tokens de push, recuperações, preferências e jobs de notificação, entitlement, exports, anonimização de `app_user`); conclui o pedido (`policy_applied`); grava `account.erased` (política, `request_id` e contagens) e outbox `AccountDeleted`.
  - Cancelamento: o Owner cancela (`UPDATE status='CANCELLED'`) até `scheduled_for` (`NN011` depois); `erase_user` de pedido cancelado falha com `NN009`.
- Preservados após exclusão: `app_user` anonimizado, `consent_record`, `audit_event`, `subscription`/`family`/`family_entitlement` (obrigação fiscal; minimização a validar, D-07/DJ-06).
- FKs: dados de bebê `ON DELETE CASCADE` a partir de `baby`; `created_by`/`last_modified_by` `ON DELETE SET NULL`; consentimento e auditoria sem cascata (append-only).
- Backups: ciclo de 35 dias; restore deve reaplicar a fila de exclusões (SEC-064). Nada no banco implementa isso; é item de infraestrutura (CLOUD-001) [Q8].

### 10.1 Requisição de privacidade do não-Owner (ADR-0010 item 3)

Quem **não** é Owner ativo de bebê (`CAREGIVER`, `READ_ONLY`, ou sem vínculo ativo) mantém os direitos do titular por `privacy_request` (seção 8.4), **sem apagar dados de bebê** (pertencem ao Owner/família):

- `nina.open_privacy_request(tipo, verificação)` (`nina_app`): `ERASURE` de Owner ativo é recusada com `NN013` (`OWNER_MUST_USE_ACCOUNT_DELETION`: ele usa a exclusão de conta, que trata os bebês); `ERASURE` sem verificação de identidade, `NN014`; usuário sem contexto/ já excluído, `NN015`. Sem janela de arrependimento (a janela de 7 dias é da exclusão de conta, ADR-0010 item 6); o titular pode cancelar enquanto `REQUESTED`.
- `nina.fulfill_privacy_erasure(request, actor)` (`nina_worker`, idempotente): exige `ERASURE` `REQUESTED` com identidade verificada (`NN014`), usuário não excluído e que **não** seja Owner ativo (`NN013`, re-checado na execução); chama `scrub_user_personal_data`, conclui o pedido (`FULFILLED`), cancela os demais pedidos abertos do usuário, grava `privacy.erasure_completed` (crítico; só contagens; `on_time` vs `due_at`) e outbox `PersonalDataErased`.
- `nina.scrub_user_personal_data(user, actor)` (interna; também usada por `erase_user`) devolve só contagens. **Elimina/anonimiza**: conta (`app_user` → `DELETED`, sem e-mail/locale/fuso), credencial, identidades externas, sessões e refresh tokens, tokens de push, recuperações, vínculos de cuidador (o bebê e seus dados seguem), preferências e jobs de notificação, membro de entitlement (`removed_at`), `file_ref` de exports; **consentimentos**: os concedidos ganham linha `REVOKED` e o histórico permanece como prova mínima sem PII (append-only); **atribuição de autoria**: `created_by/last_modified_by` = `NULL` em todos os dados de bebê (inclusive tombstones), sem alterar `version`, `change_log` nem conteúdo (`sync_stamp_*` com `nina.authorship_scrub`). `invited_by` também é zerado.
- **Retido (pseudonimizado, sem PII)**: linha `app_user` anonimizada, `consent_record`, `audit_event`, `family`/`subscription`/`family_entitlement` (fiscal), `sync_mutation`/`change_log` (apagados pela purga de 90 dias; `device_id` é UUID de dispositivo). Os dados do bebê não são tocados.
- Auditoria: `privacy.request_opened`, `privacy.request_cancelled`, `privacy.request_closed`, `privacy.erasure_completed` (todos `is_critical`, entram na cadeia de hash). Acesso/correção/exportação: o banco registra, prazo (`due_at`) e auditoria; o cumprimento (gerar o arquivo, aplicar a correção) é da API/worker, que encerra com `close_privacy_request`.
- Limite conhecido: `nina.authorship_scrub`/`nina.scrub_user` são GUCs de transação, não uma fronteira de privilégio; um `nina_app` que as ajustasse só conseguiria anular o ponteiro de autoria de linhas que já pode atualizar (sem alterar conteúdo, versão ou sync). Endurecer (ex.: token secreto) se necessário.

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

- Arquivo único e transacional `0001_init.sql`; registra `schema_migration('0001')` (ADR-0009 e ADR-0010 já incorporados; nada em produção, logo sem migração de dados). Não é re-executável (sem `IF NOT EXISTS`); o runner de migração (ARCH-001) deve controlar versões. O arquivo contém `BEGIN/COMMIT`; se o runner já abre transação, remover as duas linhas.
- A criação de papéis ocorre na migração com guarda `IF NOT EXISTS`; em nuvem gerenciada, a infra pode criar os papéis antes.
- Agendamentos (worker .NET): `purge_expired_sync_data` diário em loop até retornar zeros; `purge_expired_operational_data` diário; outbox contínuo.
- Códigos de erro customizados (SQLSTATE classe `NN`): `NN001` id/baby imutável, `NN002` tombstone, `NN003` bebê excluído, `NN004` owner com outros cuidadores (política `BLOCK`), `NN005` política desconhecida, `NN006` sono sobreposto (política `REJECT`), `NN007` cascata sem confirmação registrada, `NN008` exclusão antes de `scheduled_for` (janela de arrependimento), `NN009` sem pedido de exclusão agendado, `NN010` bebê sem Owner, `NN011` cancelamento fora da janela / transição inválida do pedido, `NN012` não é Owner ativo (usar privacy_request), `NN013` Owner ativo não usa `ERASURE` de privacidade, `NN014` identidade não verificada, `NN015` requisição de privacidade inexistente/encerrada/sem contexto, `NN020/021` entitlement, `NN030` append-only, `NN040` ator ausente.

## 13. Perguntas e pontos abertos

Resolvidas pelo ADR-0010 (2026-10-08):
- ~~**[Q12]** "Somente o Owner exclui a conta": interpretação para `caregiver`/`read_only`/sem vínculo~~ → **Resolvida**: só **Owner ativo de um bebê** pode solicitar a exclusão de conta (a permissão para "sem vínculo ativo" foi **removida**). Quem não é Owner usa `privacy_request` (acesso, correção, exportação e **eliminação/anonimização dos próprios dados pessoais** sem apagar dados do bebê; seção 10.1). Resta a validação jurídica (DJ-09) e a UI deixar o fluxo fluido.
- ~~**[Q13]** valores iniciais propostos~~ → **Resolvida**: `age.corrected_window_months = 24` e `sleep.night_awakenings.min_session_minutes = 240` aceitos como padrões, **editáveis** no banco com validação (seção 7). `milk_type NULL` em `BOTTLE` segue aceito (= indisponível). Fora do banco: `wake_event` dentro dos limites da sessão.
- ~~**[Q8, parte]** prazo da janela de arrependimento~~ → **Resolvida**: **7 dias**, parâmetro `privacy.deletion_grace_days` (editável, 1 a 30); `scheduled_for = requested_at + grace`; **o banco impede `erase_user` antes de `scheduled_for`** (`NN008`) e permite o cancelamento durante a janela (seção 8.4/10). O restante de [Q8] (restore de backup, retenção de consentimento/auditoria/fiscal, DJ-06) segue aberto.
- ~~**PA-05** `end_at` de mamada~~ → **Resolvida**: `end_at` **obrigatório** em `BREASTFEEDING` (CHECK `feeding_shape_ck` já existente; testado em INSERT e UPDATE). Reabrir se o timer de mamada for necessário.
- ~~Enums~~ → **Resolvido**: todos os valores fixos em MAIÚSCULAS (seção 5.1).

Resolvidas pelo ADR-0009 (mantidas por rastreabilidade):
- ~~**[Q3]** ADR-0008/DJ-09 (opções a/b/c para exclusão do Owner)~~ → **Resolvida**: padrão `CASCADE` (a), com `BLOCK` (b) e `TRANSFER_OWNERSHIP` por flag. Resta a **validação jurídica (DJ-09)** e a UI avisar e exigir reautenticação.
- ~~**[Q4]** o adicional do premium precisa ser cuidador ativo?~~ → **Resolvida**: não precisa; nenhuma exigência no banco nem na API. (Segue em aberto apenas: se o titular cancelar, o entitlement expira para todos, sem herança; preço fora do escopo do banco.)
- ~~**[Q7]** quem edita o perfil do bebê~~ → **Resolvida**: somente o Owner (RLS).
- ~~**[Q9] D-05** sobreposição de sono~~ → **Resolvida**: aceita e sinalizada por padrão; `REJECT` por flag. ~~**D-08** tipos de fralda~~ → **Resolvida**: `DiaperType` fechado (extensível por CHECK). ~~**D-12/D-13** despertares noturnos~~ → **Resolvida em parte**: `wake_event` + `nightAwakenings` derivado (critério de "suficiente" editável); o **corte do dia** (D-13) não afeta o banco e segue com o produto. ~~**D-01** idade corrigida~~ → **Resolvida**: nunca persistida; janela é parâmetro.

Em aberto:
- **[Q1]** Confirmar a entidade `family` (1 por titular) e que bebê pertence à família do Owner. Na política `TRANSFER_OWNERSHIP` o banco já move `baby.family_id` para a família do novo Owner (criando-a se faltar); validar com produto/entitlements, pois o bebê deixa de herdar o plano do antigo titular.
- **[Q2]** `sync_sequence` pode ser **por bebê** (esta spec) em vez de global? Cursor vira mapa por bebê. Validar no spike ARCH-003 (contenção, tamanho do cursor).
- **[Q5]** Quem pode usar `nina_config_admin` em produção, aprovação em dois olhos e ferramenta de edição (ADR-0005). As flags que mudam comportamento destrutivo/de integridade (`privacy.owner_deletion_policy`, `privacy.deletion_grace_days`, `sleep.overlap_policy`) merecem aprovação dupla; reduzir a janela para 1 dia ou mudar a política para `CASCADE` afeta o direito de arrependimento.
- **[Q6]** INV-27 (domain-model) diz "adiciona `revoked_at`"; privacy-spec 3.3 diz "append-only, estado derivado do último registro". Adotada a segunda; alinhar o domain-model.
- **[Q8]** (restante) Restore de backup e reaplicação da fila de exclusões; prazos de retenção de consentimento/auditoria/fiscal (DJ-06). O privacy-spec §4 ainda cita janela de 14 dias (opcional); o ADR-0010 fixou 7: alinhar o texto do privacy-spec.
- **[Q9]** Ainda moldam colunas: D-09 (foto: `photo_ref` apenas), D-11 (perfil pré-natal: `birth_date` é NOT NULL), método/local de sono (`method_or_place` texto curto).
- **[Q10]** Cifra em nível de campo (`display_name`, `notes`, `birth_date`, token de push) é recomendada (SEC-021) mas não implementada aqui; decidir por ADR (impacta busca/índices e RLS).
- **[Q11]** Fora do MVP, não modeladas: fases de desenvolvimento, diário/mídia, conteúdo, IA, `DsarRequest` genérico além de `privacy_request` (tipos `ACCESS|CORRECTION|EXPORT|ERASURE`).
- **[Q14]** (novo) `privacy_request.ERASURE` não tem janela de arrependimento (cancelável só até o cumprimento) e o e-mail fica liberado para novo cadastro (a `app_user` anonimizada tem `email_normalized` nulo). Confirmar com jurídico/DJ-07 o prazo (`privacy.request_response_days = 15`), a verificação de identidade além da reautenticação e se a eliminação de um cuidador deve notificar o Owner (hoje apenas a auditoria e o outbox `PersonalDataErased`).
- **[Q15]** (novo) Autoria anulada em dados de bebê pode afetar a UI "registrado por"; a API deve tolerar `created_by`/`last_modified_by` nulos. Ponteiros de autoria não fazem parte do contrato (`contracts/openapi.yaml`).

## 14. Validação executada

Em PostgreSQL 16.15 local (cluster temporário via `initdb`, removido ao final; papéis `nina_app`, `nina_worker`, `nina_config_admin` via `SET ROLE`; `nina.user_id` por transação). Revisão ADR-0010: **263 asserções funcionais, todas passando**, mais `pgbench`; a migração roda do zero em bancos novos (`psql -v ON_ERROR_STOP=1`, uma transação).

- **Enums em MAIÚSCULAS**: varredura em `pg_constraint`/`pg_attrdef` sem nenhum literal minúsculo restante em CHECKs/defaults (exceto `jsonb_typeof`, regex e `position`); seeds (`value_type`, escopo de consentimento, políticas); minúsculos antigos rejeitados (`sleep_type`, `source` de sono, `sex`, papel, status de vínculo, `diaper_type`, `feeding_type`, `source` de despertar, `op` de mutação, status de flag, política `"reject"`/`"block"` no validador); maiúsculos aceitos; RLS com `OWNER/CAREGIVER/READ_ONLY` (read_only lê e não escreve; caregiver escreve e não edita perfil; Owner edita; sem vínculo/sem contexto não vê; sem autoinclusão como Owner); `change_log`/`tombstone` com `entity_type`/`op` maiúsculos, `version` = sequência, `notes` limpo, ressurreição `NN002`; flags `GATED/OPEN/DISABLED`; entitlement `HOLDER/ADDITIONAL` (adicional sem vínculo a bebê, limite `NN021`, titular `NN020`); sobreposição de sono com `ACCEPT_AND_WARN`/`REJECT` (recusa `NN006`, adjacência, update que passa a sobrepor).
- **Exclusão só para Owner**: `CAREGIVER`, `READ_ONLY`, **usuário sem vínculo** e Owner pedindo por outro são recusados pela RLS (`42501`); Owner cria; valores forjados (`status`, `scheduled_for`, `requested_at`, `grace_days`) são sobrescritos pelo trigger.
- **Janela de 7 dias**: `scheduled_for = requested_at + 7 dias`; `erase_user` antes dela falha com `NN008` para `nina_worker` **e para superusuário**, sem alterar nada (conta, bebê, credenciais intactas); `COMPLETED` manual antes da janela `NN008`; sem pedido `NN009` (cuidador e sem vínculo); `nina_app` não executa `erase_user`; campos do pedido imutáveis (`NN001`); cancelamento durante a janela funciona (`cancelled_at`, auditoria) e depois dela `NN011`; pedido cancelado/concluído é terminal; `erase_user` de pedido cancelado `NN009`; após a janela (relógio simulado desabilitando o trigger no teste) `erase_user` executa. `privacy.deletion_grace_days`: padrão 7; `0`, `31`, `7.5`, `-1`, `"7"` rejeitados (`23514`); `3` e `30` aceitos por `nina_config_admin` com `config_change`/`audit_event`; sem ator `NN040`; `nina_app` não edita; pedido novo usa 3 dias e o anterior mantém 7 (snapshot).
- **Políticas após a janela**: `CASCADE` sem confirmação `NN007` (nada alterado); `BLOCK` `NN004`; `TRANSFER_OWNERSHIP` promove o cuidador, preserva o bebê e anula autoria; `CASCADE` em bebê solo e **compartilhado com confirmação** (membros e conta dos demais intactos, bebê de terceiro intacto, auditoria `account.cascade_shared_baby_erased` crítica com 2 outros membros, `baby.erased`, `account.erased` com política/`request_id`/contagens); quem deixou de ser Owner na janela `NN012`; idempotência.
- **Requisição de privacidade do não-Owner**: `CAREGIVER`, `READ_ONLY` e sem vínculo abrem `ERASURE`; Owner ativo `NN013` (também na execução forçada); sem verificação `NN014` (e CHECK em inserção direta); sem contexto/conta excluída `NN015`; duplicada `23505`; `nina_app` sem INSERT/UPDATE direto (`42501`) e vendo só os próprios; `due_at` = +15 dias; cancelamento só do titular. Cumprimento (`nina_worker`): `app_user` anonimizado; credencial, sessões, push e vínculos apagados; consentimentos preservados + revogações append-only; autoria `NULL` (sessão, mamada, fralda-tombstone, bebê); **dados do bebê intactos** (contagens, perfil, `last_sequence` e `version` inalterados, Owner e demais vínculos mantidos, tombstone mantido); auditoria `privacy.erasure_completed` (crítica, só contagens, sem PII) e `PersonalDataErased`; idempotente; `close_privacy_request` (ACCESS) e recusa de `COMPLETED` para `ERASURE`; `fulfill`/`scrub` negados a `nina_app`.
- **Padrões 24/240 e `end_at`**: padrões presentes; `age_calculation` e `night_awakenings` respeitam valor editado (12; 120) e voltam; valores fora da faixa/tipo inválido `23514`; `app_parameter.version` incrementa; mamada `BREASTFEEDING` sem `end_at` (INSERT e UPDATE), sem `side` ou com `milk_type` rejeitada; com `end_at` aceita; `BOTTLE` sem `end_at` aceita.
- **Validações centrais reexecutadas**: RLS (acima); append-only de `audit_event`/`consent_record`/`config_change` (UPDATE/DELETE/TRUNCATE `NN030`, DELETE de `nina_worker` sem flag `NN030`); privilégios de `nina_app` (sem escrita em `change_log`, sem leitura de `baby_sync_head`/auditoria, sem DELETE em sincronizáveis, INSERT em auditoria ok); `metadata_safe` com chave proibida rejeitada; bebê sem Owner `NN010` e segundo Owner `23505`; cadeia de hash da auditoria recomputada (todas as linhas críticas, sem quebra); purga com relógio simulado (tombstone de sessão, casca de bebê e `change_log` removidos só quando vencidos e fora do feed, `purged_through` elevado, bebê vivo preservado, purga operacional só para worker); idempotência de `sync_mutation`. **Concorrência (`pgbench`, 8 clientes, 15 s, ~17 mil transações, ~25% com rollback, 2 inserts + 1 update de bebê por transação, via `nina_app` + RLS)**: 3 bebês, 38.238 mudanças, `change_log` de cada bebê com sequência contígua `1..N` igual a `baby_sync_head.last_sequence` (12.787 / 12.832 / 12.619), zero inversão de `changed_at` em relação à sequência, zero versão duplicada.

Não validado: carga/desempenho de RLS em volume, restore de backup, PostgreSQL 15, agendamento real do worker (jobs de purga, `erase_user` e `fulfill_privacy_erasure` foram chamados manualmente), a passagem real do tempo (o relógio da janela foi simulado deslocando `requested_at/scheduled_for` com o trigger desabilitado no teste), concorrência real entre cancelamento e `erase_user` (apenas o `FOR UPDATE` por inspeção), `GUC` `nina.authorship_scrub` como fronteira de segurança (ver 10.1), integração com API/OpenAPI, e a validação jurídica da cascata (DJ-09), do prazo de 15 dias e da retenção de consentimentos (DJ-06/DJ-07).
