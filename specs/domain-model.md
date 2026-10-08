# Modelo de Domínio — Nina (MVP)

Status: rascunho (REQ-001) · Data: 2026-10-08
Fontes: discovery seções 7, 8, 13; `docs/project/specification.md`; ADR-0002, 0003, 0004.
Marcação: **[D]** = entidade/evento do discovery; **[P]** = proposta derivada de um RF (necessária para cumpri-lo, mas ausente da lista do discovery; validar em ARCH-001/DB-001).
Termos em `specs/glossary.md`. Identificadores em inglês.

## 1. Módulos (ADR-0002)

| Módulo | Entidades principais |
|---|---|
| Identity | User, AuthSession (device), Credential, RecoveryRequest [P] |
| Family | Baby, CaregiverMembership |
| Tracking | SleepSession, FeedingSession, PumpingSession, DiaperEvent, SyncMutation/Tombstone [P] |
| SleepIntelligence | SleepSchedulePreference [P], SleepPrediction, referência por idade (config) |
| Notifications | NotificationPreference, NotificationJob |
| Subscriptions | Subscription |
| Privacy | ConsentRecord, DataExportRequest [P], AccountDeletionRequest [P], AuditEvent |

Fora do MVP (não modelados): DevelopmentPhase, BabyPhaseWindow, Signal/Skill/ActivityDefinition e progressos, DiaryEntry, MediaAsset de diário, ContentItem, AudioTrack, AIConversation/AIMessage. A foto opcional do bebê (RF-004) usa uma referência simples (`photo_ref`); ver dúvida D-09.

## 2. Entidades do MVP

Convenções: todo registro sincronizável tem `id` (UUID gerado no cliente), `version` (canônica, por bebê), `created_at`, `updated_at`, `deleted_at` (tombstone), `created_by`, `last_modified_by`. Instantes em UTC; eventos guardam também `tz` (IANA) vigente (RB-014).

### 2.1 Identity

**User [D]** — `id`, `email` (único, normalizado), `locale`, `timezone`, `status` (active, pending_deletion, deleted), `created_at`.
**Credential [P]** — hash de senha (algoritmo adaptativo), nunca em claro nem em log. Detalhes de mecanismo pertencem à ARCH-001.
**AuthSession [P]** (RF-054) — `id`, `user_id`, `device_id`, `device_label`, `platform`, `created_at`, `last_seen_at`, `revoked_at`.
**RecoveryRequest [P]** (RF-002) — token de uso único, `expires_at`, `used_at`.

### 2.2 Family

**Baby [D]** — `id`, `owner_id`, `display_name` (nome ou apelido), `birth_date`, `due_date` (opcional), `sex` (opcional), `timezone`, `photo_ref` (opcional).
**CaregiverMembership [D]** — `baby_id`, `user_id` (nulo enquanto convite pendente para e-mail sem conta [P]), `invited_email` [P], `role` (Owner | Caregiver | ReadOnly), `status` (pending | active | revoked | declined [P]), `invited_at`, `accepted_at`, `revoked_at` [P]. O convite é modelado como membership `pending`; não há entidade Invitation separada.

### 2.3 Tracking

**SleepSession [D]** — `baby_id`, `start_at`, `end_at` (nulo = em aberto), `sleep_type` (nap | night), `method_or_place` (opcional, texto curto ou enum a definir), `notes` (opcional), `source` (timer | manual).
**FeedingSession [D]** — `baby_id`, `feeding_type` (breast | bottle), `start_at`, `end_at` (obrigatório para breast; opcional para bottle), `side` (left | right | both; só breast), `volume_ml` (só bottle), `milk_type` (opcional; só bottle), `notes`.
**PumpingSession [D]** — `baby_id`, `start_at`, `end_at`, `volume_ml` (opcional), `side` [P, opcional].
**DiaperEvent [D]** — `baby_id`, `occurred_at`, `diaper_type` (valores a definir; dúvida D-08), `notes`.
**Tombstone [P]** — `entity_type`, `entity_id`, `baby_id`, `deleted_at`, `version`, `expires_at` (janela de retenção, dúvida D-06).
**SyncMutation [P]** (cliente e servidor) — `mutation_id` (UUID), `device_id`, `entity_type`, `entity_id`, `op` (create | update | delete), `base_version`, `client_created_at`, `payload`, `state` (pending | sent | acked | conflict).

### 2.4 SleepIntelligence

**SleepSchedulePreference [P]** (RF-014) — `baby_id`, `target_nap_count` (opcional), `bedtime_from`, `bedtime_to` (hora local, opcionais).
**SleepPrediction [D]** — `baby_id`, `kind` (next_nap | bedtime), `predicted_start`, `predicted_end` (opcional), `confidence` (valor ou faixa), `explanation` [P] (chave de mensagem + parâmetros, localizável), `model_version`, `computed_at`, `inputs_version` [P] (versão do histórico usada). Derivada e descartável; nunca é fonte de verdade.

### 2.5 Notifications

**NotificationPreference [D]** — `user_id`, `baby_id`, `category` (nap | bedtime | routine | development_phase | system), `enabled`, `lead_time_minutes`, `quiet_hours_start`, `quiet_hours_end`. Categoria `development_phase` existe no cadastro, mas não gera envio no MVP (D-10).
**NotificationJob [D]** — `event_key` (determinística: categoria + bebê + alvo previsto), `user_id`, `scheduled_at`, `status` (scheduled | sent | failed | cancelled | skipped_quiet_hours), `attempts`, `last_error_code` (sem PII), `provider_ref`.

### 2.6 Subscriptions

**Subscription [D]** — `owner` (user_id; ou family_id conforme D-04), `plan` (free | premium), `store` (apple | google | none), `store_transaction_ref`, `status` (active | grace | expired | cancelled | revoked), `valid_until`, `last_validated_at`.
Entitlement efetivo = função de Subscription no servidor (RB-009).

### 2.7 Privacy

**ConsentRecord [D]** — `user_id`, `purpose` (terms | privacy_policy | analytics | outras finalidades opcionais), `document_version`, `granted_at`, `revoked_at`, `channel` (app/versão).
**DataExportRequest [P]** — `user_id`, `requested_at`, `status`, `file_ref` (URL assinada de curta duração), `expires_at`, `schema_version`.
**AccountDeletionRequest [P]** — `user_id`, `requested_at`, `status`, `scheduled_for` (janela de arrependimento, D-07), `completed_at`.
**AuditEvent [D]** — `actor_user_id`, `action`, `entity_type`, `entity_id`, `baby_id` (opcional), `timestamp`, `metadata_safe` (sem PII e sem conteúdo de diário/foto), `device_id` (opcional).

## 3. Relacionamentos

```
User 1─N AuthSession
User N─N Baby            (via CaregiverMembership: role, status)
Baby 1─N SleepSession | FeedingSession | PumpingSession | DiaperEvent
Baby 1─1 SleepSchedulePreference
Baby 1─N SleepPrediction (apenas as vigentes; histórico descartável)
User 1─N NotificationPreference (por baby_id e categoria)
NotificationPreference 1─N NotificationJob (via event_key)
User 1─N ConsentRecord | DataExportRequest | AccountDeletionRequest
User (ou família) 1─N Subscription
AuditEvent referencia User (ator) e qualquer entidade auditada
```
Propriedade: todo dado de tracking pertence a exatamente um Baby. Dados de usuário (consentimento, preferências, sessões) pertencem a um User. A autorização de qualquer consulta é por (user, baby, role) no servidor.

## 4. Invariantes

Tracking e sono
- INV-01: `end_at` >= `start_at` quando presente. (RF-008)
- INV-02: no máximo uma SleepSession em aberto por bebê. (RF-009)
- INV-03: sessões de sono do mesmo bebê não se sobrepõem; a política em caso de sobreposição (rejeitar ou sinalizar) é dúvida D-05. (RF-008, RF-010)
- INV-04: duração e wake window são sempre derivadas de eventos reais, nunca persistidas como verdade independente. (RF-010, RB-001)
- INV-05: SleepPrediction nunca altera nem substitui SleepSession; o recálculo é puro em função do histórico e da idade. (RB-001, RB-002)
- INV-06: alterar ou excluir evento passado dispara recálculo de previsões futuras e de agregados. (RB-003, RF-012)
- INV-07: `volume_ml` > 0 quando informado; `side` presente em amamentação; campos de bottle não aparecem em breast e vice-versa. (RF-015, RF-016)
- INV-08: nenhum evento de tracking é criado sem Baby existente e sem vínculo ativo do autor com papel Owner ou Caregiver. (RB-006, RF-006)

Família e acesso
- INV-09: todo Baby tem exatamente um Owner ativo. (RB-007)
- INV-10: um usuário tem no máximo um vínculo não revogado por bebê.
- INV-11: o vínculo só passa a `active` após aceite do convidado; criador do bebê nasce Owner ativo. (RB-006)
- INV-12: apenas Owner remove cuidador, transfere propriedade ou exclui o bebê. (RB-007)
- INV-13: ReadOnly nunca cria, edita nem exclui eventos nem altera preferências do bebê. (RF-006)
- INV-14: ao revogar vínculo, sessões/tokens/cache de acesso àquele bebê são invalidados e o cursor de sync do usuário para esse bebê deixa de ser aceito. (RB-015, ADR-0003)

Idade e datas
- INV-15: `birth_date` e `due_date` são campos distintos e independentes; `due_date` nunca é derivada nem sobrescreve `birth_date`. (RB-004)
- INV-16: `birth_date` não pode estar no futuro; `due_date` aceita datas futuras apenas enquanto o bebê ainda não nasceu (se o app permitir perfil pré-natal, D-11).
- INV-17: instantes são persistidos em UTC com `tz` contextual; trocar o fuso do bebê não reescreve eventos históricos. (RB-014)

Sync
- INV-18: toda mutação tem UUID único; reaplicar a mesma mutação não altera o estado (idempotência). (RNF-007, ADR-0003)
- INV-19: `version` por bebê é monotônica e atribuída apenas pelo servidor. (ADR-0003)
- INV-20: exclusão gera tombstone retido até a convergência; um update atrasado sobre entidade com tombstone não a ressuscita. (RB-008)
- INV-21: toda edição concorrente resolvida automaticamente deixa registro em auditoria. (RF-047, RF-020)

Notificações
- INV-22: no máximo um NotificationJob por `event_key`; retry não cria nova entrega. (RF-039, RNF-011)
- INV-23: nenhum aviso é entregue dentro de quiet hours do fuso do bebê/família (adiado ou descartado conforme regra a definir). (RB-010)
- INV-24: mudança de previsão cancela e reagenda o job correspondente. (RF-012, RF-037)

Assinatura
- INV-25: entitlement premium só existe se validado pelo servidor; estado informado pelo cliente não o concede. (RB-009)
- INV-26: restaurar compra em novo dispositivo reaproveita a mesma Subscription, sem criar cobrança nova. (RF-042)

Privacidade e auditoria
- INV-27: ConsentRecord é append-only; revogação adiciona `revoked_at`, não apaga o histórico. (RF-003)
- INV-28: AuditEvent é imutável e não contém PII nem conteúdo livre de eventos. (RF-055, RNF-008)
- INV-29: analytics e logs não contêm nome do bebê, e-mail, notas livres nem fotos. (RF-048, RB-011)
- INV-30: exportação inclui timestamps, fuso e `schema_version`. (RB-014)
- INV-31: exclusão de conta remove ou anonimiza os dados do titular, preservando apenas o que a lei exigir (registros de auditoria mínimos, D-07). (RF-045)

## 5. Eventos de domínio

### 5.1 Do discovery (seção 8) aplicáveis ao MVP [D]

| Evento | Emitido por | Consumidores (propostos) |
|---|---|---|
| `BabyCreated` | Family | Tracking (preferências padrão), Notifications (padrões), Audit |
| `CaregiverInvited` | Family | Notifications (e-mail/push de convite), Audit |
| `CaregiverAccepted` | Family | Sync (libera cursor), Audit |
| `CaregiverRemoved` | Family | Identity (invalida sessões/tokens do bebê), Sync, Notifications (cancela jobs), Audit |
| `SleepStarted` | Tracking | SleepIntelligence (suspende previsões), Notifications (cancela aviso de soneca) |
| `SleepEnded` | Tracking | SleepIntelligence (recalcula), Notifications |
| `SleepUpdated` / `SleepDeleted` | Tracking | SleepIntelligence (recalcula), agregados, Audit |
| `SleepPredictionRecalculated` | SleepIntelligence | Notifications (reagendam), clientes via sync |
| `FeedingLogged` / `PumpingLogged` / `DiaperLogged` | Tracking | agregados de gráficos |
| `NapApproaching` / `BedtimeApproaching` | Notifications (scheduler) | entrega push |
| `SubscriptionActivated` / `SubscriptionExpired` | Subscriptions | entitlement, clientes |
| `ConsentGranted` / `ConsentRevoked` | Privacy | Analytics (liga/desliga), Audit |
| `DataExportRequested` | Privacy | worker de exportação |
| `AccountDeletionRequested` | Privacy | worker de exclusão, Identity (encerra sessões), Audit |

Fora do MVP (não implementar): `SignalObserved`, `SkillObserved`, `ActivityCompleted`, `DiaryEntryCreated`, `WeeklySummaryReady`, `DevelopmentPhaseApproaching`.

### 5.2 Propostos [P]

`UserRegistered`, `SessionStarted`, `SessionRevoked`, `PasswordRecoveryRequested`, `FeedingUpdated/Deleted`, `PumpingUpdated/Deleted`, `DiaperUpdated/Deleted`, `SleepScheduleChanged` (RF-014), `NotificationPreferenceChanged`, `NotificationDelivered/Failed` (RF-039), `RoleChanged`, `OwnershipTransferred`, `SyncConflictResolved` (RF-047), `DataExportReady`, `AccountDeleted`. Cada um existe para atender explicitamente a um RF ou à auditoria (RF-055); confirmar em ARCH-001.
Entrega interna via outbox transacional (ADR-0002, MSG-001); consumidores idempotentes.

## 6. Idade cronológica e idade corrigida

Status: **mecanismo proposto; política de prematuros em aberto (D-01)**.

Definições
- Idade cronológica(t) = intervalo entre `birth_date` e a data `t`, avaliada no fuso do bebê. Exibível em dias, semanas completas e meses completos; unidade por faixa é decisão de UX.
- Idade corrigida(t), quando aplicável = idade cronológica(t) menos o intervalo entre `birth_date` e `due_date` (ou seja, intervalo entre a DPP e `t`). Calculada em tempo de consulta, nunca persistida como dado independente.
- Se `due_date` ausente: idade corrigida não existe; todas as regras usam a idade cronológica.
- Se `due_date` <= `birth_date` (nascimento a termo ou depois): não há correção; idade corrigida = idade cronológica (nunca negativa).
- Se `t` < `due_date` (ainda antes da DPP): idade corrigida é negativa; a exibição e o uso nas regras são tratados como "pré-termo" e dependem da política D-01.

Regras de uso
- RIC-01: `due_date` é guardada separadamente de `birth_date` e preservada para regras configuráveis futuras (V1: fases de desenvolvimento). (RB-004, RF-005)
- RIC-02: a idade usada pelo motor de sono (ADR-0004) é a idade corrigida quando aplicável e a cronológica caso contrário. A faixa da tabela de referência é decidida pela idade efetiva.
- RIC-03: a exibição ao usuário informa qual idade está sendo mostrada (cronológica e, quando aplicável, corrigida), sem ranquear ou comparar com outras crianças. (RB-013)
- RIC-04: nenhuma saída de idade corrigida é apresentada como diagnóstico ou meta de desenvolvimento. (RB-005, RNF-014)
- RIC-05: editar `birth_date`, `due_date` ou fuso recalcula idade e previsões, sem alterar eventos históricos. (RB-003, RB-014)
- RIC-06: os marcos de transição (mudança de faixa etária) usam a data local do bebê, para que a troca de fuso do dispositivo não altere a faixa.

Em aberto (resolver com especialista clínico e produto; ver D-01)
- Critério de "aplicável": a partir de qual prematuridade a correção vale (a idade gestacional ao nascer não é campo do discovery; só `birth_date` e `due_date`).
- Até quando corrigir (limite de idade) e se a correção termina de forma abrupta ou gradual.
- Se o cuidador pode desligar a correção manualmente.
- Se a idade corrigida deve afetar a tabela de referência do motor de sono desde o primeiro dia (ADR-0004 diz "corrigida quando aplicável", sem política).
- Comportamento para `due_date` informada por engano (ex.: muito distante do nascimento): validação ou aviso.

## 7. Estados e transições relevantes

SleepSession: `aberta` --(parar timer / informar fim)--> `fechada`; `fechada` --(editar)--> `fechada`; qualquer --(excluir)--> `tombstone`.
CaregiverMembership: `pending` --(aceite)--> `active`; `pending` --(recusa/expira)--> `declined`; `active` --(Owner remove ou usuário sai)--> `revoked`.
NotificationJob: `scheduled` --> `sent` | `failed` (retry com backoff) --> `dead-letter`; `scheduled` --> `cancelled` (previsão mudou) | `skipped_quiet_hours`.
Subscription: `active` --> `grace` --> `expired`; `active` --> `cancelled`/`revoked`; `expired` --(restauração/nova compra)--> `active`.
AccountDeletionRequest: `requested` --> `scheduled` --> `completed` (ou `cancelled` dentro da janela, se existir).

## 8. Dúvidas abertas do modelo

| ID | Dúvida | Impacto |
|---|---|---|
| D-01 | Política de idade corrigida para prematuros (critério, limite de idade, opt-out, uso no motor de sono). | RF-005, RF-011, ADR-0004 |
| D-04 | Entitlement é do usuário ou da família/bebê? Quem herda o premium (RF-043)? | Subscription, autorização |
| D-05 | Sobreposição de sessões de sono: rejeitar, mesclar ou apenas sinalizar? | INV-03, UX, sync |
| D-06 | Janela de retenção de tombstone e formato do cursor (pendências do ADR-0003). | INV-20 |
| D-07 | Janela de arrependimento da exclusão e o que a lei obriga reter (auditoria, recibos de assinatura). | RF-045, INV-31 |
| D-08 | Conjunto de tipos de fralda e de métodos/locais de sono (enum ou texto livre). | RF-008, RF-018 |
| D-09 | Foto de perfil do bebê: armazenamento, tratamento de mídia e escopo no MVP. | RF-004 |
| D-10 | Categoria "fases de desenvolvimento" em RF-037 sem conteúdo no MVP: expor na UI ou ocultar? | RF-037 |
| D-11 | Permitir perfil antes do nascimento (só DPP)? | INV-16, RF-004 |
| D-12 | Como registrar despertares noturnos (sessões separadas, campo de contagem ou eventos). | RF-010 |
| D-13 | Regra de atribuição a um dia de sessões que cruzam meia-noite e limite de "dia" (corte). | RF-010, RF-028 |

(Os IDs D-02 e D-03 estão reservados em `specs/product-spec.md` seção 9; a lista consolidada está lá.)
