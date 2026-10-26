# Especificação da API — Nina BFF (MVP)

Status: **contrato v1.0.x congelado para o MVP; patch atual 1.0.1** (ver seção 8) · Data: 2026-10-08
Contrato: `contracts/openapi.yaml` (OpenAPI 3.1, `info.version` 1.0.1, 70 operações em 54 caminhos). Este documento explica decisões, convenções, rastreio e pontos em aberto. Em divergência, o OpenAPI prevalece sobre a descrição, e ambos prevalecem sobre exemplos.
Fontes: `specs/product-spec.md`, `domain-model.md`, `ux-spec.md`, `privacy-security-spec.md`, ADR-0002, 0003, 0005, 0007, 0008, 0009, 0010, `docs/project/architecture.md`.
Revisão ADR-0009 (2026-10-08): convenções de contrato e decisões de domínio aplicadas (idade, enums, `WakeEvent`, flags de sono sobreposto e exclusão de conta, permissões do Owner, assento premium). O contrato usa `snake_case`; os nomes camelCase do ADR (`ageCalculation`, `nightAwakenings`...) viram `age_calculation`, `night_awakenings`... (confirmado no ADR-0010, decisão 1).
Revisão v1.0.1 (2026-10-08): endurecimento de segurança do contrato conforme `specs/security-review-001.md` (SR-013, SR-014 parte de contrato, SR-015, SR-016, SR-020, SR-021), **somente com mudanças aditivas ou restritivas compatíveis**; o que seria quebra está na seção 8.4.
Revisão ADR-0010 (2026-10-08): fechamento do contrato v1. `snake_case` confirmado; todos os enums em MAIÚSCULAS; exclusão de conta só para Owner ativo e caminho separado de requisição de privacidade para os demais; janela de arrependimento de 7 dias exposta; PA-05 resolvido (`end_at` obrigatório na mamada); padrões 24 meses e 240 minutos parametrizáveis; versão 1.0.0.

Escopo: API que o BFF (.NET) expõe aos apps iOS e Android (ADR-0002). Fora do contrato: API interna BFF↔domínio, webhooks de loja (App Store Server Notifications, Google RTDN; ARCH-001), envio de analytics (RF-048, vai ao fornecedor) e entrega de push (APNs/FCM).

## 1. Decisões

| ID | Decisão | Motivo |
|---|---|---|
| AD-01 | Prefixo `/v1` no caminho. Mudança aditiva não muda a versão major; quebra cria `/v2` (regras na seção 8). Cliente ignora campos e tolera valores de enum desconhecidos. Descontinuação por `Deprecation` e `Sunset`. `426 CLIENT_UPGRADE_REQUIRED` para app abaixo do mínimo. | App móvel não atualiza no mesmo instante do servidor. |
| AD-02 | Auth por bearer. Access token ≤ 15 min; refresh opaco, rotativo, uso único, com detecção de reuso (revoga a família). **Perfil do JWT (v1.0.1, AD-31):** algoritmo assimétrico fixado (`EdDSA` ou `ES256`), `typ=at+jwt`, `kid` com rotação, claims `iss`, `aud`, `sub`, `sid`, `jti`, `iat`, `nbf`, `exp`, tolerância de relógio ≤ 60 s, revogação por `sid` a cada requisição. | SEC-011, ADR-0007, SR-013. |
| AD-03 | Cadastro por e-mail devolve `202` uniforme; **a conta só fica ativa após confirmação de e-mail** e a sessão só nasce em `POST /auth/email/verify`. **Confirmado** (ADR-0009, decisão 1). | RF-001-A2 (anti-enumeração) é incompatível com devolver `201` + sessão para e-mail novo. PA-01 resolvido. |
| AD-04 | Google e Apple: o app envia `id_token` + `nonce`; o servidor valida. Mesmo e-mail em conta existente: `409 IDENTITY_LINK_REQUIRED`, sem fusão; vínculo só autenticado, em `POST /me/identities` com reauth. | ADR-0007 (evitar account takeover). |
| AD-05 | Reautenticação por `POST /auth/reauthenticate` devolve `reauth_token` de **no máximo 300 s**, enviado em `X-Reauth-Token`, **vinculado à sessão (`sid`), a um `scope` por operação e de uso único (`jti`)** (v1.0.1; AD-32). Exigido em exportar (download), excluir conta/bebê, criar requisição de privacidade, transferir propriedade, trocar senha/e-mail e vincular/desvincular identidade. | RF-044, RF-045, UX 4.10, SR-013. |
| AD-06 | Erros em RFC 7807 com `code` estável em `UPPER_SNAKE_CASE`, `errors[]` por campo e `request_id`. `title`/`detail` não são para exibição; o cliente localiza por `code`. | RF-050-A5. |
| AD-07 | Autorização por (usuário, bebê, papel). Sem vínculo: `404` uniforme. Com vínculo e papel insuficiente: `403 FORBIDDEN_ROLE`. Vínculo revogado: `403 ACCESS_REVOKED`. | RF-004-A6 (sem revelar existência) e RF-006-A5 (403 após revogação). Ver PA-09. |
| AD-08 | **Escrita de eventos de tracking só por `POST /sync/push`**, inclusive online (lote de 1 mutação). Leitura por `timeline`, `events/{id}` e `sync/pull`. Não há `POST/PATCH/DELETE /events`. **Confirmado** (ADR-0009, decisão 2). | Um único caminho de escrita elimina divergência de regras de conflito e idempotência entre online e offline. |
| AD-09 | Baby, preferências de sono, notificações, cuidadores e privacidade são REST **online** (UX 4.13: convidar, assinar, exportar e excluir exigem conexão). Mudanças nesses recursos chegam aos outros dispositivos pelo feed de sync (`BABY`, `SLEEP_PREFERENCES`, `SLEEP_PREDICTION`). | Só o tracking precisa operar offline (RF-046). |
| AD-10 | Idempotência: `Idempotency-Key` (UUID, retenção 24 h) nos `POST` com efeito colateral não-sync; mesmo corpo repete a resposta (`Idempotent-Replayed: true`); corpo diferente dá `422 IDEMPOTENCY_KEY_REUSE`. Sync idempotente por `mutation_id`. | RNF-007, RNF-011. |
| AD-11 | Paginação de listas: `limit` (1..100, padrão 50) + `page_token` opaco; resposta `page {has_more, next_page_token}`. O feed de sync usa `cursor` (mecanismo distinto, ver 3). | Evita confundir paginação de leitura com posição no change log. |
| AD-12 | Concorrência otimista nos recursos REST editáveis: `ETag`/`If-Match` com a `version`; divergência dá `412 VERSION_CONFLICT`. `If-Match` é opcional (PA-12). | Edição de bebê e preferências por dois cuidadores. |
| AD-13 | Datas: instantes UTC (RFC 3339 com `Z`), `tz` IANA contextual, datas civis `YYYY-MM-DD`, horas locais `HH:MM`, volume sempre em `ml`. | RB-014, RNF-010. |
| AD-14 | A API devolve chaves de mensagem (`explanation.key`, `disclaimer_key`) e parâmetros; texto e idioma ficam no cliente. Previsão sem percentual, só `BUILDING \| FAIR \| GOOD`. | RF-050-A4, RNF-014, UX 4.5. |
| AD-15 | Valores vigentes dos enums extensíveis (fralda, alimentação, leite), métodos de sono, limites (volumes máximos, TTLs, critério de despertares) e **políticas por flag** (`policies`, inclusive `deletion_grace_days`, `corrected_age_max_months` e prazos de privacidade) vêm de `GET /reference-data`. | ADR-0005 (parâmetros em banco), ADR-0009. Atualizada pela AD-20. |
| AD-16 | Entitlement e feature flags em `GET /me/entitlements`, calculados no servidor, com `cache_expires_at` para uso offline. Recurso desconhecido pelo cliente é tratado como bloqueado. | RB-009, ADR-0005, RF-041-A6. |
| AD-17 | Rate limit por IP, conta e dispositivo (SEC-040): `429 RATE_LIMITED` com `Retry-After`, **declarado em todas as 70 operações** (v1.0.1). Cotas de volume: `429 QUOTA_EXCEEDED`. Sync: máx. 100 mutações e 256 KiB por push (`413`), mais cota diária e teto de entidades por bebê (AD-33). | SEC-040, SEC-042, SR-013, SR-014. |
| AD-18 | Cabeçalhos: `X-Request-Id` (resposta, correlação), `Accept-Language`, `X-Reauth-Token`, `Idempotency-Key`, `If-Match`/`ETag`. Nenhum token em URL ou log. | SEC-012, RNF-008. |
| AD-19 | **Idade**: persistem-se só `birth_date` e `due_date`; a idade corrigida nunca é persistida. `Baby.age_calculation {chronological_days, corrected_days (nullable), correction_applied}` é a fonte canônica em dias; `Baby.age` (semanas/meses) é só decomposição para exibição. `corrected_days = chronological_days - (due_date - birth_date)` apenas se `birth_date < due_date` e dentro da janela (parâmetro em banco, `policies.corrected_age_max_months`, padrão inicial 24 meses, ADR-0010); caso contrário `null` e `correction_applied=false`. | ADR-0009; ADR-0005; resolve D-01 no contrato. |
| AD-20 | **Convenção de nulos** (texto em `info.description`): `null` = não se aplica ou indisponível/desconhecido (documentado por campo); `0` = zero conhecido, nunca "não informado"; `UNSPECIFIED` = usuário não especificou; `UNKNOWN` = sistema não sabe (migração; reservado, cliente não envia). | ADR-0009. |
| AD-21 | **Enums extensíveis** (`x-extensible-enum`): `DiaperType` (`WET`, `DIRTY`, `MIXED`, `DRY`, `UNSPECIFIED`), `FeedingType` (`BREASTFEEDING`, `BOTTLE`, `SOLID`, `OTHER`), `MilkType` (`BREAST_MILK`, `FORMULA`, `MIXED`, `OTHER`, `UNSPECIFIED`; `UNKNOWN` reservado). `milk_type` só existe com `feeding_type=BOTTLE`; nos demais é `null`. Cliente tolera valores desconhecidos. Lado da mama: `BreastSide` (`LEFT`, `RIGHT`, `BOTH`). Valores em maiúsculas (ver AD-27). | ADR-0009. Tipos de fralda e leite são classificações de domínio, separadas dos valores derivados (idade corrigida, despertares). |
| AD-22 | **Despertares**: entidade `WakeEvent` (`id`, `sleep_session_id`, `started_at`, `ended_at`, `duration_seconds`, `source` = `MANUAL`\|`INFERRED`\|`IMPORT`) é a fonte da verdade, sincronizável (`entity_type=WAKE_EVENT` em push e pull, `version`, tombstone; excluir a `SLEEP_SESSION` gera tombstone dos seus despertares) e com correção manual. `night_awakenings` (em `SleepSession`, buckets de `/aggregates`) e `night_awakenings_avg` (em `summary`/`deltas`) são **derivados**: `null` = dados insuficientes; `0` = acompanhamento suficiente e nenhum despertar; `N>0` = quantidade. O critério de suficiência é parâmetro editável em banco: `limits.night_awakenings_min_session_minutes` (padrão inicial **240**, ADR-0010; substitui o rascunho `night_awakenings_min_coverage_percent`). | ADR-0009; resolve D-12 no contrato (PA-17). |
| AD-23 | **Sono sobreposto governado por flag** `sleep_overlap_policy` em `/reference-data` -> `policies`: `ACCEPT_AND_WARN` (padrão; `APPLIED` + `resolution=KEPT_BOTH` + `warnings[SLEEP_OVERLAP]`) ou `REJECT` (`REJECTED`, `problem.code=SLEEP_OVERLAP`, `retryable=false`). | ADR-0009, decisão 5; resolve D-05 (PA-04). |
| AD-24 | **Exclusão de conta**: somente **Owner ativo de um bebê** (`403 FORBIDDEN_ROLE` para Caregiver, ReadOnly e quem não tem bebê, com `privacy_request_path` apontando para o caminho de privacidade, AD-28); reautenticação (`X-Reauth-Token`); **janela de arrependimento de 7 dias** (`policies.deletion_grace_days`, padrão 7; `AccountDeletion.scheduled_for = requested_at + grace_days`; cancelável por `DELETE /me/deletion-request`; nada é executado antes de `scheduled_for`); política por flag `account_deletion_policy` (`policies` e `AccountDeletion.policy`): `CASCADE` (padrão), `BLOCK`, `TRANSFER_OWNERSHIP`. Em `CASCADE` com outros cuidadores ativos exige `acknowledge_other_caregivers=true` (a UI informa que os dados do bebê serão apagados também para eles); sem isso, `409 OWNER_DECISION_REQUIRED` com `deletion_policy`. Validação jurídica pendente (DJ-09). | ADR-0009 (decisões 4, 6 e 8), ADR-0010 (decisões 3 e 6); substitui o padrão `block` do ADR-0008. |
| AD-25 | **Perfil do bebê**: só o Owner edita (`PATCH /babies/{id}`; `403 FORBIDDEN_ROLE` para Caregiver e ReadOnly). | ADR-0009, decisão 6. |
| AD-26 | **Assento premium** (titular + 1): o adicional **não precisa ser cuidador ativo**; `PUT /subscriptions/family-seat` aceita `membership_id` **ou** `email`; remover/revogar cuidador não remove o assento. | ADR-0009, decisão 7; ADR-0005. |
| AD-27 | **Enums em MAIÚSCULAS** (ADR-0010, decisão 2): todos os valores de enum (`sleep_type`, `sex`, `source`, papéis, `status`, `platform`, `op`, `entity_type`, `period`, `kind`...) são maiúsculos em requisições, respostas, parâmetros de consulta e exemplos. Chaves de objeto, nomes de campo e chaves de mensagem continuam em `snake_case`/minúsculas. **Qualquer enum pode ganhar valores em mudança aditiva**; o cliente tolera valores desconhecidos em respostas (exibe genérico ou ignora, nunca falha nem descarta o registro) e o servidor rejeita valor desconhecido na entrada com `400 VALIDATION_FAILED`. | ADR-0009, ADR-0010. Resolve PA-25. |
| AD-28 | **Requisição de privacidade do titular** (ADR-0010, decisão 3): `POST/GET /me/privacy-requests` e `GET/DELETE /me/privacy-requests/{request_id}`. Tipos `ACCESS`, `CORRECTION`, `EXPORT`, `ANONYMIZATION`, sempre sobre os **dados pessoais do próprio usuário** (conta, sessões, consentimentos, tokens, atribuição de autoria), **sem apagar dados do bebê**. Reuso: `ACCESS`/`EXPORT` geram um `DataExport` com `scope=PERSONAL_ONLY` (`data_export_id`; `/me/data-exports` ganha o campo `scope`, e quem não é Owner só obtém `PERSONAL_ONLY`); a correção de campos editáveis segue em `PATCH /me` e `/me/email-changes`. `ANONYMIZATION` encerra os vínculos do usuário com bebês, anonimiza a autoria dos eventos que registrou (os eventos permanecem para os demais cuidadores), reaproveita a janela de arrependimento (`scheduled_for`, cancelável) e responde `409 ACCOUNT_DELETION_REQUIRED` a Owner ativo (que usa a exclusão de conta). Reautenticação obrigatória (`X-Reauth-Token`). Status: `REQUESTED`, `IN_PROGRESS`, `SCHEDULED`, `COMPLETED`, `REJECTED`, `CANCELLED`; prazo `due_at = requested_at + policies.privacy_request_sla_days` (padrão 15) e confirmação em `privacy_request_ack_hours` (padrão 48). Prazos e retenções legais a validar (DJ-07, DJ-09). | ADR-0010; privacy-security-spec 5; LGPD art. 18. Resolve PA-27. |
| AD-29 | **Mamada: `end_at` obrigatório** em `BREASTFEEDING` (ADR-0010, decisão 5); o timer vive só no cliente até parar. Nos demais tipos de alimentação `end_at` segue opcional. | Resolve PA-05. |
| AD-30 | **Padrões configuráveis** (ADR-0010, decisão 4 e 6): `age.corrected_window_months = 24` (`policies.corrected_age_max_months`), `sleep.night_awakenings.min_session_minutes = 240` (`limits.night_awakenings_min_session_minutes`) e `privacy.deletion_grace_days = 7` (`policies.deletion_grace_days`) são **valores iniciais, customizáveis em banco** e publicados em `GET /reference-data`. O cliente nunca embute esses números. | ADR-0005, ADR-0010. Resolve PA-28. |
| AD-31 | **Perfil do JWT de acesso** (v1.0.1, SR-013.4). O cliente o trata como opaco. Servidor: `alg` fixado em configuração (`EdDSA`/Ed25519 ou `ES256`; `none`, `HS*`, `RS256` recusados; o `alg` do cabeçalho nunca é confiado), `typ=at+jwt`, `kid` obrigatório; claims `iss=https://api.nina.app`, `aud=nina-api`, `sub`, `sid`, `jti`, `iat`, `nbf`, `exp` (`exp-iat ≤ 900 s`); tolerância de relógio ≤ 60 s. Cada requisição confere revogação da sessão (`sid`) e vínculo ativo com o bebê; papéis nunca vêm do token. Rotação: nova chave publicada aos verificadores antes de assinar; a anterior valida até `exp` máximo + tolerância; chave comprometida é retirada na hora. | SEC-005, SEC-011, RB-015, SR-013. |
| AD-32 | **Escopo e uso único do `X-Reauth-Token`** (v1.0.1, SR-013.1). `ReauthRequest.scope` (opcional na v1.x) lista `ReauthScope`; o token carrega `sub`, `sid`, `scope`, `jti` e `exp ≤ 300 s`, e é aceito **uma vez**. Escopo por operação (`x-reauth-scope`): `ACCOUNT_PASSWORD_CHANGE`, `ACCOUNT_EMAIL_CHANGE`, `IDENTITY_LINK`, `IDENTITY_UNLINK`, `DATA_EXPORT_REQUEST`, `DATA_EXPORT_DOWNLOAD`, `ACCOUNT_DELETE`, `PRIVACY_REQUEST`, `BABY_DELETE`, `OWNERSHIP_TRANSFER`. Erro: `401 REAUTH_REQUIRED` (ausente, expirado, usado, de outra sessão ou de outro escopo). Sem `scope` (transição): token de uso único válido para uma operação sensível qualquer; `scope` obrigatório só na `/v2`. O `jti` consumido é gravado no pedido de exclusão/privacidade como prova (SR-016). `ReauthRequest` exige exatamente um entre `password` e (`provider`,`id_token`,`nonce`). | SEC-014, privacy-security-spec 5.1. |
| AD-33 | **Sync: ordem do servidor, validação cruzada e cotas** (v1.0.1, SR-014). A resolução de conflito segue a ordem de chegada (`server_received_at`, `sync_sequence`); `client_created_at` é **somente informativo**, com tolerância de desvio `limits.sync_max_clock_skew_seconds` (padrão 86400) que só gera `CLIENT_CLOCK_SKEW`. `baby_id` e `entity_id` são verificados juntos (ver 3.1). Cotas: `limits.sync_max_payload_bytes`, `sync_max_events_per_baby_per_day`, `sync_max_entities_per_baby`. | SEC-042, SEC-006. |
| AD-34 | **Códigos e tokens de verificação** (v1.0.1, SR-013.5/6). Código de e-mail numérico de 6 a 12 dígitos (alvo 8), uso único, validade ≤ 15 min (alvo 10), ≤ 5 tentativas, limite de taxa por conta e IP; erros `401 INVALID_VERIFICATION_CODE`, `INVALID_RESET_TOKEN`. **Reset de senha invalida todas as sessões e todos os refresh tokens** (e tokens de reautenticação em circulação); não emite sessão. | SEC-014, SEC-017. |
| AD-35 | **Minimização e notificação** (v1.0.1, SR-015/016). `Baby.sex` é opcional e **depreciado**: o app não deve coletar nem enviar; `Session.approx_location` é reservado (sempre `null`). Antes e depois da exclusão em cascata de bebê compartilhado o servidor notifica o titular e **todos os cuidadores ativos** (categoria `SYSTEM`); resultado em `AccountDeletion.caregivers_notification`. `/health` e `/ready` não fazem parte do contrato público nem listam módulos (SR-020). | privacy-security-spec 1.3 e 5.1, SR-015, SR-016, SR-020. |

## 2. Papéis (Owner, Caregiver, ReadOnly)

Papel no contrato (enum em maiúsculas, ADR-0010): `OWNER`, `CAREGIVER`, `READ_ONLY`. Convite aceita só `CAREGIVER` e `READ_ONLY`.

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
| Excluir a própria conta (`/me/deletion-request`; ADR-0010) | sim (Owner ativo de um bebê) | não (403 `FORBIDDEN_ROLE`) | não (403 `FORBIDDEN_ROLE`) |
| Requisição de privacidade sobre os próprios dados pessoais (`/me/privacy-requests`; acesso, correção, exportação) | sim | sim | sim |
| Eliminação/anonimização dos próprios dados pessoais (`ANONYMIZATION`; não apaga dados do bebê) | não (usa a exclusão de conta; 409 `ACCOUNT_DELETION_REQUIRED`) | sim | sim |
| Atribuir assento premium (proposta); o adicional não precisa ser cuidador | sim | não | não |

Regras transversais: o Owner nunca fica sem Owner (INV-09; `409`). Revogar vínculo invalida tokens escopados, cache e cursor do bebê (INV-14).

## 3. Sync

### 3.1 Push (`POST /sync/push`)
- Corpo: `device_id` + `mutations[]` (≤ 100), processadas em ordem. Cada mutação: `mutation_id` (UUID), `op` (`CREATE|UPDATE|DELETE`), `entity_type` (`SLEEP_SESSION`, `FEEDING_SESSION`, `PUMPING_SESSION`, `DIAPER_EVENT`, `WAKE_EVENT`), `entity_id` (UUID do cliente), `baby_id`, `base_version` (0 na criação), `client_created_at`, `data` (completo na criação; parcial na edição; ausente na exclusão).
- Resposta `200` com `results[]` na mesma ordem. Status por mutação: `APPLIED`, `DUPLICATE` (reenvio; devolve a versão canônica, sem efeito) e `REJECTED` (com `problem`, `retryable`). Falhas de envelope ou cota (`400/401/413/426/429`) rejeitam o lote inteiro.
- Conflito (ADR-0003, RF-047): edições concorrentes resolvidas por last-write-wins **por campo**; `resolution` (`MERGED`, `LWW_CLIENT_WON`, `LWW_SERVER_WON`, `DELETE_WINS`, `KEPT_BOTH`), `conflicts[]` e `entity` canônica voltam ao cliente; o servidor audita (INV-21). **Ordem definida pelo servidor (v1.0.1, SR-014):** "último" é a mutação com maior `server_received_at`/`sync_sequence` (ordem de chegada ao commit), nunca o relógio do cliente. `client_created_at` é só informativo (auditoria, exibição, diagnóstico de relógio). `LWW_CLIENT_WON` = a mutação recebida agora prevaleceu; `LWW_SERVER_WON` = o valor já aplicado permaneceu. Cada `MutationResult` devolve `server_received_at`. Consequência de produto (decisão em 8.4): uma edição feita offline antes, mas sincronizada depois, vence uma edição online mais nova de outro cuidador.
- Exclusão vira tombstone (excluir uma `SLEEP_SESSION` gera também tombstones dos seus `WAKE_EVENT`); update atrasado sobre tombstone **não ressuscita**: `REJECTED`, `ENTITY_DELETED`, `retryable=false` (INV-20).
- Sono: pela política padrão `ACCEPT_AND_WARN`, sobreposição ou segundo timer aberto não é rejeitado; vem como `warnings` (`SLEEP_OVERLAP`, `OPEN_SLEEP_EXISTS`) e `resolution=KEPT_BOTH` (UX 4.13). Com a flag `sleep_overlap_policy=REJECT`, a mutação sai `REJECTED` com `problem.code=SLEEP_OVERLAP` e `retryable=false` (AD-23). Relógio errado: mutação aceita, `client_created_at` preservado, `CLIENT_CLOCK_SKEW` em `warnings` com `skew_seconds` e `tolerance_seconds` quando o desvio excede `limits.sync_max_clock_skew_seconds` (D-35); o desvio nunca altera a ordem de resolução.
- Autorização por mutação: `baby_id` inacessível gera `REJECTED` (`FORBIDDEN_ROLE` ou `ACCESS_REVOKED`, `retryable=false`); em `ACCESS_REVOKED` o cliente apaga o cache local do bebê.
- **Validação cruzada (v1.0.1, SR-014):** o servidor confere o vínculo com `baby_id` **e** que `entity_id` pertence a esse `baby_id` (em `WAKE_EVENT`, também `sleep_session_id`). `UPDATE`/`DELETE` com `entity_id` inexistente ou de outro bebê: `REJECTED`, `ENTITY_NOT_FOUND`, `retryable=false`, sem distinguir os casos. `CREATE` com `entity_id` já usado em outro bebê/usuário: `REJECTED`, `ENTITY_ID_UNAVAILABLE`, `retryable=false` (gerar novo UUID). `mutation_id` é escopado por usuário e dispositivo (colisão com UUID alheio não queima identificador). Cursor assinado e vinculado a um `baby_id`; outro bebê gera `410 SYNC_CURSOR_EXPIRED`, `reason=INVALID`.
- **Cotas (v1.0.1):** cota diária por bebê excedida: lote inteiro `429 QUOTA_EXCEEDED` com `Retry-After`; teto de entidades vivas excedido: `CREATE` `REJECTED`, `ENTITY_QUOTA_EXCEEDED`, `retryable=false`. Valores em `/reference-data` -> `limits` (campos opcionais).
- Códigos de rejeição por item: `VALIDATION_FAILED` (com `errors[]`), `SLEEP_OVERLAP` (só com a política `REJECT`), `ENTITY_DELETED`, `ENTITY_NOT_FOUND`, `ENTITY_ID_UNAVAILABLE`, `ENTITY_QUOTA_EXCEEDED`, `VERSION_AHEAD` (base_version maior que a do servidor), `FORBIDDEN_ROLE`, `ACCESS_REVOKED`, `BABY_NOT_FOUND`, `TRANSIENT` (`retryable=true`).

### 3.2 Pull (`GET /sync/pull?baby_id=&cursor=&limit=`)
- **Por bebê** (PA-02). Sem `cursor` = **snapshot** paginado das entidades vivas; o servidor fixa o ponto de consistência na primeira página, e o último `next_cursor` do snapshot continua em **delta**. Com `cursor` = apenas mudanças posteriores, incluindo **tombstones**.
- Entidades no feed: `BABY` (com `my_role` do usuário), `SLEEP_SESSION`, `FEEDING_SESSION`, `PUMPING_SESSION`, `DIAPER_EVENT`, `WAKE_EVENT`, `SLEEP_PREFERENCES`, `SLEEP_PREDICTION`. `op=UPSERT` traz `entity`; `op=TOMBSTONE` traz só `entity_id`, `version` e `deleted_at` (sem conteúdo; tombstone não é canal de vazamento).
- **Cursor opaco**: string de até 512 caracteres; internamente referencia o `sync_sequence` (ADR-0003). O cliente só guarda e devolve; nunca interpreta, compara ou ordena. O formato pode mudar sem aviso.
- Aplicação no cliente: idempotente e por `version` por entidade (descartar upsert com versão ≤ à local); repetir uma página é seguro.
- **Cursor expirado** (> 90 dias, retenção de tombstones) **ou inválido** (adulterado, de outro bebê): `410 Gone`, `code=SYNC_CURSOR_EXPIRED`, `reason=EXPIRED|INVALID`, `resync_required=true`. Procedimento do cliente: (1) manter a fila de mutações pendentes; (2) descartar o estado sincronizado do bebê; (3) `GET /sync/pull` sem cursor até `has_more=false`; (4) reenviar as pendentes por `/sync/push` (idempotente; as que apontam para entidades inexistentes saem `REJECTED`).
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
| 401 | `INVALID_CREDENTIALS`, `TOKEN_EXPIRED`, `SESSION_REVOKED`, `REFRESH_TOKEN_REUSED`, `REAUTH_REQUIRED`, `INVALID_VERIFICATION_CODE`, `INVALID_RESET_TOKEN`, `INVALID_ID_TOKEN`, `INVALID_REFRESH_TOKEN` | Autenticação. Credencial, código e token inválidos são sempre genéricos. `REAUTH_REQUIRED` cobre token ausente, expirado, já usado, de outra sessão ou de outro escopo. |
| 403 | `FORBIDDEN_ROLE`, `ACCESS_REVOKED`, `CONSENT_REQUIRED` | Autorização e consentimento. |
| 404 | `NOT_FOUND` | Inexistente ou sem vínculo (uniforme). |
| 409 | `IDENTITY_LINK_REQUIRED`, `ALREADY_MEMBER`, `LAST_LOGIN_METHOD`, `OWNER_DECISION_REQUIRED` (com `deletion_policy`), `ACCOUNT_DELETION_REQUIRED`, `PRIVACY_REQUEST_OPEN`, `PRIVACY_REQUEST_NOT_CANCELLABLE`, `OWNER_REQUIRED`, `PURCHASE_CONFLICT`, `EXPORT_NOT_READY`, `DELETION_NOT_CANCELLABLE`, `ENTITY_DELETED`, `SLEEP_OVERLAP` (só por item de sync, política `REJECT`) | Estado incompatível. |
| 410 | `SYNC_CURSOR_EXPIRED` | Resync completo obrigatório. |
| 412 | `VERSION_CONFLICT` | `If-Match` divergente. |
| 413 | `PAYLOAD_TOO_LARGE` | Lote de sync acima do limite. |
| 422 | `IDEMPOTENCY_KEY_REUSE` | Mesma chave, corpo diferente. |
| 426 | `CLIENT_UPGRADE_REQUIRED` | App abaixo do mínimo. |
| 429 | `RATE_LIMITED`, `QUOTA_EXCEEDED` | Limite de taxa ou cota de volume, sempre com `Retry-After` (segundos). Declarado em todas as operações. |
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
| POST | `/sync/push` | pushMutations | 046, 047, 008, 009, 010, 015, 016, 017, 018, 020 | O, C (inclui `WAKE_EVENT`) |
| GET | `/sync/pull` | pullChanges | 007, 046, 047, 019 | O, C, R (inclui `WAKE_EVENT` e tombstones) |
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
| GET/POST | `/me/data-exports` | listDataExports / requestDataExport | 044, 055 | Auth (`scope=PERSONAL_ONLY` para quem não é Owner) |
| GET | `/me/data-exports/{export_id}` | getDataExport | 044 | Auth |
| POST | `/me/data-exports/{export_id}/download-links` | createExportDownloadLink | 044, 055 | Auth + reauth |
| GET/POST/DELETE | `/me/deletion-request` | get/request/cancelAccountDeletion | 045, 055 | POST: O ativo + reauth (403 `FORBIDDEN_ROLE` para os demais); GET/DELETE: Auth |
| GET/POST | `/me/privacy-requests` | listPrivacyRequests / createPrivacyRequest | 044, 045, 055 | Auth + reauth no POST (`ANONYMIZATION` só para não-Owner) |
| GET/DELETE | `/me/privacy-requests/{request_id}` | getPrivacyRequest / cancelPrivacyRequest | 044, 045 | Auth |
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
| 011..014 | `/predictions/sleep`, `/sleep-preferences` | Recálculo é efeito colateral das mutações; chega ao cliente como `SLEEP_PREDICTION` no feed. |
| 019, 020 | `/timeline`, `/events/*`, `/events/{id}/history` | Edição e exclusão via push. |
| 028, 029 | `/aggregates` | Comparação só com o próprio histórico. |
| 037..040 | `/notification-preferences`, `/me/push-tokens`, `/me/consents` | Agendamento e entrega são internos (fora do contrato). |
| 041..043 | `/me/entitlements`, `/subscriptions/*` | Webhooks de loja fora do contrato. Assento adicional sem exigir vínculo de cuidador (AD-26). |
| 044, 045 | `/me/data-exports*`, `/me/deletion-request`, `DELETE /babies/{id}` | Assíncronos; reauth; exclusão de conta só Owner ativo, `CASCADE` por padrão, janela de arrependimento de 7 dias (AD-24); demais titulares usam `/me/privacy-requests` (AD-28). |
| 046, 047 | `/sync/*` | Seção 3. |
| 048 | fora da API | Analytics vai ao fornecedor; o contrato só expõe o consentimento `analytics_product`. |
| 050 | `Accept-Language`, `PATCH /me` (`locale`), chaves de mensagem | Localização no cliente. |
| 054, 055 | `/me/sessions`, `/me/session-revocations`, `/me/audit-events`, `/babies/{id}/audit-events` | Auditoria só leitura. |

## 6. Exemplos

Há exemplos no próprio OpenAPI para: cadastro, requisição de privacidade (anonimização), exclusão de conta agendada, `/reference-data` com `policies`, login, criação de bebê, push de sono (timer iniciado e parado offline) com fralda e exclusão, resposta mista de push (`APPLIED`, `DUPLICATE`, `REJECTED`), delta de pull com upsert e tombstone, `410` de cursor expirado, previsão, agregação semanal, preferências de notificação, entitlement free, revogação de consentimento e erros de cada classe.

## 7. Pontos em aberto

Dúvidas D-nn são as de `specs/product-spec.md` seção 9. PA-nn são novas, geradas por esta spec. Itens marcados **Resolvido** foram decididos nos ADR-0009/0010 e já estão no contrato.

| ID | Ponto | Impacto | Sugestão |
|---|---|---|---|
| PA-01 | **Resolvido (ADR-0009).** Verificação de e-mail no cadastro: `register` -> `202` uniforme -> `email/verify` -> sessão; conta ativa só após confirmação. | Onboarding (UX E2), RF-001. | Nada a fazer. |
| PA-02 | Cursor por bebê (contrato atual) ou por usuário (um pull para todos os bebês). Por bebê simplifica revogação (INV-14) e o snapshot, ao custo de N chamadas. | Cliente e ARCH-003. | Por bebê. |
| PA-03 | INV-19 diz "version por bebê é monotônica"; o contrato usa `version` por entidade e a ordem global vem do cursor. Confirmar que é suficiente para `base_version` e conflito. | Modelo de sync. | Manter por entidade. |
| PA-04 | **Resolvido (ADR-0009).** Sono sobreposto (D-05): aceito e sinalizado por padrão (`ACCEPT_AND_WARN`), configurável por flag para `REJECT` (`REJECTED`, `SLEEP_OVERLAP`). Exposto em `/reference-data` -> `policies.sleep_overlap_policy`. | RF-008-A6, RF-009-A3, RF-009-A8. | Nada a fazer; UX deve tratar as duas políticas. |
| PA-05 | **Resolvido (ADR-0010, decisão 5).** `end_at` é obrigatório em `BREASTFEEDING`; o timer vive só no cliente até parar. Consequência conhecida e aceita: outro cuidador não vê a mamada em curso. Reabrir só se o timer compartilhado virar requisito (tornar `end_at` opcional na criação seria mudança aditiva). | RF-015, RF-007. | Nada a fazer. |
| PA-06 | **Resolvido em parte (ADR-0009).** Exclusão de conta de Owner com outros cuidadores: política `CASCADE` por padrão (apaga também para os demais, com `acknowledge_other_caregivers=true` e reauth); `BLOCK` e `TRANSFER_OWNERSHIP` por flag. Pendente: validação jurídica (DJ-09) e a opção "excluir só o vínculo do Owner" segue sem suporte. | RF-045, DJ-09. | Aguardar DJ-09. |
| PA-07 | **Resolvido (ADR-0009 e ADR-0010).** Janela de arrependimento mantida: `scheduled_for`, `DELETE /me/deletion-request`; duração de **7 dias** (`policies.deletion_grace_days`, padrão 7, customizável em banco). | RF-045. | Nada a fazer. |
| PA-08 | Assento premium (ADR-0005, D-04): `PUT/DELETE /subscriptions/family-seat` é **proposta** (`x-status: proposed`). Resolvido por ADR-0009: o adicional não precisa ser cuidador ativo (aceita `email` ou `membership_id`). Em aberto: o que ocorre se o titular cancelar, quem edita flags em produção (fora desta API), vínculo do assento por e-mail antes do cadastro do convidado e privacidade do e-mail. | RF-043. | Decidir cancelamento e fluxo do convite de assento. |
| PA-09 | `404` (sem vínculo) × `403 ACCESS_REVOKED` (revogado) distingue quem já teve acesso. Aceitável porque o ex-membro já conhecia o recurso; confirmar com segurança (SECURITY-REVIEW-001). | RF-004-A6, RF-006-A5. | Manter. |
| PA-10 | Obrigatoriedade de novo aceite (RF-003-A2): o contrato devolve `pending_consents`/`pending_required` e só bloqueia criação de bebê e aceite de convite (`403 CONSENT_REQUIRED`). Bloqueio global é decisão do cliente. | RF-003. | Confirmar o escopo do bloqueio do servidor. |
| PA-11 | Visibilidade entre cuidadores: nome (`display_name`) de quem registra aparece para todos; e-mail só para o Owner. Confirmar com privacidade (D-25, D-33). | RF-019-A5, RF-020-A7. | Manter. |
| PA-12 | `If-Match` opcional em `PATCH /babies/{id}` e `PUT /sleep-preferences`. Torná-lo obrigatório (`428`) é mais seguro e custa um GET prévio. | RF-004, RF-014. | Opcional no MVP. |
| PA-13 | Código HTTP de validação semântica: `400` único (atual) × `422`. | Todos os clientes. | Decidir antes de gerar SDK. |
| PA-14 | Foto do bebê (D-09): só `photo_ref` somente leitura; sem endpoint de upload no MVP. | RF-004. | Decidir D-09. |
| PA-15 | Notificações de rotina (D-28): categoria `routine` só tem `enabled`, antecedência e quiet hours; falta a estrutura do lembrete (horários, recorrência). `DEVELOPMENT_PHASE` fica `available=false` (D-10). | RF-037-A8. | Decidir D-28. |
| PA-16 | Quiet hours são por categoria (como no domínio) mas a UX as mostra como um único intervalo. O cliente pode replicar o valor em todas. Modelar quiet hours globais por usuário é alternativa. | RF-038. | Confirmar com UX. |
| PA-17 | Despertares noturnos (D-12): **resolvido no contrato** por `WakeEvent` + `night_awakenings` derivado (AD-22). Critério de suficiência definido (ADR-0010): `limits.night_awakenings_min_session_minutes`, padrão 240, customizável. Em aberto: regra de inferência (`INFERRED`) no servidor e atribuição de sessões à meia-noite (D-13, regra de bucket na implementação). | RF-010, RF-028. | Decidir D-13 e a regra de `INFERRED`. |
| PA-18 | Mínimo de dados para tendência (D-27): o servidor informa `data_sufficiency.min_days_required`; a UX cita 3 dias. | RF-029. | Decidir D-27. |
| PA-19 | Domínio, regiões e ambientes (`api.nina.app` é placeholder); requisito de região Brasil (DJ-04). | `servers`. | Definir com infra. |
| PA-20 | Idempotência de `Idempotency-Key` guarda a resposta por 24 h; confirmar retenção e tamanho do armazenamento com backend. | AD-10. | Spike ARCH-003. |
| PA-21 | Perfis `SleepSession` etc. guardam `notes` livre: limite de 500 caracteres é suposição (SEC-043). | Validação. | Confirmar. |
| PA-22 | Cabeçalho de versão mínima do app (`426`) e canal de comunicação (loja) não estão definidos. | AD-01. | Definir política de suporte (D-41). |
| PA-23 | Eventos de analytics (RF-048) não são parte desta API; confirmar que o app envia direto ao fornecedor e que o SDK respeita o consentimento `analytics_product`. | RF-048. | Confirmar com PRIV-001. |
| PA-24 | **Resolvido (ADR-0010, decisão 1).** `snake_case` confirmado em todo o contrato (`age_calculation`, `chronological_days`, `corrected_days`, `correction_applied`, `night_awakenings`). Não há mais pendência. | Clientes e SDKs. | Nada a fazer. |
| PA-25 | **Resolvido (ADR-0010, decisão 2).** Todos os enums em MAIÚSCULAS (inclui `sleep_type`, `sex`, `source`, papéis, `status`, `platform`, `op`, `entity_type`, `period`...). Quebra em relação ao rascunho `1.0.0-draft.1`, que não tinha clientes. Clientes toleram valores desconhecidos (AD-27). | Padronização de enums. | Nada a fazer. |
| PA-26 | `FeedingType` inclui `SOLID` e `OTHER`; para eles o contrato aceita `end_at` nulo e não há `side`/`volume_ml`/`milk_type`. Regras de validação e agregados (`/aggregates.feeding` só conta mama e mamadeira) para sólidos ficam por definir. | RF-015, RF-028. | Definir com produto. |
| PA-27 | **Resolvido no contrato (ADR-0010, decisão 3); validação jurídica pendente (DJ-09).** Só o Owner ativo de um bebê exclui a conta (403 para os demais, sem exceção para quem não tem vínculo). Quem não é Owner exerce acesso, correção, exportação e eliminação/anonimização dos próprios dados por `/me/privacy-requests`, sem apagar dados do bebê (AD-28). | RF-045, DJ-09. | Validar com jurídico antes de produção. |
| PA-28 | **Resolvido (ADR-0010, decisão 4).** Janela da idade corrigida: `policies.corrected_age_max_months`, padrão inicial 24, customizável em banco. | RF-005. | Nada a fazer. |
| PA-29 | `WakeEvent` fora de `/timeline` e de `GET /events/{id}`: só trafega em `/sync/pull` e é derivado em `night_awakenings`. Confirmar se a UI precisa listar despertares online sem sync, e quem gera `INFERRED`. | RF-010, RF-016. | Confirmar com UX. |
| PA-30 | Requisições de privacidade (AD-28): prazos (`privacy_request_sla_days` = 15, `privacy_request_ack_hours` = 48), verificação de identidade, motivos de recusa (`rejection_code`, p.ex. retenção legal) e quais consentimentos/registros são retidos por obrigação legal na anonimização dependem do jurídico (DJ-07, DJ-09). | RF-044, RF-045. | Validar com jurídico; os valores são parâmetros de banco. |
| PA-31 | Comportamento da `ANONYMIZATION` de não-Owner (proposta de engenharia): sai de todos os bebês, autoria dos eventos que registrou passa a rótulo anônimo (eventos ficam para os demais), mesma janela de arrependimento da exclusão (`deletion_grace_days`) e cancelável. Confirmar com produto/jurídico se a janela deve valer aqui e como a UI exibe o autor anonimizado. | RF-045, RF-019-A5. | Confirmar com produto e jurídico. |
| PA-32 | **Aberto (1.0.1).** Ordem de LWW pelo servidor (AD-33) altera o desempate documentado no ADR-0003; ver V2-06. | Modelo de sync, ADR-0003. | Decidir entre ordem de chegada pura e `min(client, server)`. |

## 8. Congelamento v1

O contrato `contracts/openapi.yaml` foi congelado como **`1.0.0`** (ADR-0010) e está hoje em **`1.0.1`** (patch de segurança, sem quebra; ver changelog). A partir daqui, `/v1` é estável para os apps do MVP.

### 8.1 Regras de mudança

| Tipo | Regra |
|---|---|
| **Aditivas (permitidas em `/v1`, versão minor/patch)** | Novo endpoint, método ou caminho; novo campo **opcional** em resposta; novo campo opcional em requisição (com padrão que preserva o comportamento); novo parâmetro de consulta ou cabeçalho **opcional**; novos valores de enum (clientes toleram desconhecidos, AD-27); novos códigos de erro (`code`) e novas extensões de `Problem`; novas políticas e limites em `/reference-data` (valores de `policies`/`limits` são dados, mudam sem alterar o contrato); esclarecimentos de descrição e exemplos. |
| **Breaking (exigem `/v2`)** | Remover ou renomear endpoint, campo, parâmetro ou valor de enum; tornar obrigatório um campo, parâmetro ou cabeçalho antes opcional; mudar tipo, formato, unidade ou semântica de um campo (inclusive de `null`/`0`); estreitar valores aceitos em requisições; mudar códigos HTTP de sucesso ou o significado de um `code` existente; mudar a convenção de enums em maiúsculas ou de `snake_case`; mudar as regras de autorização para mais restritivas. |
| **Processo** | Toda mudança passa por `npx @redocly/cli@1.34.5 lint` e `python3 -m openapi_spec_validator`, atualiza o changelog abaixo e, se aditiva, incrementa minor (1.1.0); se só documentação, patch (1.0.1). Quebra: novo prefixo `/v2` convivendo com `/v1`, com `Deprecation` e `Sunset` em `/v1` (AD-01) e suporte mínimo definido em PA-22. |
| **Entrada x saída** | Clientes ignoram campos e valores de enum desconhecidos **em respostas**. Em requisições, objetos com `additionalProperties: false` rejeitam campo desconhecido com `400 VALIDATION_FAILED`; por isso um campo novo de requisição só é aditivo quando opcional. |

### 8.2 Changelog

| Versão | Data | Mudanças |
|---|---|---|
| 1.0.1 | 2026-10-08 | **Patch de segurança (SR-013, SR-014 contrato, SR-015, SR-016, SR-020, SR-021), sem quebra.** `429` + `Retry-After` em todas as 70 operações (eram 22), novo `QUOTA_EXCEEDED`; `maxLength`/`pattern`/`minLength`/`maxItems` em senha (128), códigos (`^[0-9]{6,12}$`), tokens, `id_token`, `nonce`, e-mail, `timezone`, `locale`, versões de documento e tokens de compra; `additionalProperties: false` nos pedidos de credencial; `ReauthRequest` exige exatamente um método (`oneOf`) e ganha `scope` opcional (`ReauthScope`); `ReauthResponse` ganha `scope` e `expires_in ≤ 300`; `x-reauth-scope` por operação sensível e `X-Reauth-Token` opcional em `POST /me/data-exports`; perfil do JWT, reset de senha invalida todas as sessões e refresh tokens, política de códigos; LWW por ordem do servidor (`client_created_at` informativo, `MutationResult.server_received_at`, `PushWarning.tolerance_seconds`), validação cruzada de `baby_id`/`entity_id` (`ENTITY_NOT_FOUND`, `ENTITY_ID_UNAVAILABLE`), cotas (`ENTITY_QUOTA_EXCEEDED`, novos `limits.*` opcionais); `AccountDeletion.caregivers_notification` e notificação aos cuidadores; `Baby.sex` e `Session.approx_location` marcados `deprecated`; `UNSPECIFIED` aceito em `sex`; `deletion_grace_days` e `grace_days` com faixa 1..30; nota de `PurposeKey` MAIÚSCULAS ↔ minúsculas do banco; `/health` e `/ready` fora do contrato. Itens que não cabem em `/v1` estão em 8.4. |
| 1.0.0 | 2026-10-08 | **Congelamento v1 (ADR-0010).** `snake_case` confirmado; todos os enums em MAIÚSCULAS com exemplos atualizados e nota de tolerância a valores desconhecidos; exclusão de conta restrita a Owner ativo de um bebê (`403 FORBIDDEN_ROLE` com `privacy_request_path`); novos `/me/privacy-requests` (4 operações: acesso, correção, exportação, eliminação/anonimização dos próprios dados, com reautenticação, status e prazo) e campo `scope` em `/me/data-exports`; janela de arrependimento de 7 dias em `policies.deletion_grace_days`, `AccountDeletion.scheduled_for` e `grace_days`; PA-05 resolvido (`end_at` obrigatório); padrões 24 meses (`corrected_age_max_months`) e 240 minutos (`limits.night_awakenings_min_session_minutes`, que substitui `night_awakenings_min_coverage_percent`) documentados como customizáveis; novos `policies.privacy_request_sla_days` e `privacy_request_ack_hours`; exemplo de `/reference-data`. |
| 1.0.0-draft.1 | 2026-10-08 | Rascunho API-001 com revisão ADR-0009 (idade, enums extensíveis, `WakeEvent`, flags de sono sobreposto e exclusão, assento premium). Sem clientes. |

### 8.3 Pontos ainda abertos (não bloqueiam o v1)

Nenhum bloqueia o congelamento; a maioria é decisão de produto/jurídico que se resolve por **dado** (flag/parâmetro) ou por mudança aditiva. Detalhes na seção 7.

| ID | Resumo | Natureza |
|---|---|---|
| PA-02, PA-03 | Cursor por bebê (atual) e `version` por entidade (atual); confirmar no spike ARCH-003. | Implementação; contrato não muda. |
| PA-06, PA-27, PA-30, PA-31 | Validação jurídica (DJ-07, DJ-09) da exclusão em cascata, das requisições de privacidade, dos prazos e do comportamento da anonimização. | Jurídico; valores são parâmetros. |
| PA-08 | Assento premium (`x-status: proposed`): cancelamento do titular, fluxo do convite de assento, privacidade do e-mail. | Produto; endpoint marcado como proposta. |
| PA-09, PA-11 | `404` x `403 ACCESS_REVOKED`; visibilidade de nome e e-mail entre cuidadores. | Segurança/privacidade. |
| PA-10, PA-12, PA-13 | Escopo do `403 CONSENT_REQUIRED`; `If-Match` obrigatório (`428`); `400` x `422` na validação. **PA-13 deve ser decidido antes de gerar SDK**, pois trocar o código é quebra. | Contrato/cliente. |
| PA-14, PA-15, PA-16, PA-18 | Foto do bebê (D-09), estrutura de lembretes de rotina (D-28), quiet hours globais, mínimo de dias de tendência (D-27). | Produto/UX; aditivos. |
| PA-17, PA-29 | Regra de `INFERRED`, atribuição de sessões à meia-noite (D-13) e listagem de despertares online. | Produto/implementação. |
| PA-19, PA-20, PA-22 | Domínio e regiões (DJ-04), retenção de idempotência, política de versão mínima do app. | Infra. |
| PA-21, PA-23, PA-26 | Limite de `notes`, analytics fora da API, regras de `SOLID`/`OTHER`. | Produto/validação. |

Resolvidos neste fechamento: PA-01, PA-04, PA-05, PA-07, PA-24, PA-25, PA-28 e, no contrato, PA-27.

### 8.4 Requer decisão do usuário ou `/v2` (não aplicado na 1.0.1)

Itens do `security-review-001` que, no contrato, seriam quebra de `/v1` (ou dependem de decisão de produto/jurídico). Estão **fora** da 1.0.1.

| ID | Item | Por que não cabe em `/v1` | Opção |
|---|---|---|---|
| V2-01 | Tornar `X-Reauth-Token` **obrigatório** em `POST /me/data-exports` (privacy-security-spec 5.1; SR-013.1). | Tornar obrigatório um cabeçalho antes ausente é quebra (8.1). | 1.0.1 aceita o cabeçalho opcional e o valida/audita quando enviado. Decidir: forçar já (apps ainda não publicados) ou na `/v2`. |
| V2-02 | Tornar `ReauthRequest.scope` **obrigatório**. | Campo opcional virar obrigatório é quebra. | Na 1.x, sem `scope` o token é de uso único para uma operação sensível qualquer; obrigatório na `/v2`. |
| V2-03 | **Remover** `Baby.sex` (`BabyCreate`, `BabyUpdate`, `Baby`) conforme privacy-security-spec 1.3 e SR-015. | Remover campo é quebra. | 1.0.1: `deprecated`, app não coleta; remoção na `/v2`. Se não houver clientes publicados, o usuário pode autorizar a remoção como exceção (DJ-01). Alinhar enum com o banco (`OTHER` x `UNSPECIFIED`). |
| V2-04 | **Remover** `Session.approx_location` (SR-015). | Remover campo é quebra. | 1.0.1: `deprecated`, sempre `null`. Remoção na `/v2`. |
| V2-05 | **Janela de arrependimento** (e notificação *prévia* com direito de exportar o próprio recorte) em `DELETE /babies/{baby_id}` quando há outros cuidadores (SR-016). | Mudar o `204` imediato para exclusão agendada (`202`) altera o código de sucesso e a semântica. | 1.0.1: o servidor notifica todos os cuidadores ativos ao receber o pedido e ao concluir, e exige reauth `BABY_DELETE`. Decidir: manter imediato, ou `/v2` com `202` + `scheduled_for`. Um escopo de exportação do recorte do cuidador (`CAREGIVER_AUTHORED`) seria aditivo, mas depende de decisão jurídica. |
| V2-06 | **LWW por ordem do servidor** como regra única (SR-014) e consequência de produto. | A 1.0.1 documenta a ordem do servidor (`client_created_at` informativo), o que muda o desempate em relação ao rascunho ADR-0003 (relógio do cliente). | Decidir se aceita que edição offline antiga, sincronizada depois, vença uma edição online mais nova. Alternativa que preserva o offline: ordem por `min(client_created_at, server_received_at)` com desempate por `sync_sequence`; exige emenda no ADR-0003. |
| V2-07 | Rejeição dura por desvio de relógio (`REJECTED CLOCK_SKEW`, ±24 h). | Novo caso de rejeição de mutação hoje aceita. | Mantido só aviso `CLIENT_CLOCK_SKEW`; o desvio não afeta a ordem. |
| V2-08 | Exigir `ANONYMIZATION`/exclusão com prova de reauth verificável no banco (`reauth_jti`). | Depende de DDL e de implementação (database-engineer, dotnet-backend-engineer). | Contrato já documenta o registro do `jti`. |
| V2-09 | `PurposeKey` em minúsculas (alinhar ao banco). | Mudar o valor de enum é quebra e os apps já usam MAIÚSCULAS. | Mantido MAIÚSCULAS; o servidor converte `UPPER <-> lower` (SR-021). |
| V2-10 | `/v2`: unificar `volume_ml` decimal, `notes` 2000 e `platform` com `WEB`. | Mudar tipo/limite é quebra. | Contrato permanece inteiro, 500 e `IOS|ANDROID` (mais estreito que o banco, sempre válido). |
| V2-11 | `Membership.status=EXPIRED` e `PrivacyRequest.status` `IN_PROGRESS`/`SCHEDULED` inexistentes no banco (SR-021, SR-007). | Remover valores de enum é quebra. | Camada de API deriva os estados; database-engineer decide o mapeamento. |

### 8.5 Itens de implementação que decorrem da 1.0.1 (sem efeito no contrato)

Backend: emitir `QUOTA_EXCEEDED` e `Retry-After`; emitir tokens de reautenticação com `scope`, `sid` e `jti` de uso único (hoje há `purpose` fixo `reauth`); validar `scope` por operação; invalidar todas as sessões no reset; código de verificação com 8 dígitos (hoje 6) e 5 tentativas; validação cruzada de `entity_id`; cursor assinado; remover `/ready` da rota pública; mapear `PurposeKey` maiúsculas <-> minúsculas. Banco: PKs compostas `(baby_id, id)`, `reauth_jti`, enums de `sex`, estados de convite e privacidade. Testes de contrato<->DDL (SR-021, Anexo B 10) e IDOR/BOLA (Anexo B 9) permanecem exigidos.

## 9. Validação do contrato

- `npx @redocly/cli@1.34.5 lint contracts/openapi.yaml`: sem erros nem avisos (configuração recomendada); revalidado na 1.0.1.
- `python3 -m openapi_spec_validator contracts/openapi.yaml`: OK; revalidado na 1.0.1.
- Sugestão para a CI: as duas verificações acima, mais geração de clientes (Swift e Kotlin) como smoke test e testes de contrato (Schemathesis ou Dredd) contra o BFF.


## Confirmações do produto (ADR-0010, itens 7–9)
Prazos de privacidade (48 h / 15 dias), comportamento da anonimização (PA-30/PA-31) e **400 para validação (PA-13)** foram confirmados em 2026-10-08. Contrato v1.0.0 congelado, salvo validação jurídica dos prazos (DJ-07/DJ-09).
