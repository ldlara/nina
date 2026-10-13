# SECURITY-REVIEW-001 — Revisão de segurança do Nina (Onda 3)

Status: **concluída (somente leitura do código; nada foi corrigido)** · Data: 2026-10-08 · Revisor: security-reviewer
Commit revisado: `dabfcae` (HEAD). Itens não versionados no momento da revisão (`android/`, `ios/`, `backend/spikes/`, `backend/src/Nina.SharedKernel/`, `backend/src/Modules/Nina.Identity/{Contracts,Crypto,External,Mail,Services}` e alterações em `Directory.Packages.props`/`Nina.sln`) são trabalho em curso de outros agentes e **não foram auditados**; exigem revisão própria antes do merge (ver seção 9).

Fontes: `backend/db/migrations/0001_init.sql`, `contracts/openapi.yaml` (v1.0.0), `specs/api-spec.md`, `specs/privacy-security-spec.md` (SEC-001..067, DJ-01..13), `backend/` (Nina.Api, Nina.Bff, `Directory.*.props`), `.github/workflows/ci.yml`, `infra/openshift/*`, `infra/docker/*`, `docker-compose.yml`, `.env.example`, `scripts/setup-dev-env.sh`, ADR-0006 e ADR-0007.

---

## 1. Sumário executivo

### 1.1 Parecer

**BLOQUEIA RELEASE.** Há 2 achados CRÍTICOS e 4 ALTOS abertos. O Privacy Gate (`privacy-security-spec.md` §10) exige "`SECURITY-REVIEW-001` sem achados críticos/altos abertos"; esse item **não está atendido**.

| O que o parecer bloqueia | O que não bloqueia |
|---|---|
| Qualquer release, beta ou ambiente com dados reais de usuários ou de crianças. | Continuar a Onda 3 (BE-001/BE-002/BFF-001, ARCH-003) com dados sintéticos. |
| Integrar o código .NET ao banco com os papéis atuais antes de SR-001..SR-006 estarem fechados. | Evoluir contrato e clientes (nenhum achado do contrato é bloqueante isolado; SR-013 deve fechar antes de gerar SDK). |
| Fechar o Privacy Gate. | Trabalho de jurídico/DJ em paralelo. |

Condição para reabrir a decisão: SR-001..SR-006 corrigidos **e** as provas do anexo A convertidas em testes automatizados que rodem como `nina_app` (nunca como dono/superuser) e passem; SR-007..SR-019 com dono e data; Shadow IT (seção 7) com responsável nomeado.

### 1.2 Contagem

| Severidade | Qtde | IDs |
|---|---|---|
| CRÍTICO | 2 | SR-001, SR-002 |
| ALTO | 4 | SR-003, SR-004, SR-005, SR-006 |
| MÉDIO | 13 | SR-007 a SR-019 |
| BAIXO | 3 | SR-020, SR-021, SR-022 |

Cobertura dos 41 requisitos SEC definidos: 5 cobertos, 24 parciais, 7 lacunas, 5 não aplicáveis ao backend/MVP (seção 6).

### 1.3 Leitura rápida (o que importa)

1. **O isolamento por bebê é forte nas tabelas de eventos, mas contornável por cima.** `nina.family` não tem RLS, e a política de leitura de `baby` concede acesso a quem é "dono da família". Quem consegue executar SQL como `nina_app` (SQL injection, filtro esquecido, dependência comprometida) vira dono de qualquer família e lê o perfil de qualquer criança (SR-001). A política de UPDATE de `caregiver_membership` permite a um membro autopromover-se, reativar-se após remoção e até migrar o vínculo para outro bebê conhecendo só o UUID; **hoje isso só não é explorável por causa de um bug** (SR-004) cuja correção óbvia reabre o problema (SR-002).
2. **10 tabelas com dados sensíveis (hashes de senha, tokens, e-mails, família, assinaturas, outbox) não têm RLS** e o papel do app tem SELECT/INSERT/UPDATE em todas (SR-003).
3. **A trilha de auditoria e de consentimento "append-only" pode ser apagada integralmente** por quem tem o papel worker, sem limite de idade (SR-005), e **forjada** pelo papel do app (SR-006).
4. **Operações centrais do contrato não são implementáveis sob RLS** (aceitar convite, transferir propriedade): a pressão de prazo empurra a equipe para conectar a API com papel privilegiado ou para a correção ingênua de SR-004 (SR-004).
5. **Esqueleto:** `docker-compose.yml` conecta a API como o superusuário do Postgres, o que anula a RLS e mascara todos os achados acima nos testes (SR-018). Não há autenticação, TLS interno, rate limit, NetworkPolicy, SAST, secret scanning nem SBOM (SR-017..SR-019).
6. **Pontos positivos verificados** (seção 8): fail-closed sem contexto, ReadOnly não escreve, janela de 7 dias imposta pelo banco, `erase_*`/`purge_*` fora do alcance de `nina_app`, funções `SECURITY DEFINER` com `search_path` fixo, pods endurecidos, `NuGetAudit` com warnings como erro.

### 1.4 Modelo de ameaça usado para calibrar severidade

A RLS é declarada "defesa em profundidade" (SEC-003; cabeçalho da migração). Portanto os achados de banco assumem o atacante-alvo dessa camada: **código executando como `nina_app`** (SQL injection, bug de BOLA/IDOR, filtro ausente, dependência comprometida, insider com credencial da aplicação), com `nina.user_id` legítimo ou forjado. Um usuário final comum só os explora se existir uma falha na API; mas a função da RLS é justamente impedir que essa falha vire vazamento entre famílias. Achados que **não** dependem de falha da aplicação (um usuário legítimo com seu próprio token) estão marcados "explorável via API normal".

### 1.5 Ambiente e método das provas

- PostgreSQL 16.15, cluster temporário criado com `initdb` em diretório de trabalho, **removido ao final** (processo parado, diretório e scripts apagados). Nenhum arquivo do repositório foi alterado; nada foi commitado.
- `0001_init.sql` aplicada integralmente com `ON_ERROR_STOP` (sem erros). Dono dos objetos: superusuário (como no `docker-compose.yml`). Logins de teste: `app_login` (membro de `nina_app`), `worker_login` (`nina_worker`), `cfg_login` (`nina_config_admin`), todos sem superuser e sem BYPASSRLS.
- Contexto de usuário aplicado como a aplicação faria: `select set_config('nina.user_id', '<uuid>', true)` dentro de transação.
- Massa: 4 usuários (Alice, Bob, Carol, Dave), 2 bebês (cada um de uma família), Carol READ_ONLY no bebê da Alice, um evento de sono do Bob.
- As provas aparecem no formato "comando → resultado observado" em cada achado e estão resumidas no anexo A.
- Não foi possível validar: containers (sem daemon Docker), workflow do GitHub Actions, PostgreSQL 15, desempenho de RLS em volume, restore de backup (mesmas ressalvas de `docs/project/status.md`).

---

## 2. Achados — CRÍTICO

### SR-001 — Isolamento por bebê contornável via `nina.family` (sem RLS) + `baby_select` por "dono da família" + `family_id` mutável

- **Severidade:** CRÍTICO
- **Componente:** banco (RLS)
- **Requisitos:** SEC-001, SEC-003, SEC-006; STRIDE "I — IDOR/BOLA entre bebês"
- **Evidência:**
  - `0001_init.sql:215-219` (`nina.family`, sem `ENABLE ROW LEVEL SECURITY` em nenhum ponto do arquivo) e `:1639-1646` (`GRANT SELECT, INSERT, UPDATE ON ... nina.family ... TO nina_app`).
  - `:1601-1602` — `baby.app_select ... USING (nina.can_read_baby(id) OR nina.is_family_owner(family_id))`; `:318-320` (`is_family_owner` compara `family.owner_user_id` com o usuário do contexto).
  - `:1604-1605` — `baby.app_update ... WITH CHECK (nina.baby_role(id) = 'OWNER')`: não há `WITH CHECK` sobre o novo `family_id`.
- **Prova (executada):**
  - Dave, sem nenhum vínculo, em transação com `nina.user_id = Dave`: `SELECT count(*) FROM nina.baby` → 0 (RLS correta). Em seguida `UPDATE nina.family SET owner_user_id = <Dave> WHERE id = <família do Bob>` → `UPDATE 1`; `SELECT display_name, birth_date FROM nina.baby` → retorna o bebê do Bob (`BebeBob`, `2026-02-11`).
  - `SELECT id, owner_user_id FROM nina.family` lista todas as famílias (enumeração dos `family_id` de todos os tenants).
  - Variante B1/B2: o Owner do bebê A executa `UPDATE nina.baby SET family_id = <família do Bob>` → aceito; Bob, sem vínculo com A, passa a ler o perfil do bebê A.
- **Cenário de exploração:** um endpoint com SQL injection ou um `WHERE` esquecido executa as duas instruções acima. O atacante enumera todos os `family_id`, assume cada família e lê nome, data de nascimento, sexo e `photo_ref` de todas as crianças (C3). Pela segunda via, um Owner malicioso empurra o perfil de uma criança para a família de um terceiro, ou "enxerta" o bebê no entitlement premium de outra família (ADR-0005 agrupa entitlement por `family_id`).
- **Correção recomendada:**
  1. Habilitar RLS em `nina.family`: SELECT/UPDATE somente onde `owner_user_id = nina.current_user_id()`; INSERT somente com `owner_user_id = nina.current_user_id()`; **proibir UPDATE de `owner_user_id`** pelo app (trigger ou coluna fora do GRANT; mudança de titularidade só por função `SECURITY DEFINER` auditada).
  2. Remover `OR nina.is_family_owner(family_id)` do SELECT de `baby` (leitura somente por vínculo ativo) ou restringi-lo ao instante da criação via `can_bootstrap_owner`.
  3. Em `baby.app_update`, `WITH CHECK` que exija `family_id` imutável pelo app (trigger `BEFORE UPDATE` que rejeita mudança de `family_id` fora de função definer).
  4. Teste de regressão: para cada tabela do schema `nina`, afirmar que `relrowsecurity` é verdadeiro **ou** que existe justificativa documentada.
- **Agente responsável:** database-engineer

### SR-002 — Política `app_update` de `caregiver_membership` permite autopromoção, autorreativação e migração do vínculo entre bebês (latente: mascarada por SR-004)

- **Severidade:** CRÍTICO (latente; vira explorável com a correção óbvia de SR-004)
- **Componente:** banco (RLS)
- **Requisitos:** SEC-002, SEC-004, SEC-005, SEC-007; STRIDE "E — ReadOnly escreve; Caregiver remove Owner"
- **Evidência:** `0001_init.sql:1614-1616`:
  `USING (nina.baby_role(baby_id) = 'OWNER' OR user_id = nina.current_user_id())` e o mesmo predicado em `WITH CHECK`. Nenhuma coluna é protegida: `role`, `status`, `baby_id`, `revoked_*`, `accepted_at`, `invite_*` podem ser alteradas pelo próprio membro. Não existe trigger de transição de estado em `caregiver_membership` (só `touch_updated_at` e o check de Owner, `:286` e `:300-302`).
- **Prova (executada):**
  - Estado atual do repositório: Carol (READ_ONLY) executa `UPDATE ... SET role='CAREGIVER' WHERE user_id=<Carol>` → `UPDATE 1` e consegue inserir um `sleep_session` no bebê da Alice na mesma transação; o `COMMIT` falha com `NN010 bebe ... sem Owner ativo` **somente** porque o constraint trigger `check_baby_has_owner` roda sem privilégio e não enxerga a linha do Owner (SR-004).
  - Com `check_baby_has_owner` marcada `SECURITY DEFINER SET search_path = nina, pg_temp` (a correção óbvia para SR-004, aplicada em cópia do banco de teste): (E1) a autopromoção READ_ONLY → CAREGIVER **comita** e permite escrever; (E2) Carol sai (`REVOKED`) e depois executa `UPDATE ... SET status='ACTIVE', revoked_at=NULL` → reativação **aceita**, anulando a revogação do Owner (SEC-005); (E3) `UPDATE nina.caregiver_membership SET baby_id = <bebê do Bob>, role = 'CAREGIVER' WHERE user_id = <Carol>` → aceito; Carol lê `display_name`, `birth_date` e as `notes` do Bob. Basta o UUID do bebê-alvo.
- **Cenário de exploração:** (a) explorável via API normal se `PATCH /caregivers/{id}` ou "sair do bebê" for implementado como UPDATE genérico com mass assignment; (b) via qualquer falha de SQL no app. UUIDs de bebê vazam por `outbox_message.aggregate_id` e `change_log` (SR-003) e por `audit_event` do Owner. Como `baby.id` é gerado no cliente (`:222`), IDs previsíveis ou reaproveitados agravam.
- **Correção recomendada:**
  1. Remover o ramo `OR user_id = current_user_id()` do UPDATE. Toda mudança de vínculo por não-Owner passa por funções `SECURITY DEFINER` com `search_path` fixo: `leave_baby()`, `accept_invitation(token)`, `decline_invitation(token)`; Owner usa `set_member_role`, `remove_member`, `transfer_ownership`.
  2. Trigger `BEFORE UPDATE` de transição de estado: `baby_id`, `user_id`, `invited_by`, `invite_token_hash` imutáveis fora das funções; transições permitidas explicitamente (PENDING→ACTIVE/DECLINED/REVOKED, ACTIVE→REVOKED, nunca REVOKED/DECLINED→ACTIVE); `role` só muda por `set_member_role`/`transfer_ownership`.
  3. **Ordem de correção:** corrigir SR-002 **antes ou junto** com SR-004. Corrigir SR-004 isoladamente abre a tomada de tenant.
  4. Testes negativos obrigatórios: cada coluna e cada transição acima, executados como `nina_app`, devem falhar.
- **Agente responsável:** database-engineer (com dotnet-backend-engineer para o consumo das funções)

---

## 3. Achados — ALTO

### SR-003 — Tabelas de identidade, credenciais, tokens, assinatura e outbox sem RLS e com DML total para `nina_app`

- **Severidade:** ALTO
- **Componente:** banco (RLS/privilégios)
- **Requisitos:** SEC-001, SEC-003, SEC-011, SEC-024; STRIDE "I — vazamento de banco"
- **Evidência:** `0001_init.sql:1639-1646` concede `SELECT, INSERT, UPDATE` a `nina_app` em `app_user` (`:125`), `user_credential` (`:144`), `user_identity` (`:151`), `refresh_token` (`:179`), `recovery_request` (`:190`), `family` (`:215`), `subscription` (`:766`), `family_entitlement` (`:785`), `family_entitlement_member` (`:802`), `outbox_message` (`:746`); o bloco de RLS (`:1548-1626`) não cobre nenhuma delas. `GRANT DELETE` em `refresh_token`/`recovery_request` (`:1648`) também sem política.
- **Prova (executada):** como Dave (sem bebê): `SELECT id, email FROM nina.app_user` → 4 linhas (todos os e-mails); `SELECT user_id, password_hash FROM nina.user_credential` → hashes de Alice e Bob; `UPDATE nina.user_credential SET password_hash='$argon2id$ATACANTE' WHERE user_id=<Bob>` → `UPDATE 1` (tomada de conta no banco); `INSERT` em `family_entitlement` com `source='MANUAL_GRANT'` + `family_entitlement_member` HOLDER → `nina.user_plan_code(Dave)` = `premium`; `SELECT aggregate_id FROM nina.outbox_message` expõe o UUID de bebês de outros tenants.
- **Cenário de exploração:** qualquer SQL injection ou filtro esquecido em módulos Identity/Subscriptions/Privacy dá: dump de todos os e-mails e hashes, sobrescrita de senha e de tokens de recuperação (account takeover), concessão de premium sem pagar (SEC-024) e coleta de UUIDs de bebês para encadear com SR-002.
- **Correção recomendada:**
  1. Separar papéis: `nina_auth` (login/registro/refresh/recuperação) com acesso via funções `SECURITY DEFINER` estreitas (`verify_credentials`, `rotate_refresh_token`, `create_session`...), e `nina_app` **sem** acesso direto a `user_credential`, `refresh_token`, `recovery_request`.
  2. RLS em `app_user` (self), `user_identity` (self), `family_entitlement*`/`subscription` (somente leitura por membro; escrita só por `nina_worker`/função de validação de recibo), `outbox_message` (INSERT-only para o app, sem SELECT/UPDATE), `family` (SR-001).
  3. Remover `source='MANUAL_GRANT'` do alcance do app (CHECK por papel via função, ou tabela separada escrita só por `nina_config_admin`).
  4. Teste de regressão: matriz papel × tabela × operação gerada de `information_schema.role_table_grants`, comparada a um snapshot aprovado.
- **Agente responsável:** database-engineer

### SR-004 — `check_baby_has_owner` não é `SECURITY DEFINER`: aceitar convite, transferir propriedade e rebaixar-se são inviáveis sob RLS; a correção ingênua ativa SR-002

- **Severidade:** ALTO
- **Componente:** banco (integridade INV-09 / SEC-007)
- **Requisitos:** SEC-004, SEC-007; contrato `acceptInvitation`, `transferOwnership`, `removeCaregiver`
- **Evidência:** `0001_init.sql:289-302` — a função e o `CONSTRAINT TRIGGER` rodam com o papel do chamador, sob a política `caregiver_membership.app_select` (`:1609-1610`: só vê a própria linha ou, se for Owner, as do bebê). Um não-Owner nunca enxerga a linha do Owner; um Owner que se rebaixou deixa de enxergar o novo Owner.
- **Prova (executada):**
  - Carol (ACTIVE) altera o próprio vínculo → `COMMIT` falha: `ERROR: bebe ... sem Owner ativo (INV-09/SEC-007)` (NN010).
  - Transferência pelo Owner como `nina_app`: demote-first → o segundo `UPDATE` afeta 0 linhas (o Owner já perdeu o papel dentro da própria transação) e o commit falha com NN010; promote-first → `duplicate key ... membership_one_owner_uq` (índice parcial não pode ser `DEFERRABLE`). Transferir propriedade é, portanto, impossível com as políticas atuais.
  - Aceitar convite: convite `PENDING` sem `user_id` não é visível a nenhum outro usuário (`SELECT ... WHERE invite_token_hash = sha256('tok')` → 0 linhas; `UPDATE ... SET user_id=<convidado>, status='ACTIVE'` → `UPDATE 0`). Não existe função `accept_invitation`.
  - Sair do bebê funciona **por acidente**: após `REVOKED`, o bebê fica invisível ao usuário e o check é ignorado.
- **Cenário de exploração:** não é exploração direta, é pressão de desenho: para entregar RF-006/007 a equipe vai (a) conectar a API como `nina_worker` ou dono, anulando a RLS inteira (um login que seja membro de `nina_app` e `nina_worker` enxerga todos os bebês e usuários sem contexto, comprovado), ou (b) tornar a função `SECURITY DEFINER` e reabrir SR-002.
- **Correção recomendada:** `check_baby_has_owner` com `SECURITY DEFINER SET search_path = nina, pg_temp` **e** SR-002 corrigido no mesmo PR; criar `accept_invitation(p_token)`, `decline_invitation`, `leave_baby`, `transfer_ownership(p_baby, p_new_owner_membership)` (atômica, troca de papéis dentro da função, atualiza `baby.family_id` do novo dono, audita), todas `SECURITY DEFINER`, `search_path` fixo, `REVOKE ... FROM PUBLIC`, `GRANT EXECUTE` só a `nina_app`. `accept_invitation` valida: token por hash em tempo constante, expiração, `status='PENDING'`, e-mail verificado do usuário igual a `invited_email`, consentimento exigido, e grava `consent_record` com `source='INVITE_ACCEPT'` e auditoria.
- **Agente responsável:** database-engineer

### SR-005 — Trilha de auditoria e de consentimento "append-only" pode ser apagada por completo (worker + GUC; superuser/dono)

- **Severidade:** ALTO
- **Componente:** banco (integridade da auditoria)
- **Requisitos:** SEC-030, SEC-032; STRIDE "T — adulteração de auditoria", "R — disputa sobre consentimento" (prova F12 do privacy-spec)
- **Evidência:** `0001_init.sql:959-968` — `forbid_mutation` libera DELETE quando `current_setting('nina.retention_purge') = 'on'` e `pg_has_role(current_user, 'nina_worker', 'MEMBER')`. Não há limite de idade, de lote nem de tabela; o GUC é definido pelo próprio chamador. `pg_has_role` retorna verdadeiro para superuser. Triggers em `audit_event`, `consent_record`, `config_change` (`:969-974`). `GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES ... TO nina_worker` (`:1635`). O encadeamento (`:940-956`) só cobre eventos `is_critical` e não detecta truncamento do final da cadeia nem remoção total (não há âncora externa).
- **Prova (executada):** como `worker_login`: `BEGIN; SET LOCAL nina.retention_purge='on'; DELETE FROM nina.audit_event WHERE is_critical;` → `DELETE 18`; restou 1 linha (a não crítica). Nenhum rastro da remoção (a própria remoção não gera evento).
- **Cenário de exploração:** worker comprometido (ou insider com a credencial do worker) executa uma exclusão, apaga a evidência de `account.erased`, `baby.erased`, `config.changed`, `privacy.erasure_completed` e das provas de consentimento exigidas para defesa jurídica (art. 7º, VI; F12).
- **Correção recomendada:**
  1. Remover DELETE direto das tabelas append-only do worker; criar `purge_audit(p_older_than interval)` e `purge_consent(...)` `SECURITY DEFINER` com piso mínimo de idade (12 meses audit, prazo prescricional consent; parâmetros em `app_parameter` com validação), que registram **antes** um evento de auditoria de sistema com contagem e faixa de `id`.
  2. Remover o GUC como autorização; autorizar por papel dedicado `nina_retention` ou por dono da função.
  3. Ancorar a cadeia: exportar periodicamente `(chain_seq, row_hash)` para armazenamento WORM/externo (SEC-032) e verificar na leitura; incluir todos os eventos de segurança na cadeia, não só `is_critical`.
  4. Separar o worker em papéis por função (`nina_outbox`, `nina_retention`, `nina_erasure`) em vez de DML em tudo.
- **Agente responsável:** database-engineer (ancoragem externa: cloud-backend-engineer)

### SR-006 — Auditoria forjável por `nina_app` e `nina_config_admin`; deny-list de PII superficial

- **Severidade:** ALTO
- **Componente:** banco (auditoria)
- **Requisitos:** SEC-030, SEC-032, SEC-061; INV-28 ("sem PII")
- **Evidência:** `0001_init.sql:1652` (`GRANT INSERT ON nina.audit_event TO nina_app`) e `:1671` (idem `nina_config_admin`); `audit_event` não tem RLS e nenhum CHECK/trigger vincula `actor_user_id` ao contexto, restringe `actor_type`/`action` por papel ou limita `is_critical` (`:911-932`). `audit_metadata_ck` (`:929-932`) é lista negativa de chaves de topo.
- **Prova (executada):** como Dave: `INSERT INTO nina.audit_event(actor_user_id, actor_type, action, entity_type, entity_id, is_critical) VALUES (<Alice>, 'ADMIN', 'privacy.erasure_completed', 'USER', <Alice>, true)` → aceito e **encadeado** como evento crítico legítimo (`chain_seq 15`); idem 3 eventos `SUPPORT`/`config.changed` em nome de Bob. `metadata_safe = {"detalhe":{"email":"alice@ex.org","nome":"Alice Silva"},"e_mail":"bob@ex.org"}` → aceito (PII aninhada ou com outro nome passa). `cfg_login` também insere eventos arbitrários.
- **Cenário de exploração:** quem compromete o app fabrica evidência (atribuir `account.erased` a um administrador, "provar" que um consentimento foi dado) ou polui a cadeia para tornar a auditoria inútil; PII entra em `metadata_safe` e na retenção longa de 12 meses a 5 anos.
- **Correção recomendada:** remover `INSERT` direto em `audit_event` do app e do config_admin; expor `nina.audit(p_action, p_entity_type, p_entity_id, p_baby, p_metadata)` `SECURITY DEFINER` que **força** `actor_user_id = current_user_id()`, `actor_type='USER'`, valida `action` contra catálogo (tabela `audit_action`), `result`, tamanho e allowlist de chaves de `metadata_safe` (schema por ação), e que calcula `ip_hash` com HMAC e pepper. Eventos `SYSTEM`/`ADMIN` só por funções do worker/config.
- **Agente responsável:** database-engineer

---

## 4. Achados — MÉDIO

### SR-007 — `open_privacy_request` com default fail-open e `ANONYMIZATION` sem janela de arrependimento (banco diverge do contrato)

- **Severidade:** MÉDIO
- **Requisitos:** SEC-014, SEC-017 (reautenticação), art. 18 LGPD; AD-05, AD-28
- **Evidência:** `0001_init.sql:1358` — `open_privacy_request(p_type text, p_verification text DEFAULT 'REAUTHENTICATION')`: omitir o argumento **declara** a reautenticação (o bloco `NN014` em `:1368-1370` só dispara com `NULL` explícito). `privacy_request` (`:1144-1161`) não tem `scheduled_for` nem os status `IN_PROGRESS`/`SCHEDULED` do contrato (`openapi.yaml:3940-3946`, `:3956-3959`; `api-spec.md:42`, PA-31). `fulfill_privacy_erasure` (`:1415-1444`) executa na hora, sem consultar janela.
- **Prova (executada):** `SELECT nina.open_privacy_request('ANONYMIZATION')` como Carol → linha com `identity_verified_at` preenchido e `verification_method='REAUTHENTICATION'`; `fulfill_privacy_erasure` como worker → `app_user` com `status='DELETED'`, vínculos removidos, pedido `COMPLETED`, sem espera.
- **Cenário de exploração:** (a) bug ou chamada que omita o parâmetro cria pedido "verificado" sem reautenticação; (b) sessão roubada + reautenticação facilitada resulta em anonimização **irreversível** assim que o worker rodar, contra a promessa do contrato (SCHEDULED, 7 dias, cancelável). Não apaga dados de bebê, mas destrói a conta e o histórico do titular.
- **Correção recomendada:** remover o DEFAULT (obrigatório e validado contra `NOT NULL`); acrescentar `scheduled_for` (= `requested_at + privacy.deletion_grace_days`, congelado por trigger como em `account_deletion_guard`), status `SCHEDULED`, e `fulfill_privacy_erasure` recusar antes de `scheduled_for` (NN008). Notificar o e-mail da conta no pedido e no cancelamento.
- **Agente responsável:** database-engineer (contrato: api-contract-engineer confirma estados)

### SR-008 — GUCs de controle são definíveis pelo chamador (autoria, migração, retenção, identidade)

- **Severidade:** MÉDIO
- **Requisitos:** SEC-001, SEC-030, SEC-032
- **Evidência:** `nina.authorship_scrub`/`nina.scrub_user` (`0001_init.sql:398-404`, `:426-431`), `nina.migration` (`:983`), `nina.retention_purge` (`:962`), `nina.user_id` (`:53-55`), `nina.device_id` (`:457`). GUCs com prefixo custom podem ser definidos por qualquer papel.
- **Prova (executada):**
  - Dave (CAREGIVER do bebê do Bob): `set_config('nina.authorship_scrub','on',true)`, `set_config('nina.scrub_user','<Bob>',true)`, `UPDATE nina.sleep_session SET created_by=created_by WHERE id=<evento do Bob>` → `created_by` passa a `NULL`, sem nova versão, sem `change_log`, sem auditoria (apaga evidência de autoria; STRIDE "R").
  - `cfg_login` sem `nina.user_id`: update de `app_parameter` falha (NN040); com `SET LOCAL nina.migration='on'` → aceito, registrado em `config_change` e `audit_event` com `actor_user_id` nulo e `actor_type='SYSTEM'` (evasão de atribuição, ADR-0005).
  - `nina.user_id`: o app pode definir qualquer UUID (personificação trivial se houver injeção). `SET LOCAL nina.user_id = '...'` não aceita parâmetros: concatenar string é um risco de injeção (o código em curso em `Nina.SharedKernel/Data/DbTx.cs` usa `Guid` formatado, ok, mas não foi auditado).
- **Correção recomendada:** (1) tirar a autorização desses GUCs: scrub de autoria e retenção só dentro de funções `SECURITY DEFINER` que definem o GUC internamente e o limpam; `sync_stamp_*` deve ignorar o GUC quando `session_user` não for o worker/dono; (2) `nina.migration` removido do trigger (seed por `session_replication_role` ou ator `SYSTEM` explícito); (3) DbTx deve usar `set_config('nina.user_id', $1, true)` parametrizado, `DISCARD ALL`/`RESET` ao devolver a conexão ao pool, e teste que falha se houver `SET` sem `LOCAL`; (4) opcional: validar `nina.user_id` contra a sessão (ex.: HMAC do contexto assinado pela API e verificado em `current_user_id()`).
- **Agente responsável:** database-engineer; dotnet-backend-engineer (DbTx/pool)

### SR-009 — Owner cria vínculos `ACTIVE` sem aceite; convite não é verificável no banco

- **Severidade:** MÉDIO (explorável via API normal se o endpoint aceitar `status`)
- **Requisitos:** SEC-004, SEC-016, SEC-051; privacy-spec F4/§3.4 (aceite informado do convidado)
- **Evidência:** `0001_init.sql:1611-1613` (INSERT livre para o Owner) com apenas `membership_active_ck` (`:271`, exige `accepted_at` não nulo, mas não valida origem). `invite_token_hash` (`:263`) sem `used_at`/contador de tentativas; nada vincula token a e-mail verificado. Nenhum limite diário de convites (SEC-004).
- **Prova (executada):** Owner Bob executa `INSERT INTO nina.caregiver_membership(baby_id, user_id, role, status, accepted_at) VALUES (<bebê>, <Dave>, 'CAREGIVER', 'ACTIVE', now())` → aceito. Dave passa a ter acesso, nunca aceitou Termos nem convite.
- **Cenário:** um Owner (ou um endpoint com mass assignment) adiciona qualquer usuário a um bebê sem consentimento do adulto; o adulto tem dados (nome, id) expostos e passa a receber notificações. Em disputa de guarda (DJ-09) vira vetor de assédio.
- **Correção recomendada:** INSERT do Owner limitado a `status='PENDING'`, `user_id IS NULL OR existente`, sem `accepted_at`; ativação **somente** por `accept_invitation` (SR-004). Adicionar `used_at`, `failed_attempts`, `invited_by_day` (contagem por dia para o limite), e CHECK de e-mail normalizado.
- **Agente responsável:** database-engineer; api-contract-engineer (limites de convite documentados)

### SR-010 — Dono da família (ex-Owner) mantém leitura do perfil do bebê após perder o vínculo

- **Severidade:** MÉDIO
- **Requisitos:** SEC-005, RB-015; STRIDE "I — ex-cuidador mantém acesso"; DJ-09
- **Evidência:** `0001_init.sql:1601-1602` (`OR nina.is_family_owner(family_id)`); a função `erase_user` move `family_id` no `TRANSFER_OWNERSHIP` (`:1323-1327`), mas a transferência do contrato (`POST /babies/{id}/ownership-transfer`, `openapi.yaml:885-916`) e a remoção de cuidador não têm função equivalente no banco.
- **Prova (executada):** Alice (dona da família), com vínculo `REVOKED` (`OWNERSHIP_TRANSFERRED`) e outro Owner ativo: `nina.can_read_baby(bebê)` = falso, porém `SELECT display_name, birth_date FROM nina.baby` retorna o perfil (eventos não, `0 linhas`).
- **Cenário:** guarda compartilhada/separação (R4 do RIPD): quem foi removido continua vendo nome e data de nascimento da criança indefinidamente.
- **Correção recomendada:** eliminar o ramo `is_family_owner` do SELECT (SR-001) e mover `family_id` junto com a titularidade em `transfer_ownership`.
- **Agente responsável:** database-engineer

### SR-011 — Exclusão deixa resíduos: exportações `READY`, referências em consentimento, objeto de foto e ausência de purga de auditoria/consentimento

- **Severidade:** MÉDIO
- **Requisitos:** art. 18, VI e art. 16 LGPD; privacy-spec §4/§5.5; SEC-064
- **Evidência:** `erase_baby` (`0001_init.sql:1168-1198`) não toca `data_export_request`; `scrub_user_personal_data` só zera exportações do usuário-alvo (`:1249`). `consent_record.subject_baby_id` permanece (`:892`, aceito por desenho). `photo_ref` é zerado (`:1191`) mas o apagamento do objeto depende de consumidor do `outbox` inexistente. Não há job de purga para `audit_event`, `consent_record`, `config_change`, `data_export_request`, `privacy_request`, `account_deletion_request`, `app_user` anonimizado (somente `purge_expired_*`, `:1448-1541`). `change_log.actor_user_id` persiste até 90 dias (`:362`).
- **Prova (executada):** `worker_login: SELECT nina.erase_baby(<bebê do Bob>)`; depois `data_export_request` de Bob e de Dave continuam `READY` com `file_ref` (`s3://exports/...`) por até 7 dias; `consent_record` mantém o UUID do bebê excluído.
- **Cenário:** exportação com todos os eventos de uma criança excluída continua baixável por 7 dias por qualquer cuidador (inclusive um cuidador que acabou de ser revogado, se a sessão ainda for válida).
- **Correção recomendada:** em `erase_baby`, invalidar (`status='EXPIRED'`, `file_ref=NULL`) todas as exportações cujo conteúdo inclua o bebê (adicionar `baby_ids uuid[]` a `data_export_request`) e emitir `outbox` para remover o arquivo no storage; criar purgas controladas (SR-005) com os prazos de privacy-spec §4; documentar reaplicação de exclusões após restore (SEC-064) com ledger `erasure_ledger(entity, id, erased_at)` que sobrevive aos backups de 35 dias.
- **Agente responsável:** database-engineer; cloud-backend-engineer (storage de exports/foto, backups)

### SR-012 — Privilégios: `PUBLIC` em funções futuras, `TEMP`, papéis combinados, oráculos e grants supérfluos

- **Severidade:** MÉDIO
- **Requisitos:** SEC-001, SEC-023
- **Evidência:** `0001_init.sql:1631-1632` só revoga de `PUBLIC` o que **já existe**; não há `ALTER DEFAULT PRIVILEGES`. Papéis são criados "se não existirem" sem reafirmar atributos (`:32-34`). `GRANT SELECT, INSERT, UPDATE` em `notification_job`, `outbox_message` e `sync_mutation` (`:1639-1646`) sem política correspondente (INSERT/UPDATE negados pela RLS: grants mortos). `is_active_baby_owner(uuid)`, `user_plan_code(uuid)` e `user_has_feature(uuid, text)` (`:1201-1205`, `:839-860`, executáveis por `nina_app`, `:1653-1659`) aceitam UUID arbitrário.
- **Prova (executada):** função nova criada pelo dono depois da migração (`CREATE FUNCTION nina.zz_future() ...`) → executável por `app_login` (herda de `PUBLIC`); `has_database_privilege('app_login','nina','TEMP')` = verdadeiro; `SELECT nina.user_plan_code(<Bob>), nina.is_active_baby_owner(<Bob>)` como Dave → `free | f` (oráculo de plano/papel de terceiros); login membro de `nina_app` **e** `nina_worker` sem contexto: `count(*) FROM nina.baby` = todos, `app_user` = todos (soma das políticas).
- **Correção recomendada:** `ALTER DEFAULT PRIVILEGES IN SCHEMA nina REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC` (e tabelas/sequências); `REVOKE TEMP ON DATABASE ... FROM PUBLIC`; `ALTER ROLE ... NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT` reafirmado; `FORCE ROW LEVEL SECURITY` nas tabelas de tenant; funções de plano/papel chamadas só com `current_user_id()` (sem parâmetro); restringir logins para que **nenhum login herde dois papéis** (teste de catálogo no CI: `pg_auth_members`); remover grants mortos.
- **Agente responsável:** database-engineer; cloud-backend-engineer (logins distintos e secrets separados)

### SR-013 — Contrato: reautenticação sem escopo, rate limit incompleto, limites de tamanho, JWT e códigos de verificação

- **Severidade:** MÉDIO
- **Requisitos:** SEC-010, SEC-011, SEC-014, SEC-040, SEC-041, SEC-043
- **Evidência / detalhe:**
  1. **Reauth** (`openapi.yaml:2237-2242`, `:2657-2672`; `api-spec.md:19`): `reauth_token` genérico de 5 min, sem vínculo a sessão (`sid`), finalidade (`aud=export|delete|transfer`...) nem uso único; `ReauthRequest` não exige `password` **ou** `provider+id_token` (sem `oneOf`/`required`). `POST /me/data-exports` não exige reauth (só o download, `openapi.yaml:1772`), contra privacy-spec §5.1 (reautenticação antes de exportar/excluir).
  2. **429 ausente em 48 das 70 operações**, incluindo `POST /me/deletion-request`, `POST /me/data-exports/{id}/download-links`, `POST /me/email-changes/confirm`, `POST /invitations/decline`, `POST /me/consents` e todas as de bebê. AD-17 declara limite global, mas o contrato por operação não o reflete (geração de SDK/testes de contrato).
  3. **Sem `maxLength`** em `password` (Argon2id sobre senha de MB = DoS de CPU/memória; bcrypt trunca em 72 bytes), `code`, `token`, `id_token`, `nonce`, `refresh_token`, `timezone`, `locale`, `tz`, `product_id` e tokens de compra (`openapi.yaml:2558-2562`, `:2583-2589`, `:2597-2605`); `RegisterRequest`, `LoginRequest`, `ReauthRequest` sem `additionalProperties: false`. Notas: contrato 500 vs. banco 2000 (PA-21), `volume_ml` inteiro sem teto (o limite vem de `/reference-data`, `:4081-4084`).
  4. **JWT:** `bearerAuth` (`:2164-2169`) não fixa algoritmo, `iss`/`aud`, claims (`sid`, `jti`), verificação de revogação por sessão nem rotação de chaves. Com access token stateless de 15 min, "revogação imediata" (SEC-005/RB-015) exige checagem de `sid` e de vínculo a cada requisição.
  5. **Códigos** de e-mail (`/auth/email/verify`, `/me/email-changes/confirm`): tamanho, entropia, TTL e contador de tentativas não especificados (força bruta do código de 6 dígitos em janela de minutos).
  6. `POST /auth/password/reset` (`:310`) "encerra todas as **outras** sessões": no reset não há sessão corrente; devem ser todas (SEC-017).
- **Correção recomendada:** `reauth_token` assinado com `sid`, `aud` (finalidade), `jti` de uso único para ações destrutivas, `exp` ≤ 5 min; `oneOf` em `ReauthRequest`; reauth também no `POST /me/data-exports`; 429 + `Retry-After` em todas as operações com custo/risco (ou `x-ratelimit` global documentado e testado); `maxLength` (senha 128, códigos 12, tokens 2–4 KiB) e `additionalProperties: false`; perfil JWT (EdDSA/ES256 ou RS256 fixado, `iss`, `aud`, `sid`, `jti`, tolerância de relógio ≤ 60 s, `kid` com rotação); política de código (≥ 8 dígitos ou 128 bits, TTL 10 min, 5 tentativas, uso único); reset invalida todas as sessões.
- **Agente responsável:** api-contract-engineer; dotnet-backend-engineer (implementação)

### SR-014 — Sync: last-write-wins por relógio do cliente, quotas, verificação cruzada de `baby_id` e colisão de chaves globais

- **Severidade:** MÉDIO
- **Requisitos:** SEC-042, SEC-006; STRIDE "T — mutação replay/sync adulterado", "D — abuso de API/sync"
- **Evidência:**
  - `MutationBase.client_created_at` (`openapi.yaml:3290-3293`) é "preservado" e alimenta LWW por campo (`api-spec.md:74`, ADR-0003): um cuidador (inclusive um revogado ainda com sessão válida, ou relógio adulterado) envia `client_created_at` no futuro e vence todo conflito indefinidamente; `CLIENT_CLOCK_SKEW` só gera aviso (`:3364-3372`).
  - Limites: 100 mutações e 256 KiB por push (AD-17); **não há** quota por bebê/dia nem teto de entidades por bebê (crescimento de armazenamento por cuidador).
  - `entity_id` e `mutation_id` globais: `sleep_session.id uuid PRIMARY KEY` (`0001_init.sql:475`) e `sync_mutation.mutation_id uuid PRIMARY KEY` (`:371`).
  - A autorização do push é por mutação; a API precisa garantir que `entity_id` pertence ao `baby_id` informado (o banco só protege pela RLS, porque UPDATE filtra por `id`).
- **Prova (executada):** Alice insere `sleep_session` em seu bebê com o `id` de um evento do Bob → `ERROR duplicate key ... sleep_session_pkey` (existência de ID entre tenants); `sync_mutation` com `mutation_id` já usado por Bob → `duplicate key ... sync_mutation_pkey` (um tenant "queima" o `mutation_id` de outro; exige conhecer o UUID).
- **Correção recomendada:** LWW por **tempo do servidor** (`received_at`) com `client_created_at` só informativo, ou limitar o skew aceito (ex.: ±24 h, fora disso `REJECTED CLOCK_SKEW`); quotas (`sync.max_events_per_baby_per_day`, tamanho total) com 429; chave primária composta `(baby_id, id)` em tabelas sincronizáveis e `(user_id, device_id, mutation_id)` ou `(baby_id, mutation_id)` em `sync_mutation`; testes de que `UPDATE`/`DELETE` com `entity_id` de outro bebê retorna `REJECTED ENTITY_NOT_FOUND` (nunca revela existência); cursor assinado (HMAC) vinculado a `baby_id` e à versão do formato, com teste de `410 INVALID` ao trocar de bebê (o exemplo `eyJ2IjoxLCJwIjoiYWJjIn0.q8Zx` sugere assinatura; não verificável).
- **Agente responsável:** api-contract-engineer (regra LWW/quotas no contrato); database-engineer (PKs); dotnet-backend-engineer (implementação)

### SR-015 — Minimização e retenção (LGPD): campo `sex` coletado, `ip_hash` sem pepper, `approx_location` sem base no banco, lacunas de retenção

- **Severidade:** MÉDIO
- **Requisitos:** privacy-spec §1.3 (sexo: **"Não coletar"**), §4, SEC-031, SEC-061; art. 6º, III (necessidade)
- **Evidência:**
  - `baby.sex` (`0001_init.sql:227`), `BabyCreate.sex`/`BabyUpdate.sex`/`Baby.sex` (`openapi.yaml:2804-2806`, `:2852-2854`, `:2866-2868`) coletam o dado que a especificação de privacidade manda **não coletar**. Divergência adicional: contrato aceita `OTHER`; banco só `FEMALE|MALE|UNSPECIFIED` (`:227`) → erro de CHECK em runtime com valor válido do contrato.
  - `audit_event.ip_hash bytea(32)` (`:923`) sem especificar HMAC/pepper: hash de IPv4 sem segredo é reversível por força bruta (2^32).
  - `Session.approx_location` (`openapi.yaml:2715-2717`) pressupõe geolocalização por IP, sem coluna, base legal ou descrição no inventário (§1.2); `auth_session` guarda `user_agent` e `device_label` (`:166-168`).
  - Retenção: `auth_session` conserva `user_agent`/`device_label` 30 dias após expiração (`:1509-1511`); `change_log` guarda `actor_user_id` 90 dias; `audit_event` sem purga (SR-011).
- **Correção recomendada:** retirar `sex` do contrato v1 antes do primeiro cliente (campo opcional removível sem quebra; se for mantido, registrar decisão jurídica DJ-01 e alinhar enum); `ip_hash = HMAC-SHA256(pepper_rotativo, ip_truncado)`; decidir/remover `approx_location`; incluir purga e prazos no `purge_expired_operational_data` com testes.
- **Agente responsável:** api-contract-engineer (contrato); database-engineer (hash/retenção)

### SR-016 — Exclusão em cascata e `DELETE /babies/{id}`: sem notificação aos demais cuidadores; confirmação não verificável no banco

- **Severidade:** MÉDIO
- **Requisitos:** SEC-014, SEC-005; privacy-spec R4/DJ-09; STRIDE "R"
- **Evidência:** política padrão `CASCADE` (`0001_init.sql:1269`, seed `:1686`; `openapi.yaml:1872-1880`); `DELETE /babies/{id}` apaga imediatamente, sem janela (`openapi.yaml:730-751`); contrato não prevê notificação aos cuidadores nem ao e-mail do titular no pedido (privacy-spec §5.1 pede "notificação por e-mail a cada pedido"). `account_deletion_request.confirmed_at`/`confirmation_method` são fornecidos pelo cliente do banco (`:1086-1087`; INSERT liberado, `:1588-1589`).
- **Prova (executada):** Dave (Owner) insere pedido com `confirmed_at=now()`, `confirmation_method='REAUTHENTICATION'` e `grace_days=1`, `status='COMPLETED'`: o trigger força `status='SCHEDULED'`, `grace_days=7`, `scheduled_for=+7d` (**bom**), mas aceita a "confirmação" sem prova de reautenticação.
- **Cenário:** Owner em conflito (ou conta invadida) apaga o histórico da criança também para o outro responsável legal, sem aviso; os outros cuidadores só descobrem depois da purga.
- **Correção recomendada:** notificar (e-mail/push) o titular e **todos os cuidadores ativos** ao criar e ao concluir o pedido de exclusão que afete bebê compartilhado, com direito de exportação do próprio recorte durante a janela; janela também para `DELETE /babies/{id}` quando houver outros cuidadores; gravar `reauth_jti` (id do token de reautenticação, SR-013) em `account_deletion_request` e validar a existência/expiração dentro de função `SECURITY DEFINER`, em vez de aceitar `confirmed_at` livre.
- **Agente responsável:** api-contract-engineer; dotnet-backend-engineer; database-engineer

### SR-017 — Cadeia de suprimentos: CI, imagens, scripts e NuGet

- **Severidade:** MÉDIO
- **Requisitos:** SEC-023, SEC-060, SEC-062
- **Evidência:**
  - `.github/workflows/ci.yml:23-24,39`: ações por tag (`@v4`), não por SHA. Há `permissions: contents: read` (`:8-9`, bom). Sem SAST (CodeQL/Sonar no CI, embora `sonar-project.properties` exista), sem secret scanning, sem varredura de imagem, sem SBOM, sem lint do OpenAPI nem teste do SQL em PostgreSQL (RLS), sem `dependabot.yml`/`CODEOWNERS`. A varredura `grep -qE "^\s+>"` (`:35-38`) depende do formato de saída do `dotnet list`.
  - `backend/Directory.Packages.props:5-10` e `Directory.Build.props:14-16`: `NuGetAudit` (bom, com `TreatWarningsAsErrors`), mas **sem** `RestorePackagesWithLockFile`/`packages.lock.json`, sem `CentralPackageTransitivePinningEnabled`, sem `nuget.config` com `packageSourceMapping` (confusão de dependência).
  - `infra/docker/Dockerfile.service:1,8` e `Dockerfile.dev:2`: imagens por tag (`sdk:10.0`, `aspnet:10.0`), não por digest; `docker-compose.yml:4` `postgres:17` enquanto a migração é "validada em 16".
  - `infra/openshift/nina-api.yaml:20-21` / `nina-bff.yaml:20-21`: `REGISTRY/nina-*:TAG` com `imagePullPolicy: IfNotPresent` (tag mutável + cache de nó).
  - `scripts/setup-dev-env.sh:8-9,14-16`: `dotnet-install.sh` e `commandlinetools-linux-*.zip` baixados sem verificação de checksum/assinatura, para caminho previsível em `/tmp` (`/tmp/dotnet-install.sh`, `/tmp/cmdtools.zip`) e executados como root (risco de corrida/symlink em host compartilhado); licenças aceitas automaticamente (`yes | ... --licenses`, `:19`).
- **Correção recomendada:** fixar ações por SHA; jobs de CodeQL/Sonar, `gitleaks`/secret scanning, Trivy/Grype na imagem, geração de SBOM (CycloneDX) e assinatura (cosign) no build; job que sobe PostgreSQL 16 e roda `0001_init.sql` + suíte de RLS como `nina_app`; lint do OpenAPI; `packages.lock.json` com `--locked-mode` e `nuget.config` com mapeamento de fontes; imagens por digest e `imagePullPolicy: Always` ou deploy por digest; script de setup com `mktemp -d`, checagem de SHA-256 publicada e sem execução como root quando evitável; alinhar `postgres:16` ao alvo validado.
- **Agente responsável:** cloud-backend-engineer

### SR-018 — Containers, OpenShift e compose: superusuário na API, porta do banco exposta, rede aberta, Route sem endurecimento

- **Severidade:** MÉDIO
- **Requisitos:** SEC-020, SEC-023, SEC-040, SEC-062; ADR-0006
- **Evidência:**
  - `docker-compose.yml:7,22`: `POSTGRES_USER` (superusuário do cluster) é o `Username` da API → a RLS não se aplica (superuser) e o ambiente local mascara os achados SR-001..SR-006; nenhum passo cria `nina_app`/`nina_worker`/logins nem aplica a migração. `:9` publica `5432:5432` em todas as interfaces; `.env.example:2` traz `POSTGRES_PASSWORD=troque-me`. `:23-25,34-36` `ASPNETCORE_ENVIRONMENT: Development` e `volumes: [".:/src"]` (repositório inteiro, incluindo `.env`, montado nos contêineres; portas `5080/5081` em todas as interfaces).
  - `infra/openshift/`: **sem `NetworkPolicy`** (nenhum default-deny; a API sem autenticação acessível de qualquer pod do namespace); a `Route` (`nina-bff.yaml:61-72`) não define `haproxy.router.openshift.io/hsts_header`, limites de conexões/taxa (`rate-limit-connections*`), timeout nem política TLS mínima (depende do IngressController); `Api__BaseUrl: http://nina-api:8080` (`configmap.yaml:8`): tráfego BFF→API em claro, sem mTLS nem token de serviço; um único `Secret` `ConnectionStrings__Default` (`nina-api.yaml:26-29`), sem separação de logins app/worker/config (a RLS depende disso, SR-004/SR-012); sem `PodDisruptionBudget`, sem `ASPNETCORE_FORWARDEDHEADERS_ENABLED`/`KnownProxies` (rate limit por IP e HSTS dependem do IP real atrás da Route edge).
  - Pontos positivos mantidos: `runAsNonRoot`, `readOnlyRootFilesystem`, `drop: ALL`, `seccompProfile`, `automountServiceAccountToken: false`, Secret só por referência.
- **Correção recomendada:** compose com bootstrap SQL que cria `nina_owner` (migração), `nina_app_login`, `nina_worker_login`; API conectada **somente** como login do `nina_app`; bind do banco em `127.0.0.1`, senha gerada; remover o mount do repositório inteiro (montar só `backend/`); `NetworkPolicy` default-deny + allow BFF→API e API→Postgres; segredo/credencial de serviço BFF↔API (token curto ou mTLS do service mesh); anotações de Route (HSTS, rate limit, timeouts); Secrets separados por papel de banco; `ForwardedHeaders` com redes confiáveis.
- **Agente responsável:** cloud-backend-engineer

### SR-019 — Esqueleto sem controles de aplicação (autenticação/autorização, TLS interno, headers, rate limit, log seguro)

- **Severidade:** MÉDIO (passa a ALTO ao expor a primeira rota de negócio)
- **Requisitos:** SEC-001, SEC-020, SEC-040, SEC-041, SEC-053, SEC-061, SEC-065
- **Evidência:** `backend/src/Nina.Api/Program.cs:1-14`, `backend/src/Nina.Bff/Program.cs:1-29`: nenhuma autenticação, autorização, `UseHsts`/`UseHttpsRedirection`, headers de segurança, `ExceptionHandler`/`ProblemDetails`, rate limiter, `ForwardedHeaders`, limite de corpo (`MaxRequestBodySize` para os 256 KiB do push), CORS ou política de log sem PII. `ASPNETCORE_URLS=http://+:8080` (`Dockerfile.service:10`). O README do backend declara estas pendências ("autenticação (ADR-0007)").
- **Correção recomendada:** antes da primeira rota: authN JWT (SR-013 perfil), políticas de autorização por bebê/papel com testes de matriz, `ProblemDetails` uniforme (SEC-053), `Kestrel` limits, rate limiter por IP/conta/dispositivo, HSTS e headers no BFF, redação de logs (nunca corpo de requisição de bebê, nunca token), healthchecks sem listar módulos (SR-020).
- **Agente responsável:** dotnet-backend-engineer

---

## 5. Achados — BAIXO

### SR-020 — Divulgação de informação evitável

- **Severidade:** BAIXO
- **Evidência:** `HealthEndpoints.cs:11` (`/ready` da API lista os módulos); BFF `/ready` público pela Route revela indisponibilidade da API (`Nina.Bff/Program.cs:21-24`); mensagens `RAISE EXCEPTION` do banco incluem UUIDs e detalhe de negócio (`0001_init.sql:296`, `:393`, `:1285`) e devem ser mapeadas por `ERRCODE` sem repassar o texto (SEC-053); `Event.created_by` expõe o UUID do usuário autor a todos os cuidadores (`openapi.yaml:1038`, `UserRef` `:2512-2518`); `404` vs `403 ACCESS_REVOKED` distingue ex-membros (PA-09, aceito, `api-spec.md:212`); `ALREADY_MEMBER` (`openapi.yaml:836`) revela que um e-mail com conta já pertence ao bebê (visível só ao Owner).
- **Correção:** `/ready` interno apenas (porta/rota não exposta) e sem lista de módulos; mapeamento de `ERRCODE` → `code` do Problem; avaliar `id` opaco por bebê para autoria; manter PA-09 como risco aceito documentado.
- **Agente responsável:** dotnet-backend-engineer; cloud-backend-engineer (Route só para rotas do contrato)

### SR-021 — Inconsistências contrato ↔ banco com efeito sobre validação

- **Severidade:** BAIXO
- **Evidência:** `PurposeKey` em MAIÚSCULAS (`openapi.yaml:3778-3781`) vs `consent_purpose.purpose_key ~ '^[a-z]...'` minúsculo (`0001_init.sql:880`, seed `:1693-1699`); `Membership.status` inclui `EXPIRED` (`openapi.yaml:2889`) que o banco não tem (`:261`); `deletion_grace_days` mínimo 0 no contrato (`:3899-3902`, `:4041-4045`) vs 1..30 no banco (`:1033-1037`); `Baby.sex` `OTHER` vs `UNSPECIFIED` (SR-015); `notes` 500 vs 2000; `volume_ml` inteiro vs `numeric(6,1)`; `Session.platform` IOS|ANDROID vs IOS|ANDROID|WEB; `PrivacyRequest.status` com `IN_PROGRESS`/`SCHEDULED` inexistentes no banco (SR-007); `privacy_request_ack_hours` só no contrato (`docs/project/status.md`).
- **Também (provas O4/O5):** `device_push_token` tem `UNIQUE (platform, token)` (`0001_init.sql:208`) e a RLS impede o novo usuário do mesmo aparelho de reatribuir o token (`duplicate key`; `ON CONFLICT DO UPDATE` bloqueado), o que trava `PUT /me/push-tokens/{device_id}` e, se contornado com privilégio, faria notificações de um bebê chegarem ao usuário anterior do aparelho. Precisa de função definer `register_push_token` que remove o token do dono anterior. `consent_record.subject_baby_id` aceita UUID de bebê sem vínculo do usuário (`:1571-1572`; prova O5): validar vínculo ativo para `child_data_guardian`.
- **Correção:** tabela de mapeamento única e testes de contrato↔DDL (gerados) no CI; congelar o contrato só depois do alinhamento (breaking após v1 exige `/v2`).
- **Agente responsável:** api-contract-engineer; database-engineer

### SR-022 — `Dockerfile.service`: shell form sem `exec`, `USER $APP_UID`, ausência de `HEALTHCHECK`

- **Severidade:** BAIXO
- **Evidência:** `infra/docker/Dockerfile.service:13,15`: `ENTRYPOINT ["sh","-c","dotnet $PROJECT_DLL"]` deixa o `sh` como PID 1 e o `dotnet` sem receber `SIGTERM` (desligamento não gracioso, perda de requisições de sync em rollout); `USER $APP_UID` depende de a imagem base definir `APP_UID` (hoje define; falha silenciosa se mudar); sem `HEALTHCHECK` (o OpenShift usa probes, mitiga).
- **Correção:** `ENTRYPOINT ["dotnet", "Nina.Api.dll"]` por estágio (ARG→ENV→JSON exec form) ou `exec dotnet`; `USER 1654:0` explícito; opcional `HEALTHCHECK`.
- **Agente responsável:** cloud-backend-engineer

---

## 6. Matriz de cobertura SEC-001..067

Legenda: **Coberto** = especificação e artefato (contrato/DB/infra) atendem sem lacuna material; **Parcial** = há desenho, mas incompleto, com defeito ou sem implementação/teste; **Lacuna** = sem artefato; **N/A** = fora do escopo do backend/MVP (cliente ou futuro). Os IDs não listados (008, 009, 018, 019, 026-029, 034-039, 044-049, 054-059) não existem em `privacy-security-spec.md` §7. São **41 requisitos definidos**.

| ID | Requisito (resumo) | Situação | Evidência / comentário | Achados |
|---|---|---|---|---|
| SEC-001 | Autorização server-side em toda consulta/mutação | Parcial | Contrato (AD-07, `x-roles`) e RLS nas tabelas de evento; sem código; RLS contornável | SR-001, 002, 003, 019 |
| SEC-002 | Papéis por bebê + matriz de testes | Parcial | Papéis no DB/contrato; ReadOnly não escreve (provado); sem suíte; autoescalação latente | SR-002, 004 |
| SEC-003 | Filtro por `baby_id` + vínculo ativo; RLS | Parcial | RLS nas tabelas de eventos, sync, `baby` e `caregiver_membership`; bypass por `family`/`baby_select` | SR-001, 003, 010 |
| SEC-004 | Convites: token ≥128 bits, hash, 7 dias, uso único, e-mail, revogável, limite/dia | Parcial | `invite_token_hash`, `invite_expires_at`; sem `used_at`/tentativas/limite diário/ligação a e-mail; aceite inexpressível | SR-004, 009 |
| SEC-005 | Revogação imediata (sessões, caches, tokens) | Parcial | Status `REVOKED`; reativação própria possível; JWT sem checagem de `sid` | SR-002, 013 |
| SEC-006 | Prevenção de IDOR, 404 uniforme | Parcial | UUID + 404 uniforme (contrato); PKs globais, oráculos | SR-012, 014, 020 |
| SEC-007 | Último Owner não removível | Parcial | Constraint trigger deferred; quebra transferência/convite sob RLS | SR-004 |
| SEC-010 | Argon2id, política de senha, senhas vazadas | Parcial | `hash_algorithm`; política (D-16) e checagem k-anonimato não especificadas; sem `maxLength` | SR-013 |
| SEC-011 | Access ≤15 min; refresh rotativo/uso único/reuso | Parcial | `refresh_token` com hash/`used_at`, `REUSE_DETECTED`; sem família/`replaced_by`; perfil JWT ausente | SR-003, 013 |
| SEC-012 | Tokens em Keychain/Keystore | N/A | Cliente | |
| SEC-013 | Lista/revogação de sessões; revogar ao trocar senha/e-mail | Coberto | `/me/sessions`, `/me/session-revocations`, `revoked_reason PASSWORD_CHANGED` | |
| SEC-014 | Reauth em exportar, excluir, trocar e-mail/senha, transferir | Parcial | AD-05; token sem escopo/uso único; export sem reauth no pedido; confirmação forjável no DB | SR-007, 013, 016 |
| SEC-015 | MFA/passkeys fora do MVP (backlog) | N/A | Registrado como fora do MVP; confirmar item de backlog | |
| SEC-016 | E-mail verificado antes de convites/export | Parcial | Cadastro só ativa após verificação; sem regra explícita no aceite/export | SR-004, 009 |
| SEC-017 | Recuperação de senha: token curto, resposta genérica, invalida sessões | Parcial | `recovery_request` com hash/expira; contrato diz "outras" sessões | SR-013 |
| SEC-020 | TLS 1.2+/HSTS/pinning | Parcial | Route edge com redirect; tráfego interno HTTP; sem HSTS | SR-018, 019 |
| SEC-021 | Criptografia em repouso (KMS), campos sensíveis | Lacuna | Nenhuma definição de volume/backup/KMS; sem ADR de cifra de campo | Shadow IT |
| SEC-022 | Banco local cifrado | N/A | Cliente | |
| SEC-023 | Segredos em cofre; secret scanning no CI | Parcial | Secret por referência; sem scanner; `.env.example` fraco; um Secret só | SR-017, 018 |
| SEC-024 | Recibos/chaves só no backend | Parcial | Desenho no contrato; `nina_app` insere `MANUAL_GRANT` | SR-003 |
| SEC-025 | Mídia futura: URL assinada, bucket privado | N/A | Fora do MVP | |
| SEC-030 | Auditoria append-only sem conteúdo | Parcial | Tabela e triggers; apagável e forjável | SR-005, 006 |
| SEC-031 | Campos de auditoria (ator, ação, request_id, IP hash) | Coberto | Colunas presentes; ajustar HMAC do `ip_hash` | SR-015 |
| SEC-032 | Acesso restrito e integridade (hash/WORM) | Parcial | Sem SELECT para o app (bom); cadeia sem âncora externa e só para críticos | SR-005 |
| SEC-033 | Acesso de suporte just-in-time, sem acesso padrão a produção | Lacuna | Existe `actor_type='SUPPORT'`, sem papel, fluxo JIT nem política de acesso a produção | Shadow IT |
| SEC-040 | Rate limit IP/conta/dispositivo | Parcial | 429 em 22/70 operações; nada implementado; Route sem limites | SR-013, 018, 019 |
| SEC-041 | Credential stuffing (lockout, senhas vazadas, CAPTCHA, aviso de login) | Lacuna | Sem contador de falhas/tabela de lockout, sem notificação de novo login no contrato | SR-013 |
| SEC-042 | Limites em sync | Parcial | 100 mutações/256 KiB; sem quota por bebê; LWW por relógio do cliente | SR-014 |
| SEC-043 | Tamanho de texto, validação estrita de esquema | Parcial | `additionalProperties: false` em dados de sync/bebê; faltam `maxLength` em auth e inconsistências com o DB | SR-013, 021 |
| SEC-050 | Respostas/tempos uniformes em login/cadastro/recuperação | Parcial | Mensagens uniformes no contrato; tempo (hash fictício) não especificado | SR-019 |
| SEC-051 | Convites não revelam existência de conta | Coberto | Contrato (`createInvitation`, `inspect` com 404 uniforme); `ALREADY_MEMBER` limitado ao Owner | SR-020 |
| SEC-052 | IDs não enumeráveis, sem busca de usuários | Coberto | UUIDs; sem endpoint de busca no contrato | SR-014 |
| SEC-053 | Erros genéricos ao cliente | Parcial | `Problem` sem PII no contrato; `RAISE` do DB com UUIDs, sem camada de mapeamento ainda | SR-019, 020 |
| SEC-060 | SCA, SAST, DAST, imagens mínimas, SBOM | Parcial | SCA (NuGetAudit + `dotnet list`); sem SAST/DAST/SBOM/scan de imagem | SR-017 |
| SEC-061 | Logs/crash sem PII | Lacuna | Sem política nem código de redação; nada testado | SR-019 |
| SEC-062 | Ambientes não produtivos com dados sintéticos | Parcial | Sem dados reais no repositório; falta política escrita e controle (Shadow IT) | SR-017, 018 |
| SEC-063 | Resposta a incidentes e ANPD | Lacuna | Sem runbook (SRE-001/DJ-10 pendentes) | Shadow IT |
| SEC-064 | Backups cifrados, restore reaplica exclusões, RPO/RTO | Lacuna | Nada definido; sem ledger de exclusões | SR-011, Shadow IT |
| SEC-065 | Headers de segurança, CORS, CSRF | Lacuna | BFF sem middleware algum | SR-019 |
| SEC-066 | Disclaimers de saúde | Coberto | `disclaimer_key` no contrato | |
| SEC-067 | Upload futuro | N/A | Fora do MVP | |

**Totais:** Coberto 5 (SEC-013, 031, 051, 052, 066) · Parcial 24 · Lacuna 7 (SEC-021, 033, 041, 061, 063, 064, 065) · N/A 5 (SEC-012, 015, 022, 025, 067).

Lacunas de requisitos que a especificação ainda **não tem** (recomendação ao security/privacy owner): proteção de dumps/exports e de logs de acesso do Postgres; política de rotação de `pepper` de IP e de chaves JWT; requisito para o armazenamento de arquivos de exportação (cifra, bucket privado, ACL); requisito de verificação de integridade da cadeia de auditoria; requisito de gestão de papéis de banco (um login por papel).

---

## 7. Riscos de "Shadow IT" (ADR-0006)

ADR-0006 registra "Shadow IT" (infraestrutura fora da governança corporativa de TI) e manda levar o risco a este review. Todos os itens abaixo são **riscos de governança que nenhum artefato do repositório mitiga hoje**; o que muda com Shadow IT é quem consegue ler/alterar o banco, os segredos e os backups sem trilha.

| # | Risco | Impacto específico no Nina | Requisito / DJ | Controle mínimo antes de dados reais |
|---|---|---|---|---|
| S1 | **Sem responsável nomeado** (RACI) pela plataforma, correção de CVE e plantão | Achados deste review sem dono em produção; incidente sem comando | SEC-063 | Responsável e substituto nomeados, SLA de patch para CVE crítica |
| S2 | **Local/região desconhecidos** e provedor sem DPA | Transferência internacional sem garantias (DJ-04); controlador/operador indefinidos (DJ-08) | DJ-04, DJ-08, R6 | Região Brasil ou mecanismo adequado documentado; DPA assinado; ROPA |
| S3 | **Administradores do cluster/PostgreSQL leem Secrets, etcd, volumes e backups** | Acesso humano irrestrito a dados C3 e hashes (sem JIT/justificativa) | SEC-033, STRIDE "E — abuso de suporte" | Lista nominal de administradores, MFA, acesso JIT com justificativa, logs de `oc`/SQL imutáveis |
| S4 | **Backup/RPO-RTO inexistente** e sem teste de restore; restore pode **ressuscitar dados excluídos** | Perda de dados de crianças; violação de exclusão (art. 18, VI) | SEC-064, §4 | Backup cifrado com chave fora do cluster, ciclo 35 dias, teste trimestral de restore com reaplicação do `erasure_ledger` (SR-011) |
| S5 | **Cifra em repouso e gestão de chaves** indefinidas (StorageClass do cluster, KMS) | Disco/volume em claro; `display_name`/`notes` expostos a quem tem o volume | SEC-021 | Volume cifrado comprovado + ADR de cifra de campo (nome, nascimento, notas) |
| S6 | **Gestão de segredos** ad hoc (`oc create secret --from-literal` no README; segredo único) | Credencial única do banco, sem rotação; vazamento por histórico de shell | SEC-023 | Cofre (Vault/External Secrets), credenciais separadas por papel, rotação, secret scanning |
| S7 | **Rede**: sem NetworkPolicy, Route sem rate limit/HSTS, Postgres possivelmente no mesmo cluster | Movimento lateral a partir de qualquer pod/projeto vizinho | SEC-020, SEC-040 | NetworkPolicy default-deny, projeto dedicado, quota, PostgreSQL gerenciado ou namespace isolado com política |
| S8 | **Monitoramento, logs e retenção** sem dono | Sem detecção de abuso; logs de acesso (Marco Civil, 6 meses) podem não existir ou conter PII | SEC-061, SEC-063, F7 | Pipeline de logs central com redação, alertas (auth, 429, exclusões em massa), retenção 6 meses |
| S9 | **Supply chain sem governança**: registry indefinido, imagens sem assinatura/scan | Imagem adulterada em produção com acesso ao banco | SEC-060 | Registry corporativo/privado, digest + assinatura, scan e SBOM no pipeline |
| S10 | **Offboarding e continuidade** (fator ônibus): contas pessoais com acesso ao ambiente | Ex-colaborador mantém acesso; indisponibilidade por perda de acesso | SEC-033 | Inventário de acessos, revisão trimestral, contas nominais |
| S11 | **Cópias não controladas** de dados (dump para depuração, restore em notebook) | Dados reais fora do perímetro; viola SEC-062 | SEC-062 | Proibição escrita, dados sintéticos, máscara em dumps |
| S12 | **Evidência de conformidade** (ANPD, RIPD, Privacy Gate) sem responsável técnico | Impossível provar medidas em incidente ou fiscalização | DJ-03, DJ-10, art. 48 | Encarregado nomeado; evidências arquivadas por release |

Gate proposto para "dados reais": S1, S2, S3, S4, S5 e S6 fechados e verificados; S7 e S9 fechados até o primeiro deploy; S8, S10, S11 e S12 até o Privacy Gate. Como o ADR-0006 diz que a pendência "entra no escopo de SECURITY-REVIEW-001, SRE-001 e PRIV-001", este documento a registra como **bloqueante para dados reais** (parte do parecer da seção 1.1).

---

## 8. Controles verificados que funcionam (manter e proteger com testes)

Provado nos testes ou por leitura:

- Sem contexto (`nina.user_id` ausente) a RLS é **fail-closed**: 0 bebês visíveis; GUC inválido (`'abc'`) provoca erro, não vazamento.
- READ_ONLY **não** escreve evento no bebê onde é membro (`new row violates row-level security policy`).
- `nina_app` **não** executa `erase_baby`, `erase_user`, `scrub_user_personal_data`, `fulfill_privacy_erasure`, `close_privacy_request`, `purge_*` (permission denied) e não tem DELETE em tabelas sincronizáveis nem acesso a `baby_sync_head`/`config_change`/`audit_event` (leitura).
- UPDATE e DELETE em `audit_event` por `nina_app` falham; TRUNCATE bloqueado por trigger.
- Janela de arrependimento **imposta pelo banco**: `account_deletion_guard` recalcula `grace_days` e `scheduled_for` e rejeita execução antes do prazo, ignorando valores do cliente (prova D1).
- Funções `SECURITY DEFINER` fixam `search_path = nina, pg_temp` (sem sequestro de função); `current_user_id()` é fail-closed.
- `sync_stamp_*` impedem ressuscitar tombstone e alterar `id`/`baby_id`; `sync_mutation` só insere com `user_id` do contexto.
- Pods: não-root, UID arbitrário, FS somente leitura, `drop: ALL`, seccomp `RuntimeDefault`, sem token de ServiceAccount, Secret por referência, `ASPNETCORE_ENVIRONMENT=Production`.
- `NuGetAudit` com `TreatWarningsAsErrors`; CI com `permissions: contents: read`; `.gitignore` exclui `.env`; nenhum segredo real encontrado no repositório nem em `git log -S`.

---

## 9. Plano de remediação por agente

| Prioridade | Agente | Itens |
|---|---|---|
| P0 (bloqueiam release) | database-engineer | SR-001, SR-002 (juntos com SR-004), SR-003, SR-004, SR-005, SR-006 |
| P0 | cloud-backend-engineer | SR-018 (compose sem superusuário e bootstrap de papéis para que os testes de RLS sejam representativos), Shadow IT S1-S6 |
| P1 (antes do primeiro endpoint de negócio) | dotnet-backend-engineer | SR-008 (DbTx/pool), SR-019, SR-014 (implementação), SR-020, perfil JWT (SR-013) |
| P1 | api-contract-engineer | SR-013 (contrato), SR-015 (`sex`), SR-016, SR-021; **antes de gerar SDK** (PA-13) |
| P1 | database-engineer | SR-007, SR-008, SR-009, SR-010, SR-011, SR-012, SR-014 (PKs), SR-015 (`ip_hash`/retenção) |
| P2 | cloud-backend-engineer | SR-017, SR-022, Shadow IT S7-S12 |
| Contínuo | todos | Converter o anexo A em testes automatizados no CI (PostgreSQL 16, papéis `nina_app`/`nina_worker`/`nina_config_admin` como logins sem BYPASSRLS) |

Observações para a próxima revisão (não auditado nesta rodada): `backend/src/Nina.SharedKernel/**`, `backend/src/Modules/Nina.Identity/{Crypto,Services,External,Mail,Contracts}/**`, `backend/spikes/**`, `android/`, `ios/` e as novas dependências (`Npgsql`, `JwtBearer`, `Konscious.Security.Cryptography.Argon2`). Pontos a olhar nelas: parâmetros Argon2id (memória/iterações), comparação de tokens em tempo constante, validação de `id_token` (assinatura, `aud`, `iss`, `exp`, `nonce`), `DbTx` (SET LOCAL/pool), `RateLimiter` (chave por IP atrás de proxy), logs sem PII.

---

## Anexo A — Provas executadas (resumo reprodutível)

Pré-requisitos: PostgreSQL 16, `0001_init.sql` aplicada; logins `app_login ∈ nina_app`, `worker_login ∈ nina_worker`, `cfg_login ∈ nina_config_admin`; contexto por `select set_config('nina.user_id', '<uuid>', true)` na transação.

| ID | Papel | Comando (essência) | Resultado observado | Achado |
|---|---|---|---|---|
| T0 | app (Carol READ_ONLY) | `INSERT INTO nina.sleep_session ... baby_id=<A>` | `violates row-level security policy` (correto) | controle OK |
| Z0/Z1 | app | `SELECT count(*) FROM nina.baby` sem GUC / com `'abc'` | 0 / erro de cast (fail-closed) | controle OK |
| N1 | app (Dave) | `SELECT email FROM app_user; SELECT password_hash FROM user_credential; SELECT * FROM family` | todas as linhas de todos os tenants | SR-003 |
| N3 | app (Dave) | `UPDATE family SET owner_user_id=<Dave> WHERE id=<fB>; SELECT * FROM baby` | `UPDATE 1`; perfil do bebê do Bob visível | SR-001 |
| B1/B2 | app (Owner A, depois Bob) | `UPDATE baby SET family_id=<fB>`; Bob lê o bebê de A | aceito; Bob lê | SR-001 |
| N4 | app (Dave) | `UPDATE user_credential SET password_hash=... WHERE user_id=<Bob>` | `UPDATE 1` | SR-003 |
| N5 | app (Dave) | `INSERT family_entitlement(MANUAL_GRANT)` + `family_entitlement_member` | `user_plan_code(Dave)='premium'` | SR-003 |
| N6 | app | `SELECT aggregate_id FROM outbox_message` | UUIDs de bebês de outros tenants | SR-003 |
| T1 | app (Carol) | `UPDATE caregiver_membership SET role='CAREGIVER' WHERE user_id=<Carol>` + insert de sono | UPDATE ok; `COMMIT` falha com NN010 (bug SR-004) | SR-002/004 |
| E1-E3 | app (Carol), banco com `check_baby_has_owner` definer | autopromoção; revogar+reativar; `SET baby_id=<B>` | todos aceitos; Carol lê o bebê e as notas do Bob | SR-002 |
| T2 | app (Carol) | `UPDATE ... SET status='REVOKED', revoked_reason='LEFT'` | ok (por acidente: bebê fica invisível) | SR-004 |
| T3/T3b | app (Owner A) | demote-first / promote-first | `UPDATE 0` + NN010 / `duplicate key membership_one_owner_uq` | SR-004 |
| I1 | app (Bob) | localizar/aceitar convite `PENDING` por `sha256(token)` | 0 linhas / `UPDATE 0` | SR-004 |
| M1 | app (Owner Bob) | `INSERT caregiver_membership(user_id=<Dave>, status='ACTIVE', accepted_at=now())` | aceito | SR-009 |
| F1 | app (Alice, REVOKED, dona da família) | `SELECT display_name, birth_date FROM baby` | perfil legível | SR-010 |
| P1/P2 | app / worker | `open_privacy_request('ANONYMIZATION')`; `fulfill_privacy_erasure(id)` | verificação autodeclarada; `app_user.status='DELETED'` na hora | SR-007 |
| AU1/AU2 | app (Dave) | `INSERT audit_event(actor_type='ADMIN', actor_user_id=<Alice>, is_critical)`; metadata com PII aninhada | aceitos e encadeados | SR-006 |
| AU3 | app | `DELETE/UPDATE audit_event` | permission denied (correto) | controle OK |
| AU4 | worker | `SET LOCAL nina.retention_purge='on'; DELETE FROM audit_event WHERE is_critical` | `DELETE 18` | SR-005 |
| G2 | app (Dave CAREGIVER) | `set_config('nina.authorship_scrub','on'); set_config('nina.scrub_user','<Bob>'); UPDATE sleep_session SET created_by=created_by` | `created_by` de Bob anulado, sem log | SR-008 |
| G3 | config_admin | `SET LOCAL nina.migration='on'; UPDATE app_parameter` | aceito, `actor_user_id` nulo | SR-008 |
| G4 | config_admin | `INSERT audit_event ... 'privacy.erasure_completed'` | aceito | SR-006 |
| O1/O2 | app | PK de `sleep_session`/`sync_mutation` já usada por outro tenant | `duplicate key` (oráculo/queima de ID) | SR-014 |
| O3 | app | `user_plan_code(<Bob>)`, `is_active_baby_owner(<Bob>)` | respondem para UUID arbitrário | SR-012 |
| O4 | app | token de push já ligado a outro usuário | `duplicate key` e `ON CONFLICT` bloqueado por RLS (troca de aparelho/usuário impossível) | SR-012/021 |
| O5 | app | `consent_record` com `subject_baby_id` de bebê alheio | aceito | SR-012 (sem vínculo validado) |
| D1 | app (Owner) | `INSERT account_deletion_request(grace_days=1, status='COMPLETED', confirmed_at=now())` | trigger força SCHEDULED/7 dias; confirmação aceita sem prova | SR-016 / controle OK |
| X1 | app | função criada depois da migração | executável (herda `PUBLIC`) | SR-012 |
| X2 | login em `nina_app`+`nina_worker` | `SELECT count(*) FROM baby` sem GUC | todas as linhas | SR-004/012 |
| R1 | worker | `erase_baby(<bebê>)` e consulta a `data_export_request` | exports `READY` com `file_ref` intactos | SR-011 |

## Anexo B — Testes de regressão exigidos (mínimo)

1. Para cada tabela do schema `nina`: `relrowsecurity` verdadeiro ou exceção listada e aprovada (SR-001/003).
2. Matriz `role × table × operation` comparada com snapshot aprovado (SR-003/012).
3. Cada coluna e cada transição de `caregiver_membership` por não-Owner falha, exceto via funções definer (SR-002/004/009).
4. Transferência de propriedade, aceitar/recusar convite e sair do bebê funcionam como `nina_app` (SR-004).
5. `DELETE` em `audit_event`/`consent_record` por qualquer papel falha fora de `purge_*` com piso de idade (SR-005); `INSERT` direto negado (SR-006).
6. Nenhum login pertence a mais de um dos papéis `nina_app`/`nina_worker`/`nina_config_admin`; papéis NOSUPERUSER/NOBYPASSRLS (SR-012).
7. `open_privacy_request` sem verificação falha; `fulfill_privacy_erasure` antes de `scheduled_for` falha (SR-007).
8. `erase_baby` invalida exportações que contenham o bebê (SR-011).
9. Suíte de IDOR/BOLA por endpoint (privacy-spec §10) e matriz RBAC com negativos, executada contra o banco real como `nina_app`.
10. Teste de contrato↔DDL (enums, tamanhos, estados) (SR-021).
