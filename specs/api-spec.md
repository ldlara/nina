# Especificação da API — Nina BFF (MVP)

Status: rascunho para revisão (API-001) · Data: 2026-10-08
Contrato: `contracts/openapi.yaml` (OpenAPI 3.1, 66 operações em 52 caminhos). Este documento explica decisões, convenções, rastreio e pontos em aberto. Em divergência, o OpenAPI prevalece sobre a descrição, e ambos prevalecem sobre exemplos.
Fontes: `specs/product-spec.md`, `domain-model.md`, `ux-spec.md`, `privacy-security-spec.md`, ADR-0002, 0003, 0005, 0007, 0008, 0009, `docs/project/architecture.md`.
Revisão ADR-0009 (2026-10-08): convenções de contrato e decisões de domínio aplicadas (idade, enums, `WakeEvent`, flags de sono sobreposto e exclusão de conta, permissões do Owner, assento premium). O contrato usa `snake_case`; os nomes camelCase do ADR (`ageCalculation`, `nightAwakenings`...) viram `age_calculation`, `night_awakenings`... (PA-24).

Escopo: API que o BFF (.NET) expõe aos apps iOS e Android (ADR-0002). Fora do contrato: API interna BFF↔domínio, webhooks de loja (App Store Server Notifications, Google RTDN; ARCH-001), envio de analytics (RF-048, vai ao fornecedor) e entrega de push (APNs/FCM).

## 1. Decisões

| ID | Decisão | Motivo |
|---|---|---|
| AD-01 | Prefixo `/v1` no caminho. Mudança aditiva não muda versão; quebra cria `/v2`. Cliente ignora campos e enums desconhecidos. Descontinuação por `Deprecation` e `Sunset`. `426 CLIENT_UPGRADE_REQUIRED` para app abaixo do mínimo. | App móvel não atualiza no mesmo instante do servidor. |
| AD-02 | Auth por bearer. Access token ≤ 15 min; refresh opaco, rotativo, uso único, com detecção de reuso (revoga a família). | SEC-011, ADR-0007. |
| AD-03 | Cadastro por e-mail devolve `202` uniforme; **a conta só fica ativa após confirmação de e-mail** e a sessão só nasce em `POST /auth/email/verify`. **Confirmado** (ADR-0009, decisão 1). | RF-001-A2 (anti-enumeração) é incompatível com devolver `201` + sessão para e-mail novo. PA-01 resolvido. |
| AD-04 | Google e Apple: o app envia `id_token` + `nonce`; o servidor valida. Mesmo e-mail em conta existente: `409 IDENTITY_LINK_REQUIRED`, sem fusão; vínculo só autenticado, em `POST /me/identities` com reauth. | ADR-0007 (evitar account takeover). |
| AD-05 | Reautenticação por `POST /auth/reauthenticate` devolve `reauth_token` de 5 min, enviado em `X-Reauth-Token`. Exigido em exportar (download), excluir conta/bebê, transferir propriedade, trocar senha/e-mail e vincular identidade. | RF-044, RF-045, UX 4.10. |
| AD-06 | Erros em RFC 7807 com `code` estável em `UPPER_SNAKE_CASE`, `errors[]` por campo e `request_id`. `title`/`detail` não são para exibição; o cliente localiza por `code`. | RF-050-A5. |
| AD-07 | Autorização por (usuário, bebê, papel). Sem vínculo: `404` uniforme. Com vínculo e papel insuficiente: `403 FORBIDDEN_ROLE`. Vínculo revogado: `403 ACCESS_REVOKED`. | RF-004-A6 (sem revelar existência) e RF-006-A5 (403 após revogação). Ver PA-09. |
| AD-08 | **Escrita de eventos de tracking só por `POST /sync/push`**, inclusive online (lote de 1 mutação). Leitura por `timeline`, `events/{id}` e `sync/pull`. Não há `POST/PATCH/DELETE /events`. **Confirmado** (ADR-0009, decisão 2). | Um único caminho de escrita elimina divergência de regras de conflito e idempotência entre online e offline. |
| AD-09 | Baby, preferências de sono, notificações, cuidadores e privacidade são REST **online** (UX 4.13: convidar, assinar, exportar e excluir exigem conexão). Mudanças nesses recursos chegam aos outros dispositivos pelo feed de sync (`baby`, `sleep_preferences`, `sleep_prediction`). | Só o tracking precisa operar offline (RF-046). |
| AD-10 | Idempotência: `Idempotency-Key` (UUID, retenção 24 h) nos `POST` com efeito colateral não-sync; mesmo corpo repete a resposta (`Idempotent-Replayed: true`); corpo diferente dá `422 IDEMPOTENCY_KEY_REUSE`. Sync idempotente por `mutation_id`. | RNF-007, RNF-011. |
| AD-11 | Paginação de listas: `limit` (1..100, padrão 50) + `page_token` opaco; resposta `page {has_more, next_page_token}`. O feed de sync usa `cursor` (mecanismo distinto, ver 3). | Evita confundir paginação de leitura com posição no change log. |
| AD-12 | Concorrência otimista nos recursos REST editáveis: `ETag`/`If-Match` com a `version`; divergência dá `412 VERSION_CONFLICT`. `If-Match` é opcional (PA-12). | Edição de bebê e preferências por dois cuidadores. |
| AD-13 | Datas: instantes UTC (RFC 3339 com `Z`), `tz` IANA contextual, datas civis `YYYY-MM-DD`, horas locais `HH:MM`, volume sempre em `ml`. | RB-014, RNF-010. |
| AD-14 | A API devolve chaves de mensagem (`explanation.key`, `disclaimer_key`) e parâmetros; texto e idioma ficam no cliente. Previsão sem percentual, só `building \| fair \| good`. | RF-050-A4, RNF-014, UX 4.5. |
| AD-15 | Valores vigentes dos enums extensíveis (fralda, alimentação, leite), métodos de sono, limites (volumes máximos, TTLs, critério de despertares) e **políticas por flag** (`policies`) vêm de `GET /reference-data`. | ADR-0005 (parâmetros em banco), ADR-0009. Atualizada pela AD-20. |
| AD-16 | Entitlement e feature flags em `GET /me/entitlements`, calculados no servidor, com `cache_expires_at` para uso offline. Recurso desconhecido pelo cliente é tratado como bloqueado. | RB-009, ADR-0005, RF-041-A6. |
| AD-17 | Rate limit por IP, conta e dispositivo (SEC-040): `429 RATE_LIMITED` com `Retry-After`. Sync: máx. 100 mutações e 256 KiB por push (`413`). | SEC-040, SEC-042. |
| AD-18 | Cabeçalhos: `X-Request-Id` (resposta, correlação), `Accept-Language`, `X-Reauth-Token`, `Idempotency-Key`, `If-Match`/`ETag`. Nenhum token em URL ou log. | SEC-012, RNF-008. |
| AD-19 | **Idade**: persistem-se só `birth_date` e `due_date`; a idade corrigida nunca é persistida. `Baby.age_calculation {chronological_days, corrected_days (nullable), correction_applied}` é a fonte canônica em dias; `Baby.age` (semanas/meses) é só decomposição para exibição. `corrected_days = chronological_days - (due_date - birth_date)` apenas se `birth_date < due_date` e dentro da janela (parâmetro em banco, `policies.corrected_age_max_months`); caso contrário `null` e `correction_applied=false`. | ADR-0009; ADR-0005; resolve D-01 no contrato. |
| AD-20 | **Convenção de nulos** (texto em `info.description`): `null` = não se aplica ou indisponível/desconhecido (documentado por campo); `0` = zero conhecido, nunca "não informado"; `UNSPECIFIED` = usuário não especificou; `UNKNOWN` = sistema não sabe (migração; reservado, cliente não envia). | ADR-0009. |
| AD-21 | **Enums extensíveis** (`x-extensible-enum`): `DiaperType` (`WET`, `DIRTY`, `MIXED`, `DRY`, `UNSPECIFIED`), `FeedingType` (`BREASTFEEDING`, `BOTTLE`, `SOLID`, `OTHER`), `MilkType` (`BREAST_MILK`, `FORMULA`, `MIXED`, `OTHER`, `UNSPECIFIED`; `UNKNOWN` reservado). `milk_type` só existe com `feeding_type=BOTTLE`; nos demais é `null`. Cliente tolera valores desconhecidos. Lado da mama: `BreastSide` (`LEFT`, `RIGHT`, `BOTH`). Valores em maiúsculas (mudança em relação ao rascunho anterior, que usava minúsculas). | ADR-0009. Tipos de fralda e leite são classificações de domínio, separadas dos valores derivados (idade corrigida, despertares). |
| AD-22 | **Despertares**: entidade `WakeEvent` (`id`, `sleep_session_id`, `started_at`, `ended_at`, `duration_seconds`, `source` = `MANUAL`\|`INFERRED`\|`IMPORT`) é a fonte da verdade, sincronizável (`entity_type=wake_event` em push e pull, `version`, tombstone; excluir a `sleep_session` gera tombstone dos seus despertares) e com correção manual. `night_awakenings` (em `SleepSession`, buckets de `/aggregates`) e `night_awakenings_avg` (em `summary`/`deltas`) são **derivados**: `null` = dados insuficientes; `0` = acompanhamento suficiente e nenhum despertar; `N>0` = quantidade. O critério de suficiência é parâmetro editável (`limits.night_awakenings_min_coverage_percent`). | ADR-0009; resolve D-12 no contrato (PA-17). |
| AD-23 | **Sono sobreposto governado por flag** `sleep_overlap_policy` em `/reference-data` -> `policies`: `accept_and_warn` (padrão; `applied` + `resolution=kept_both` + `warnings[SLEEP_OVERLAP]`) ou `reject` (`rejected`, `problem.code=SLEEP_OVERLAP`, `retryable=false`). | ADR-0009, decisão 5; resolve D-05 (PA-04). |
| AD-24 | **Exclusão de conta**: só o Owner; janela de arrependimento mantida (`scheduled_for`, `DELETE /me/deletion-request`); reautenticação (`X-Reauth-Token`); política por flag `account_deletion_policy` (`policies` e `AccountDeletion.policy`): `cascade` (padrão), `block`, `transfer_ownership`. Em `cascade` com outros cuidadores ativos exige `acknowledge_other_caregivers=true` (a UI informa que os dados do bebê serão apagados também para eles); sem isso, `409 OWNER_DECISION_REQUIRED` com `deletion_policy`. Validação jurídica pendente (DJ-09). | ADR-0009, decisões 4, 6 e 8; substitui o padrão `block` do ADR-0008. |
| AD-25 | **Perfil do bebê**: só o Owner edita (`PATCH /babies/{id}`; `403 FORBIDDEN_ROLE` para Caregiver e ReadOnly). | ADR-0009, decisão 6. |
| AD-26 | **Assento premium** (titular + 1): o adicional **não precisa ser cuidador ativo**; `PUT /subscriptions/family-seat` aceita `membership_id` **ou** `email`; remover/revogar cuidador não remove o assento. | ADR-0009, decisão 7; ADR-0005. |

## 2. Papéis (Owner, Caregiver, ReadOnly)

Papel no contrato: `owner`, `caregiver`, `read_only`. Convite aceita só `caregiver` e `read_only`.

| Capacidade | Owner | Caregiver | ReadOnly |
|---|---|---|---|
| Ler bebê, timeline, eventos, previsão, preferências de sono, gráficos, lista de cuidadores | sim | sim | sim |
| Criar/editar/excluir eventos (`/sync/push`) | sim | sim | não (403 por mutação) |
| Alterar meta de sonecas e faixa de bedtime | sim | sim | não |
| Editar perfil do bebê (`403 FORBIDDEN_ROLE` para os demais) | sim | não | não |
| Convidar, reenviar, cancelar, alterar papel, remover cuidador, transferir propriedade, excluir bebê | sim | não | não |
| Sair do bebê (remover o próprio vínculo) | não (transferir antes) | sim | sim |
| Ver histórico de alteração de um evento | sim | sim | não (D-25) |
| Ver e-mail de convidados e "atividade de acesso" (`audit-events` do bebê) | sim | não | não |
| Preferências de notificação (por usuário e bebê) | sim | sim | sim |
| Entitlement (do usuário), consentimentos, exportação, sessões | próprio usuário (qualquer papel) | idem | idem |
| Excluir a própria conta (`/me/deletion-request`; ADR-0009, PA-27) | sim | não (403 `FORBIDDEN_ROLE`) | não (403 `FORBIDDEN_ROLE`) |
| Atribuir assento premium (proposta); o adicional não precisa ser cuidador | sim | não | não |

Regras transversais: o Owner nunca fica sem Owner (INV-09; `409`). Revogar vínculo invalida tokens escopados, cache e cursor do bebê (INV-14).

## 3. Sync

### 3.1 Push (`POST /sync/push`)
- Corpo: `device_id` + `mutations[]` (≤ 100), processadas em ordem. Cada mutação: `mutation_id` (UUID), `op` (`create|update|delete`), `entity_type` (`sleep_session`, `feeding_session`, `pumping_session`, `diaper_event`, `wake_event`), `entity_id` (UUID do cliente), `baby_id`, `base_version` (0 na criação), `client_created_at`, `data` (completo na criação; parcial na edição; ausente na exclusão).
- Resposta `200` com `results[]` na mesma ordem. Status por mutação: `applied`, `duplicate` (reenvio; devolve a versão canônica, sem efeito) e `rejected` (com `problem`, `retryable`). Falhas de envelope ou cota (`400/401/413/426/429`) rejeitam o lote inteiro.
- Conflito (ADR-0003, RF-047): edições concorrentes resolvidas por last-write-wins **por campo**; `resolution` (`merged`, `lww_client_won`, `lww_server_won`, `delete_wins`, `kept_both`), `conflicts[]` e `entity` canônica voltam ao cliente; o servidor audita (INV-21).
- Exclusão vira tombstone (excluir uma `sleep_session` gera também tombstones dos seus `wake_event`); update atrasado sobre tombstone **não ressuscita**: `rejected`, `ENTITY_DELETED`, `retryable=false` (INV-20).
- Sono: pela política padrão `accept_and_warn`, sobreposição ou segundo timer aberto não é rejeitado; vem como `warnings` (`SLEEP_OVERLAP`, `OPEN_SLEEP_EXISTS`) e `resolution=kept_both` (UX 4.13). Com a flag `sleep_overlap_policy=reject`, a mutação sai `rejected` com `problem.code=SLEEP_OVERLAP` e `retryable=false` (AD-23). Relógio errado: mutação aceita, `client_created_at` preservado, `CLIENT_CLOCK_SKEW` em `warnings` (D-35).
- Autorização por mutação: `baby_id` inacessível gera `rejected` (`FORBIDDEN_ROLE` ou `ACCESS_REVOKED`, `retryable=false`); em `ACCESS_REVOKED` o cliente apaga o cache local do bebê.
- Códigos de rejeição por item: `VALIDATION_FAILED` (com `errors[]`), `SLEEP_OVERLAP` (só com a política `reject`), `ENTITY_DELETED`, `ENTITY_NOT_FOUND`, `VERSION_AHEAD` (base_version maior que a do servidor), `FORBIDDEN_ROLE`, `ACCESS_REVOKED`, `BABY_NOT_FOUND`, `TRANSIENT` (`retryable=true`).

### 3.2 Pull (`GET /sync/pull?baby_id=&cursor=&limit=`)
- **Por bebê** (PA-02). Sem `cursor` = **snapshot** paginado das entidades vivas; o servidor fixa o ponto de consistência na primeira página, e o último `next_cursor` do snapshot continua em **delta**. Com `cursor` = apenas mudanças posteriores, incluindo **tombstones**.
- Entidades no feed: `baby` (com `my_role` do usuário), `sleep_session`, `feeding_session`, `pumping_session`, `diaper_event`, `wake_event`, `sleep_preferences`, `sleep_prediction`. `op=upsert` traz `entity`; `op=tombstone` traz só `entity_id`, `version` e `deleted_at` (sem conteúdo; tombstone não é canal de vazamento).
- **Cursor opaco**: string de até 512 caracteres; internamente referencia o `sync_sequence` (ADR-0003). O cliente só guarda e devolve; nunca interpreta, compara ou ordena. O formato pode mudar sem aviso.
- Aplicação no cliente: idempotente e por `version` por entidade (descartar upsert com versão ≤ à local); repetir uma página é seguro.
- **Cursor expirado** (> 90 dias, retenção de tombstones) **ou inválido** (adulterado, de outro bebê): `410 Gone`, `code=SYNC_CURSOR_EXPIRED`, `reason=expired|invalid`, `resync_required=true`. Procedimento do cliente: (1) manter a fila de mutações pendentes; (2) descartar o estado sincronizado do bebê; (3) `GET /sync/pull` sem cursor até `has_more=false`; (4) reenviar as pendentes por `/sync/push` (idempotente; as que apontam para entidades inexistentes saem `rejected`).
- Vínculo revogado: `403 ACCESS_REVOKED`; o cliente remove os dados locais do bebê (SEC-005).
- Lacunas visíveis por ordem de commit (ADR-0003) ficam no spike ARCH-003; o contrato não muda, apenas o que o servidor garante ao emitir `next_cursor`.
- `version` é por entidade, atribuída pelo servidor. A ordem global do feed vem do cursor. Ver PA-03 sobre a leitura de INV-19.

### 3.3 Papel de cada endpoint no offline
`/babies/{id}/timeline` e `/events/{id}` servem consulta online e relatórios; o app renderiza a partir do banco local. Previsão offline usa a baseline por idade no cliente (RF-011-A10). Gráficos offline: D-26.

## 4. Convenções de erro

`Content-Type: application/problem+json`. Campos: `type` (`https://api.nina.app/problems/<slug>`), `title`, `status`, `detail` (sem PII), `code`, `request_id`, `errors[{field, code}]`, mais extensões (`resync_required`, `reason`, `required_consents`, `babies_requiring_decision`, `retry_after_seconds`).

| HTTP | `code` | Quando |
|---|---|---|
| 400 | `VALIDATION_FAILED` | Corpo ou parâmetro inválido (`errors[].code`: `REQUIRED`, `OUT_OF_RANGE`, `END_BEFORE_START`, `FUTURE_DATE`, `PASSWORD_POLICY`, ...). |
| 401 | `INVALID_CREDENTIALS`, `TOKEN_EXPIRED`, `SESSION_REVOKED`, `REFRESH_TOKEN_REUSED`, `REAUTH_REQUIRED` | Autenticação. Credencial inválida é sempre genérica. |
| 403 | `FORBIDDEN_ROLE`, `ACCESS_REVOKED`, `CONSENT_REQUIRED` | Autorização e consentimento. |
| 404 | `NOT_FOUND` | Inexistente ou sem vínculo (uniforme). |
| 409 | `IDENTITY_LINK_REQUIRED`, `ALREADY_MEMBER`, `LAST_LOGIN_METHOD`, `OWNER_DECISION_REQUIRED` (com `deletion_policy`), `OWNER_REQUIRED`, `PURCHASE_CONFLICT`, `EXPORT_NOT_READY`, `DELETION_NOT_CANCELLABLE`, `ENTITY_DELETED`, `SLEEP_OVERLAP` (só por item de sync, política `reject`) | Estado incompatível. |
| 410 | `SYNC_CURSOR_EXPIRED` | Resync completo obrigatório. |
| 412 | `VERSION_CONFLICT` | `If-Match` divergente. |
| 413 | `PAYLOAD_TOO_LARGE` | Lote de sync acima do limite. |
| 422 | `IDEMPOTENCY_KEY_REUSE` | Mesma chave, corpo diferente. |
| 426 | `CLIENT_UPGRADE_REQUIRED` | App abaixo do mínimo. |
| 429 | `RATE_LIMITED` | Limite de taxa, com `Retry-After`. |
| 5xx | `INTERNAL`, `UNAVAILABLE` | Sem detalhe interno. |

Nota: o contrato usa `400` para validação semântica (um único código para validação). Se a equipe preferir `422` para erros semânticos e `400` para sintaxe, a troca é mecânica (PA-13).

## 5. Matriz endpoint → RF e papéis

Papéis permitidos: O = Owner, C = Caregiver, R = ReadOnly, Pub = sem token, Auth = qualquer usuário autenticado (o recurso é do próprio usuário), Inv = convidado autenticado.

| Método | Caminho | operationId | RF | Papéis |
|---|---|---|---|---|
| POST | `/auth/register` | registerWithEmail | 001, 003 | Pub |
| POST | `/auth/email/verify` | verifyEmail | 001 | Pub |
| POST | `/auth/login` | loginWithPassword | 001 | Pub |
| POST | `/auth/google` | loginWithGoogle | 001, 003 | Pub |
| POST | `/auth/apple` | loginWithApple | 001, 003 | Pub |
| POST | `/auth/refresh` | refreshSession | 001, 054 | Pub (refresh token) |
| POST | `/auth/logout` | logout | 054 | Auth |
| POST | `/auth/password/forgot` | requestPasswordReset | 002 | Pub |
| POST | `/auth/password/reset` | resetPassword | 002 | Pub (token) |
| POST | `/auth/reauthenticate` | reauthenticate | 044, 045 | Auth |
| GET/PATCH | `/me` | getMe / updateMe | 001, 040, 050 | Auth |
| PUT | `/me/password` | changePassword | 002 | Auth + reauth |
| POST | `/me/email-changes` e `/confirm` | requestEmailChange / confirmEmailChange | 002, 055 | Auth + reauth |
| POST/DELETE | `/me/identities`, `/me/identities/{provider}` | linkIdentity / unlinkIdentity | 001 | Auth + reauth |
| GET | `/me/sessions` | listSessions | 054 | Auth |
| DELETE | `/me/sessions/{session_id}` | revokeSession | 054, 002 | Auth |
| POST | `/me/session-revocations` | revokeOtherSessions | 002, 054 | Auth |
| PUT/DELETE | `/me/push-tokens/{device_id}` | registerPushToken / unregisterPushToken | 037, 039 | Auth |
| GET | `/babies` | listBabies | 004 | O, C, R |
| POST | `/babies` | createBaby | 004, 005 | Auth (vira O) |
| GET | `/babies/{baby_id}` | getBaby | 004, 005 | O, C, R |
| PATCH | `/babies/{baby_id}` | updateBaby | 004, 005, 012 | O |
| DELETE | `/babies/{baby_id}` | deleteBaby | 045, 055 | O + reauth |
| GET | `/babies/{baby_id}/caregivers` | listCaregivers | 006 | O, C, R |
| PATCH | `/babies/{baby_id}/caregivers/{membership_id}` | updateCaregiverRole | 006, 055 | O |
| DELETE | `/babies/{baby_id}/caregivers/{membership_id}` | removeCaregiver | 006, 007, 055 | O; C/R só o próprio |
| POST | `/babies/{baby_id}/invitations` | createInvitation | 006, 055 | O |
| POST | `/babies/{baby_id}/invitations/{membership_id}/resend` | resendInvitation | 006 | O |
| POST | `/babies/{baby_id}/ownership-transfer` | transferOwnership | 006, 045, 055 | O + reauth |
| POST | `/invitations/inspect`, `/accept`, `/decline` | inspect/accept/declineInvitation | 006, 007 | Inv |
| GET | `/babies/{baby_id}/timeline` | getTimeline | 019, 010 | O, C, R |
| GET | `/babies/{baby_id}/events/{event_id}` | getEvent | 019, 020 | O, C, R |
| GET | `/babies/{baby_id}/events/{event_id}/history` | getEventHistory | 020, 055 | O, C |
| POST | `/sync/push` | pushMutations | 046, 047, 008, 009, 010, 015, 016, 017, 018, 020 | O, C (inclui `wake_event`) |
| GET | `/sync/pull` | pullChanges | 007, 046, 047, 019 | O, C, R (inclui `wake_event` e tombstones) |
| GET | `/babies/{baby_id}/predictions/sleep` | getSleepPredictions | 011, 012, 013 | O, C, R |
| GET/PUT/DELETE | `/babies/{baby_id}/sleep-preferences` | get/put/resetSleepPreferences | 014, 012 | leitura O, C, R; escrita O, C |
| GET | `/babies/{baby_id}/aggregates` | getAggregates | 028, 029, 010 | O, C, R (`night_awakenings` derivado) |
| GET/PUT | `/babies/{baby_id}/notification-preferences` | get/putNotificationPreferences | 037, 038, 040 | O, C, R (próprias) |
| GET | `/me/entitlements` | getEntitlements | 041, 043 | Auth |
| POST | `/subscriptions/purchases` | submitPurchase | 041, 042 | Auth |
| POST | `/subscriptions/restore` | restorePurchases | 042 | Auth |
| PUT/DELETE | `/subscriptions/family-seat` | assignFamilySeat / releaseFamilySeat | 043 | O (proposta; adicional não precisa ser cuidador) |
| GET | `/legal/documents` | listLegalDocuments | 003, 040 | Pub |
| GET/POST | `/me/consents` | listConsents / recordConsent | 003, 040, 055 | Auth |
| GET/POST | `/me/data-exports` | listDataExports / requestDataExport | 044, 055 | Auth |
| GET | `/me/data-exports/{export_id}` | getDataExport | 044 | Auth |
| POST | `/me/data-exports/{export_id}/download-links` | createExportDownloadLink | 044, 055 | Auth + reauth |
| GET/POST/DELETE | `/me/deletion-request` | get/request/cancelAccountDeletion | 045, 055 | POST: O + reauth (403 para C/R, PA-27); GET/DELETE: Auth |
| GET | `/me/audit-events` | listMyAuditEvents | 055 | Auth |
| GET | `/babies/{baby_id}/audit-events` | listBabyAuditEvents | 055, 020 | O |
| GET | `/reference-data` | getReferenceData | 016, 018, 008 | Auth (enums, limites e `policies`) |

### 5.1 Cobertura dos RFs do MVP

| RF | Coberto por | Observação |
|---|---|---|
| 001, 002, 003 | auth, `/me/*`, consentimentos | Conta ativa só após verificação de e-mail (ADR-0009; PA-01 resolvido). |
| 004, 005 | `/babies`, campos `age_calculation` e `age` | Idade calculada no servidor na data local do bebê, nunca persistida; `corrected_days` nulo = não se aplica (AD-19). Edição só do Owner (AD-25). |
| 006, 007 | caregivers, invitations, `/sync/pull` | Prévia do convite com rótulo reduzido do bebê. |
| 008..010, 015..018 | `/sync/push` (escrita), `/timeline`, `/aggregates` | Totais, wake windows e `night_awakenings` derivados de `WakeEvent`, nunca gravados (INV-04, AD-22). |
| 011..014 | `/predictions/sleep`, `/sleep-preferences` | Recálculo é efeito colateral das mutações; chega ao cliente como `sleep_prediction` no feed. |
| 019, 020 | `/timeline`, `/events/*`, `/events/{id}/history` | Edição e exclusão via push. |
| 028, 029 | `/aggregates` | Comparação só com o próprio histórico. |
| 037..040 | `/notification-preferences`, `/me/push-tokens`, `/me/consents` | Agendamento e entrega são internos (fora do contrato). |
| 041..043 | `/me/entitlements`, `/subscriptions/*` | Webhooks de loja fora do contrato. Assento adicional sem exigir vínculo de cuidador (AD-26). |
| 044, 045 | `/me/data-exports*`, `/me/deletion-request`, `DELETE /babies/{id}` | Assíncronos; reauth; exclusão de conta só Owner, `cascade` por padrão, com janela de arrependimento (AD-24). |
| 046, 047 | `/sync/*` | Seção 3. |
| 048 | fora da API | Analytics vai ao fornecedor; o contrato só expõe o consentimento `analytics_product`. |
| 050 | `Accept-Language`, `PATCH /me` (`locale`), chaves de mensagem | Localização no cliente. |
| 054, 055 | `/me/sessions`, `/me/session-revocations`, `/me/audit-events`, `/babies/{id}/audit-events` | Auditoria só leitura. |

## 6. Exemplos

Há exemplos no próprio OpenAPI para: cadastro, login, criação de bebê, push de sono (timer iniciado e parado offline) com fralda e exclusão, resposta mista de push (`applied`, `duplicate`, `rejected`), delta de pull com upsert e tombstone, `410` de cursor expirado, previsão, agregação semanal, preferências de notificação, entitlement free, revogação de consentimento e erros de cada classe.

## 7. Pontos em aberto

Dúvidas D-nn são as de `specs/product-spec.md` seção 9. PA-nn são novas, geradas por esta spec. Itens marcados **Resolvido** foram decididos no ADR-0009 e já estão no contrato.

| ID | Ponto | Impacto | Sugestão |
|---|---|---|---|
| PA-01 | **Resolvido (ADR-0009).** Verificação de e-mail no cadastro: `register` -> `202` uniforme -> `email/verify` -> sessão; conta ativa só após confirmação. | Onboarding (UX E2), RF-001. | Nada a fazer. |
| PA-02 | Cursor por bebê (contrato atual) ou por usuário (um pull para todos os bebês). Por bebê simplifica revogação (INV-14) e o snapshot, ao custo de N chamadas. | Cliente e ARCH-003. | Por bebê. |
| PA-03 | INV-19 diz "version por bebê é monotônica"; o contrato usa `version` por entidade e a ordem global vem do cursor. Confirmar que é suficiente para `base_version` e conflito. | Modelo de sync. | Manter por entidade. |
| PA-04 | **Resolvido (ADR-0009).** Sono sobreposto (D-05): aceito e sinalizado por padrão (`accept_and_warn`), configurável por flag para `reject` (`rejected`, `SLEEP_OVERLAP`). Exposto em `/reference-data` -> `policies.sleep_overlap_policy`. | RF-008-A6, RF-009-A3, RF-009-A8. | Nada a fazer; UX deve tratar as duas políticas. |
| PA-05 | **Em aberto (mantido por ADR-0009, decisão 3).** Amamentação em curso: o domínio exige `end_at` para `BREASTFEEDING`, mas RF-015-A2 prevê timer. O contrato exige `end_at` (o timer vive só no cliente até parar). Consequência: outro cuidador não vê a mamada em curso. A resposta do usuário sobre permitir `end_at` nulo foi ambígua; reabrir se o timer de mamada for necessário. | RF-015, RF-007. | Reabrir se o timer compartilhado for requisito. |
| PA-06 | **Resolvido em parte (ADR-0009).** Exclusão de conta de Owner com outros cuidadores: política `cascade` por padrão (apaga também para os demais, com `acknowledge_other_caregivers=true` e reauth); `block` e `transfer_ownership` por flag. Pendente: validação jurídica (DJ-09) e a opção "excluir só o vínculo do Owner" segue sem suporte. | RF-045, DJ-09. | Aguardar DJ-09. |
| PA-07 | **Resolvido (ADR-0009).** Janela de arrependimento mantida: `scheduled_for` e `DELETE /me/deletion-request`. Duração da janela é parâmetro (a definir com produto/jurídico). | RF-045. | Definir duração. |
| PA-08 | Assento premium (ADR-0005, D-04): `PUT/DELETE /subscriptions/family-seat` é **proposta** (`x-status: proposed`). Resolvido por ADR-0009: o adicional não precisa ser cuidador ativo (aceita `email` ou `membership_id`). Em aberto: o que ocorre se o titular cancelar, quem edita flags em produção (fora desta API), vínculo do assento por e-mail antes do cadastro do convidado e privacidade do e-mail. | RF-043. | Decidir cancelamento e fluxo do convite de assento. |
| PA-09 | `404` (sem vínculo) × `403 ACCESS_REVOKED` (revogado) distingue quem já teve acesso. Aceitável porque o ex-membro já conhecia o recurso; confirmar com segurança (SECURITY-REVIEW-001). | RF-004-A6, RF-006-A5. | Manter. |
| PA-10 | Obrigatoriedade de novo aceite (RF-003-A2): o contrato devolve `pending_consents`/`pending_required` e só bloqueia criação de bebê e aceite de convite (`403 CONSENT_REQUIRED`). Bloqueio global é decisão do cliente. | RF-003. | Confirmar o escopo do bloqueio do servidor. |
| PA-11 | Visibilidade entre cuidadores: nome (`display_name`) de quem registra aparece para todos; e-mail só para o Owner. Confirmar com privacidade (D-25, D-33). | RF-019-A5, RF-020-A7. | Manter. |
| PA-12 | `If-Match` opcional em `PATCH /babies/{id}` e `PUT /sleep-preferences`. Torná-lo obrigatório (`428`) é mais seguro e custa um GET prévio. | RF-004, RF-014. | Opcional no MVP. |
| PA-13 | Código HTTP de validação semântica: `400` único (atual) × `422`. | Todos os clientes. | Decidir antes de gerar SDK. |
| PA-14 | Foto do bebê (D-09): só `photo_ref` somente leitura; sem endpoint de upload no MVP. | RF-004. | Decidir D-09. |
| PA-15 | Notificações de rotina (D-28): categoria `routine` só tem `enabled`, antecedência e quiet hours; falta a estrutura do lembrete (horários, recorrência). `development_phase` fica `available=false` (D-10). | RF-037-A8. | Decidir D-28. |
| PA-16 | Quiet hours são por categoria (como no domínio) mas a UX as mostra como um único intervalo. O cliente pode replicar o valor em todas. Modelar quiet hours globais por usuário é alternativa. | RF-038. | Confirmar com UX. |
| PA-17 | Despertares noturnos (D-12): **resolvido no contrato** por `WakeEvent` + `night_awakenings` derivado (AD-22). Em aberto: valor do critério de suficiência (`night_awakenings_min_coverage_percent`), regra de inferência (`INFERRED`) no servidor e atribuição de sessões à meia-noite (D-13, regra de bucket na implementação). | RF-010, RF-028. | Definir critério; decidir D-13. |
| PA-18 | Mínimo de dados para tendência (D-27): o servidor informa `data_sufficiency.min_days_required`; a UX cita 3 dias. | RF-029. | Decidir D-27. |
| PA-19 | Domínio, regiões e ambientes (`api.nina.app` é placeholder); requisito de região Brasil (DJ-04). | `servers`. | Definir com infra. |
| PA-20 | Idempotência de `Idempotency-Key` guarda a resposta por 24 h; confirmar retenção e tamanho do armazenamento com backend. | AD-10. | Spike ARCH-003. |
| PA-21 | Perfis `SleepSession` etc. guardam `notes` livre: limite de 500 caracteres é suposição (SEC-043). | Validação. | Confirmar. |
| PA-22 | Cabeçalho de versão mínima do app (`426`) e canal de comunicação (loja) não estão definidos. | AD-01. | Definir política de suporte (D-41). |
| PA-23 | Eventos de analytics (RF-048) não são parte desta API; confirmar que o app envia direto ao fornecedor e que o SDK respeita o consentimento `analytics_product`. | RF-048. | Confirmar com PRIV-001. |
| PA-24 | Nomes de campo: o ADR-0009 cita camelCase (`ageCalculation`, `nightAwakenings`); o contrato é `snake_case` em todo o resto, então usa `age_calculation`, `chronological_days`, `corrected_days`, `correction_applied`, `night_awakenings`. | Clientes e SDKs. | Manter `snake_case` (consistência); confirmar com o autor do ADR. |
| PA-25 | Enums passaram de minúsculas (rascunho: `wet`, `breast`, `left`) para maiúsculas (`WET`, `BREASTFEEDING`, `LEFT`), conforme ADR-0009. Quebra em relação ao rascunho `1.0.0-draft.1`, ainda sem clientes. `sleep_type`, `source` de sono (`timer`/`manual`), `sex` e demais enums continuam em minúsculas. | Padronização de enums. | Decidir se os demais enums também migram para maiúsculas antes do congelamento. |
| PA-26 | `FeedingType` inclui `SOLID` e `OTHER`; para eles o contrato aceita `end_at` nulo e não há `side`/`volume_ml`/`milk_type`. Regras de validação e agregados (`/aggregates.feeding` só conta mama e mamadeira) para sólidos ficam por definir. | RF-015, RF-028. | Definir com produto. |
| PA-27 | "Somente o Owner exclui a conta" (ADR-0009): o contrato retorna `403 FORBIDDEN_ROLE` a quem tem só vínculo de Caregiver/ReadOnly; usuário sem nenhum vínculo pode excluir. Isso pode conflitar com o direito de eliminação do titular de dados (LGPD art. 18); um cuidador convidado só consegue sair do bebê (`DELETE caregivers/{id}`) e pedir exclusão ao suporte. | RF-045, DJ-09. | Validar com jurídico (DJ-09) antes do congelamento. |
| PA-28 | Janela de aplicação da idade corrigida (ADR-0005/0009): exposta como `policies.corrected_age_max_months` (nulo = sem limite). Valor padrão a definir pelo produto. | RF-005. | Definir. |
| PA-29 | `WakeEvent` fora de `/timeline` e de `GET /events/{id}`: só trafega em `/sync/pull` e é derivado em `night_awakenings`. Confirmar se a UI precisa listar despertares online sem sync, e quem gera `INFERRED`. | RF-010, RF-016. | Confirmar com UX. |

## 8. Validação do contrato

- `npx @redocly/cli@1.34.5 lint contracts/openapi.yaml`: sem erros nem avisos (configuração recomendada).
- `python3 -m openapi_spec_validator contracts/openapi.yaml`: OK.
- Sugestão para a CI: as duas verificações acima, mais geração de clientes (Swift e Kotlin) como smoke test e testes de contrato (Schemathesis ou Dredd) contra o BFF.
