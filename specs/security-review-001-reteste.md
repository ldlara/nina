# SECURITY-REVIEW-001 — RETESTE independente (pós-remediação)

Status: **concluído (somente leitura; nada foi corrigido, commitado ou publicado)** · Data: 2026-10-08 · Revisor: security-reviewer
Commit revisado: `cff32df` (HEAD; remediação em `72e94c0`, ajustes de CI em `dbc5835`). Árvore de trabalho limpa antes e depois (`git status` vazio).
Documento original: `specs/security-review-001.md` (SR-001..SR-022, Anexo A de provas, Anexo B).

Escopo: `backend/db/migrations/0001_init.sql` (3009 linhas), `backend/tests/Nina.Database.Tests`, módulos Identity e SharedKernel, `Nina.Bff`, `docker-compose.yml`, `infra/*`, `.github/workflows/ci.yml`, `contracts/openapi.yaml` v1.0.1 e `specs/api-spec.md`.

---

## 1. Parecer

**O release continua BLOQUEADO.** A camada de banco ficou sólida: todas as provas críticas do Anexo A que eram exploráveis foram refeitas por mim contra a migração atual e **falham agora** (zero crítico). Mas o reteste encontrou **2 achados ALTOS novos** (NR-01, NR-02) e 10 MÉDIOS que o autor não viu nem testou. O critério do Privacy Gate (`privacy-security-spec.md` §10: "sem achados críticos/altos abertos") **não está atendido**.

| Pergunta | Resposta |
|---|---|
| Críticos abertos? | 0 (SR-001 e SR-002 estão corrigidos; reproduzi os ataques). |
| Altos abertos? | 2: **NR-01** (personificação por `nina.user_id`: o RLS não protege contra SQL arbitrário) e **NR-02** (o BFF não resolve o IP do cliente; todos os limites por IP viram globais, um cliente anônimo derruba login, cadastro e recuperação para todos). |
| Dá para continuar a Onda 3 com dados sintéticos? | Sim. |
| Dá para usar dados reais / beta? | Não, até fechar NR-01 e NR-02, decidir formalmente os itens PARCIAIS de privacidade (SR-013/016/015) e fechar Shadow IT S1-S6 (ADR-0006, ainda aberto em `docs/project/status.md`). |

### 1.1 O que falta para reabrir a decisão (ordem de execução)

1. **NR-02** (BFF/forwarded headers + limiter compartilhado) com teste ponta a ponta BFF->API sem injetar IP. Pequeno e barato; faça primeiro.
2. **NR-01**: implementar contexto assinado (`nina.user_id` + MAC verificado por `current_user_id()`) **ou** formalizar o risco por escrito (dono nomeado, data), mantendo SQL 100% parametrizado (hoje está) e a regra de CI que proíbe SQL dinâmico. O autor já registrou a lacuna como Q19 (`specs/database-spec.md`), mas a decisão não foi tomada. Sem uma das duas, o isolamento por RLS só vale contra bugs de filtro, não contra injeção.
3. **NR-03, NR-07, NR-08, NR-10, NR-11** (mudam a promessa de privacidade/segurança já escrita no contrato 1.0.1) e as decisões V2-01..V2-05 de `api-spec.md` §8.4. Como **não existe cliente publicado**, o argumento "quebra exige /v2" não se aplica: levantar o congelamento agora é mais barato que manter dívida em /v1.
4. NR-04, NR-05, NR-06, NR-09, NR-12 (hardening do banco e do Identity; cada um tem correção de poucas linhas).
5. Conferência final: repetir as provas da seção 4 e as novas da seção 5 como **testes automatizados como `nina_app`** (hoje nenhuma delas está na suíte) e rodar a CI.

---

## 2. Método e limites

- PostgreSQL **16.15**, cluster descartável criado com `initdb` em diretório temporário, **removido ao final** (processo parado, diretório apagado). Banco `nina` com `0001_init.sql` aplicada com `ON_ERROR_STOP` (sem erro), dono = superusuário (como no `docker-compose.yml`). Repeti a aplicação com dono **não superusuário e sem CREATEROLE** (papéis `nina_*` pré-existentes): funciona; papéis ficam NOSUPERUSER/NOBYPASSRLS, `TEMP` revogado de `PUBLIC`, nenhuma função executável por `PUBLIC`, dono não é membro de `nina_*`.
- Logins de teste, cada um membro de **um** papel e sem superuser nem BYPASSRLS: `app_login` (`nina_app`), `worker_login` (`nina_worker`), `cfg_login` (`nina_config_admin`). Contexto aplicado como a aplicação faz (`set_config('nina.user_id', ..., true)` dentro da transação). Massa: Alice (Owner A), Bob (Owner B), Carol (READ_ONLY em A), Dave (CAREGIVER em B), Eve (sem vínculo; atacante).
- Não confiei nos testes do autor: refiz cada prova do Anexo A à mão (seção 4) e acrescentei ataques novos (seção 5). Depois, em **cópia** do repositório (para não escrever `bin/obj` na árvore), rodei a suíte inteira: **325 testes passam** (114 banco, 181 Identity, 26 BFF, 4 API).
- Verificado de fato: SHAs das 6 ações do GitHub Actions contra `git ls-remote` (todos conferem com a tag citada), SHA-256 do `gitleaks 8.30.1` (confere). BFF real executado com uma API falsa para provar NR-02.
- **Não verificado**: Docker/compose (sem daemon), OpenShift, execução do workflow no GitHub, digests das imagens (`postgres:16@sha256:...`, `sdk`/`aspnet`), PostgreSQL 15, PgBouncer em modo transação, desempenho sob volume, restore de backup.

---

## 3. Status de SR-001..SR-022

Legenda: CORRIGIDO = a prova original falha e não achei contorno; PARCIAL = parte corrigida, parte aberta ou só em papel; ABERTO = não corrigido. Resultado: **12 CORRIGIDO, 10 PARCIAL, 0 ABERTO.**

| ID | Sev. original | Status | Evidência (o que reproduzi / li) |
|---|---|---|---|
| SR-001 | CRÍTICO | **CORRIGIDO** | `nina.family` agora tem RLS (`:2789-2791`, SELECT/INSERT só do titular) e `family_owner_guard` (`:375-383`) impede mudar `owner_user_id`. `baby.app_select` (`:2794-2798`) não depende mais de ser dono da família. `family_id` fora do GRANT de coluna (`:2869`) e protegido por `baby_family_guard` (`:422-430`). Reproduzi N3/B1/B2: Eve não lê nenhuma família nem bebê, `UPDATE family` e `UPDATE baby SET family_id` dão `permission denied`; `INSERT family(owner=Bob)` viola RLS. |
| SR-002 | CRÍTICO | **CORRIGIDO** | O app não tem UPDATE/DELETE em `caregiver_membership` (`:2870`, sem política). `membership_guard` (`:473-513`) exige a ficha 'membership'/'ownership' e define a máquina de estados (REVOKED/DECLINED terminais; `baby_id`/`user_id` imutáveis). Carol: autopromoção, troca de `baby_id`, reativação e DELETE => `permission denied`; INSERT de vínculo ACTIVE/OWNER como Eve/Carol viola RLS. Tentei forjar fichas por GUC (`nina.guard.*`, `nina.migration`, `nina.retention_purge`): sem efeito (o app nem tem EXECUTE em `guard_token/guard_arm/guard_ok` nem leitura de `guard_secret`). |
| SR-003 | ALTO | **CORRIGIDO** (resíduo grave tratado em NR-01) | RLS em todas as tabelas de tenant (único catálogo sem RLS: `plan`, `feature_flag`, `plan_feature`, `app_parameter`, `consent_purpose`, só leitura). Reproduzi N1/N4/N5/N6: Eve vê só a própria linha em `app_user`, 0 credenciais, 0 famílias; `UPDATE user_credential` de Bob => 0 linhas; `INSERT family_entitlement` e `SELECT outbox_message` => `permission denied`. Persistem as funções `auth_lookup_*` por chave de busca (residual aceito pelo autor, Q21). |
| SR-004 | ALTO | **CORRIGIDO** | `check_baby_has_owner` é DEFINER (`:532-545`); existem `accept_invitation`, `decline_invitation`, `leave_baby`, `remove_member`, `set_member_role`, `transfer_ownership` (`:2270-2429`). Executei como `nina_app`: aceite por destinatário correto (ACCEPTED, consentimento gravado), por destinatário errado (NOT_FOUND uniforme, contador sobe), transferência atômica (ex-Owner vira CAREGIVER, `family_id` acompanha), sair do bebê. IDOR: Eve, Carol e Bob chamando `remove_member/set_member_role/transfer_ownership/leave_baby` com ids alheios recebem o mesmo erro uniforme. |
| SR-005 | ALTO | **CORRIGIDO** (resíduos: âncora WORM ausente; NR-04) | Ninguém tem DELETE em `audit_event`/`consent_record`; `forbid_mutation` só libera DELETE com a ficha `purge_*` emitida por função DEFINER com piso de idade (`:2176-2246`). Worker: `SET LOCAL nina.retention_purge='on'; DELETE FROM audit_event` => `permission denied`; `purge_audit('1 day')` => `PURGE_BELOW_FLOOR`; `verify_audit_chain()` ok. Cadeia agora inclui DENIED e não-USER (`:1325`). Sem âncora externa, truncar o fim da cadeia continua indetectável (infra/CLOUD-001). |
| SR-006 | ALTO | **CORRIGIDO** | `INSERT` direto em `audit_event` negado para app, worker e config_admin. `nina.audit()` força `actor=nina.user_id`, valida ação/resultado/chaves contra `audit_action` e PII profunda (`:1370-1408`): ação de outro, ação PRIVILEGED, chave `email` e valor `alice@ex.org` rejeitados. Resíduos: valores livres das chaves permitidas (ex.: nome em texto corrido passa) e `audit_auth_attempt` registra falha em nome de qualquer conta (aceito pelo autor). |
| SR-007 | MÉDIO | **CORRIGIDO** (ver NR-03) | `open_privacy_request` sem DEFAULT, exige `reauth_bind` (`:1933-1963`); ANONYMIZATION nasce SCHEDULED com `scheduled_for`; `fulfill_privacy_erasure` => `DELETION_GRACE_NOT_ELAPSED`; Owner ativo => `OWNER_MUST_USE_ACCOUNT_DELETION`. Reproduzi. A "prova" de reautenticação, porém, é auto-emitível (NR-03). |
| SR-008 | MÉDIO | **PARCIAL** | Fichas por transação substituíram os GUCs de autoria, migração e purga (testado: `set_config('nina.authorship_scrub'...)` e fichas forjadas não fazem nada; `nina.migration` não evita o NN040). `DbTx.SetUserAsync` é parametrizado e local (`DbTx.cs:20-25`). **Item 4 (contexto assinado) não foi feito**: `nina.user_id` continua definível por qualquer SQL (NR-01). `nina.device_id` segue GUC livre (só afeta `change_log.device_id`). |
| SR-009 | MÉDIO | **CORRIGIDO** | INSERT do Owner limitado a PENDING/CAREGIVER|READ_ONLY, expiração <= 30 dias, `invited_by` = ator (`:2808-2817`); `invite_used_at`, tentativas e limite diário por trigger (`:517-528`). Reproduzi M1: ACTIVE, OWNER e 90 dias violam RLS; e-mail não normalizado falha no CHECK. Observação: o Owner ainda pode criar convite PENDING com `user_id` de qualquer usuário existente (convite não solicitado; FK também serve de oráculo de existência de UUID). Baixo. |
| SR-010 | MÉDIO | **CORRIGIDO** | Reproduzi F1: depois da transferência e da remoção de Alice, ela vê 0 bebês e 0 eventos; `family_id` se moveu com a propriedade. |
| SR-011 | MÉDIO | **PARCIAL** | `erase_baby` invalida exportações (`baby_ids`) e enfileira `ExportFileInvalidated` (reproduzi R1); purgas de auditoria/consentimento existem; `change_log.actor_user_id` é anulado no scrub; `erasure_ledger` + `reapply_erasure_ledger` existem. Abertos: (a) **a exclusão de bebê feita pelo Owner pelo caminho normal (UPDATE de `deleted_at`) não passa por `erase_baby`** (NR-07); (b) não há consumidor do outbox para apagar arquivo/foto; (c) `privacy_request`, `account_deletion_request`, `app_user` anonimizado sem purga (Q22); (d) nenhuma das purgas está agendada (worker inexistente). |
| SR-012 | MÉDIO | **CORRIGIDO** | `ALTER DEFAULT PRIVILEGES` global (`:56-58`) + `REVOKE TEMP` + atributos reafirmados. Reproduzi X1: função/tabela/sequência criadas depois da migração não são executáveis/legíveis pelo app; `app_login` não consegue criar objeto, tabela temporária, nem `SET ROLE`. `user_plan_code`, `is_active_baby_owner`, `user_has_feature` => `permission denied` (O3). Resíduo: `baby_ever_had_owner(uuid)` continua executável e serve de oráculo de existência de bebê (NR-13). X2 (login em dois papéis vê todos os tenants) é inerente ao PostgreSQL; mitigado por teste de catálogo e por um papel = um login no compose/bootstrap. |
| SR-013 | MÉDIO | **PARCIAL** | Contrato: 429 em **70/70** operações (verifiquei por script), `maxLength` nos campos de entrada (verifiquei por script: os campos string restantes sem `maxLength` são, em sua maioria, de resposta), `additionalProperties:false` nos requests, `oneOf` no `ReauthRequest`, perfil JWT (ES256, `typ`, `kid`, `iss/aud`, `sid`, `jti`), reset invalida todas as sessões, código de 6-12 dígitos. Implementação do JWT é boa (seção 6). **Abertos**: (1) `scope` do reauth opcional: sem `scope` o token vale para qualquer operação sensível (`AuthService.cs:618-620`, `TokenService.cs:30`); (2) `POST /me/data-exports` com `X-Reauth-Token` **opcional** (`ReauthTokenOptional`), contra privacy-spec 5.1; (3) tentativas do código de e-mail contadas em memória (NR-10), não na tabela persistente que existe; (4) breached-password check é lista local de 28 senhas (`PasswordPolicy.cs:15`), não k-anonimato. Itens 1 e 2 estão em V2-01/V2-02 e são decisão pendente, não limitação técnica. |
| SR-014 | MÉDIO | **PARCIAL** | Banco: PK `(baby_id,id)` e `mutation_id` por `(user,device,mutation_id)` (`:640-655`). Reproduzi O1/O2: Alice insere `sleep_session` com o id de um evento do Bob e `sync_mutation` com o mutation_id do Bob sem colisão. Contrato: `client_created_at` só informativo, ordem do servidor, `CLIENT_CLOCK_SKEW` só aviso. Abertos: cotas (`sync_max_*`), cursor assinado e validação cruzada `entity_id`/`baby_id` são só texto de contrato; o módulo de sync não existe. |
| SR-015 | MÉDIO | **PARCIAL** | `ip_hash` = HMAC com chave derivada do `Security:MasterKey` (`AuditLog.cs:41`; não é reversível por força bruta); `user_agent` nunca é gravado; `approx_location` reservado/null; sessões purgadas aos 30 dias. **`baby.sex` continua coletado** (coluna e contrato, "deprecated"), contra privacy-spec 1.3 (V2-03; decisão jurídica DJ-01 pendente). |
| SR-016 | MÉDIO | **PARCIAL** | `request_account_deletion/confirm_account_deletion` ligam o pedido ao `reauth_jti`; app não escreve `confirmed_at` (reproduzi D1: UPDATE de `confirmed_at` => `permission denied`); `erase_user` envia `SharedBabyDeletedNotice` a cada cuidador ativo **na execução**. Abertos: (a) a prova de reauth é auto-emitível (NR-03); (b) `DELETE /babies/{id}` continua imediato, sem função, sem aviso, sem reauth no banco (NR-07); (c) o pedido de exclusão pode ser criado sem reauth e não gera aviso no pedido (NR-14). |
| SR-017 | MÉDIO | **PARCIAL** | Corrigido e verificado: ações por SHA (todos conferem), gitleaks com SHA-256 (conferi), CodeQL `security-extended`, SBOM CycloneDX (código e imagens), Trivy HIGH/CRITICAL, imagens por digest, `script de setup` com `mktemp -d` + SHA-256, `permissions: contents: read`. **Faltam**: `.github/dependabot.yml` (o comentário do `ci.yml` diz que existe), `packages.lock.json` / `nuget.config` com `packageSourceMapping`, lint/validação do OpenAPI na CI (api-spec §9 recomenda), CODEOWNERS, assinatura/atestado de imagem; o teste de vulnerabilidade ainda depende do formato do `dotnet list` (`grep "^\s+>"`). Os testes de banco rodam na CI dentro de `dotnet test Nina.sln` (falham alto se o PostgreSQL não existir; não pulam). |
| SR-018 | MÉDIO | **PARCIAL** | Compose corrigido: dono/migrator separado (`nina_owner`), API conecta como `nina_app`, bootstrap aplica a migração e dá LOGIN a cada papel (um login por papel), sem porta do banco por padrão (profile `dbhost` em 127.0.0.1), senhas obrigatórias (`:?`), mount só de `./backend`, binds em 127.0.0.1. OpenShift: NetworkPolicy deny-all + allow explícito, Route com HSTS/rate-limit/timeout, `imagePullPolicy: Always`, Secret por papel. **Abertos**: BFF->API em HTTP sem mTLS nem token de serviço (README admite); sem `ASPNETCORE_FORWARDEDHEADERS_ENABLED`/opções no BFF (NR-02); manifestos não referenciam `Security__MasterKey`, `Jwt__SigningKeyPem`, client ids (o pod não sobe em Production); egress da API bloqueado para JWKS Google/Apple e SMTP (NR-16). Nada disso pôde ser exercitado sem cluster. |
| SR-019 | MÉDIO | **PARCIAL** | Existem: JWT ES256 com checagem de sessão a cada requisição, RFC 7807 sem vazar SQL, cabeçalhos de segurança, limite de corpo 256 KiB, rate limiter, HSTS na Route. Faltam/erradas: resolução do IP (NR-02), limiter compartilhado (NR-10), autorização por bebê/papel (não há endpoint de negócio ainda; a matriz de IDOR do Anexo B-9 não existe), política de log sem PII (não há teste). |
| SR-020 | BAIXO | **PARCIAL** | `/ready` da API não lista módulos (`HealthEndpoints.cs`). **O BFF mantém `/ready` público** (503 `api-unavailable`, `Nina.Bff/Program.cs`) e a Route expõe todas as rotas; o 400 de CHECK devolve `ex.ConstraintName` como `field` (`ProblemDetailsMiddleware.cs:44`), vazando nomes internos de constraint. `created_by` exposto e 404 x 403 são aceitos (PA-09). |
| SR-021 | BAIXO | **CORRIGIDO** | `ContractDdlTests` compara enums/limites contrato x DDL; `sex` aceita `OTHER`; `register_push_token` trata troca de dono do aparelho (conflito se o dono anterior ainda tem sessão ativa); `consent_record.subject_baby_id` agora exige vínculo (reproduzi O5: violação de RLS). |
| SR-022 | BAIXO | **CORRIGIDO** | `ENTRYPOINT ["sh","-c","exec dotnet \"$PROJECT_DLL\""]` (PID 1 = dotnet), `USER 1654:0`, `HEALTHCHECK` por `/dev/tcp`. |

---

## 4. Anexo A reproduzido (contra a migração atual)

| ID | Resultado hoje | Veredito |
|---|---|---|
| T0 | Carol (READ_ONLY) inserindo sono em A: `violates row-level security` | controle OK |
| Z0/Z1 | sem contexto 0 linhas; GUC `'abc'` => erro de cast | controle OK |
| N1 | Eve: `app_user` 1 linha (a dela), `user_credential` 0, `family` 0 | CORRIGIDO |
| N3 | `UPDATE family` => `permission denied`; `SELECT baby` => 0 | CORRIGIDO |
| B1/B2 | `UPDATE baby SET family_id` => `permission denied`; `family_id` também protegido por trigger | CORRIGIDO |
| N4 | `UPDATE user_credential` de Bob por Eve => `UPDATE 0` | CORRIGIDO |
| N5 | `INSERT family_entitlement` => `permission denied` | CORRIGIDO |
| N6 | `SELECT outbox_message` => `permission denied`; `change_log` 0 linhas | CORRIGIDO |
| T1/E1-E3 | Carol: UPDATE/DELETE em `caregiver_membership` => `permission denied`; INSERT próprio ACTIVE/OWNER => RLS | CORRIGIDO |
| T2 | `leave_baby` funciona e é auditado | CORRIGIDO |
| T3/T3b | `transfer_ownership` atômico sob `nina_app` | CORRIGIDO |
| I1 | aceite por token do destinatário certo ok; errado => NOT_FOUND | CORRIGIDO |
| M1 | Owner inserindo ACTIVE/OWNER/90 dias => RLS | CORRIGIDO |
| F1 | ex-Owner removido: 0 bebês, 0 eventos | CORRIGIDO |
| P1/P2 | `open_privacy_request` sem prova => `IDENTITY_NOT_VERIFIED`; `fulfill` antes de `scheduled_for` => NN008 | CORRIGIDO (ver NR-03) |
| AU1/AU2/G4 | INSERT direto negado; `nina.audit` com chave/valor de PII rejeitados | CORRIGIDO |
| AU3 | UPDATE/DELETE/TRUNCATE em auditoria e consentimento => `permission denied` | controle OK |
| AU4 | worker + `retention_purge='on'` + DELETE => `permission denied`; purga só por função com piso | CORRIGIDO |
| G2 | scrub de autoria por GUC: `permission denied` (coluna fora do GRANT) e ficha forjada ignorada | CORRIGIDO |
| G3 | `nina.migration='on'` não dispensa o ator (NN040) | CORRIGIDO |
| O1/O2 | ids de outro tenant não colidem | CORRIGIDO |
| O3 | `user_plan_code/is_active_baby_owner/user_has_feature` => `permission denied` | CORRIGIDO |
| O4/O5 | push token por função; consentimento de bebê alheio => RLS | CORRIGIDO |
| D1 | `confirmed_at` forjado => `permission denied`; janela de 7 dias imposta | CORRIGIDO |
| X1 | função/tabela/sequência nova não executável pelo app | CORRIGIDO |
| X2 | login em `nina_app`+`nina_worker` ainda vê tudo | inerente; mitigado por teste de catálogo e infra |
| R1 | `erase_baby` => exportação EXPIRED, `file_ref` nulo, 1 evento de outbox | CORRIGIDO (ver NR-07) |

---

## 5. Novos achados

Ordem: severidade, depois ordem de correção sugerida. Todas as provas foram executadas como `app_login`/`worker_login`/`cfg_login` sobre a migração atual.

### NR-01 — Personificação por `nina.user_id`: o RLS não protege contra SQL arbitrário; `auth_lookup_user_by_email` entrega o UUID

- **Severidade:** ALTO
- **Local:** `0001_init.sql:74-76` (`current_user_id()` confia no GUC), `:2448-2454` (`auth_lookup_user_by_email` devolve `id` e `password_hash` para **qualquer** e-mail), `:2906-2909` (EXECUTE para `nina_app`). Reconhecido pelo autor em `database-spec.md` Q19/Q21 como "exige decisão", não decidido.
- **Exploração (executada):** como Eve (sem vínculo nenhum) numa única transação:
  `select set_config('nina.user_id', (select id::text from nina.auth_lookup_user_by_email('alice@ex.org')), true);` e depois `select display_name, birth_date from nina.baby;` => retorna `BebeAlice`/data de nascimento; `select notes from nina.sleep_session` => "nota alice". Com o mesmo contexto, `email_code_issue`/`email_code_verify`/`email_change_create` (a prova de reauth é auto-emitível, NR-03) levam à troca do e-mail da conta e à tomada total. Basta saber o e-mail da vítima.
- **Por que importa:** o modelo de ameaça do relatório original (§1.4) inclui `nina.user_id` "legítimo **ou forjado**". As correções de SR-001..SR-004 fecham o atalho do tenant cruzado, mas qualquer SQL injetado no app continua podendo se passar por qualquer usuário cujo e-mail se conheça. Hoje todo SQL do repositório é parametrizado (revisei `IdentityStore.cs`, `DbTx.cs`), o que reduz a probabilidade, não o impacto.
- **Correção:** (a) contexto assinado: a API chama `nina.set_context(uid, exp, mac)` (DEFINER) com HMAC cujo segredo só o banco e a API têm; `current_user_id()` só confia em contexto validado na transação (ficha por xid, como as demais); o atacante com SQL injetado não tem o segredo. (b) Separar o pré-auth em papel `nina_auth` (Q21) e tirar `id`/`password_hash` do retorno que o login não precisa. (c) Se não for feito agora: aceite formal por escrito com dono nomeado e regra de CI contra SQL não parametrizado.
- **Agente:** database-engineer (função + `current_user_id`), dotnet-backend-engineer (`DbTx`, MAC por transação), cloud-backend-engineer (segredo).

### NR-02 — O BFF não resolve o IP do cliente: todos os limites por IP viram globais (DoS anônimo) e `ip_hash` perde sentido

- **Severidade:** ALTO
- **Local:** `Nina.Bff/Program.cs:33` (`app.UseForwardedHeaders()` sem opções; padrão do ASP.NET é `ForwardedHeaders.None`), `ApiForwarder.cs:44-47` (reenvia `RemoteIpAddress` como `X-Forwarded-For`), `infra/openshift/configmap.yaml` e `nina-bff.yaml` (nenhum `ASPNETCORE_FORWARDEDHEADERS_ENABLED`), `AuthService.cs:40` (`Ip`), `:46/:215/:232/:361/:466` (chaves `register:ip`, `login:ip`, `refresh:ip`, `forgot:ip`). A API confia no `X-Forwarded-For` de quem alcançar a porta (`SharedKernelExtensions.cs:45-51`), o que só é seguro por causa da NetworkPolicy.
- **Prova (executada):** subi o `Nina.Bff.dll` real com uma API falsa e enviei `POST /v1/auth/login` com `X-Forwarded-For: 198.51.100.9` e `X-Forwarded-Proto: https`. A API recebeu `X-Forwarded-For: 127.0.0.1` (o IP do socket). Atrás da Route do OpenShift esse valor é o IP do roteador para **todos** os clientes. O teste do autor (`IdentityProxyTests.cs:64-95`) injeta o IP por `FixedIpFilter` e por isso não percebe.
- **Impacto:** `login:ip` (30 falhas/15 min), `register:ip` (20/h), `forgot:ip` (10/h), `verify:ip`, `reset:ip` e `refresh:ip` (120/min) passam a ser compartilhados por toda a base. **Um único cliente anônimo** com 30 logins falhos em 15 min impede o login de todos; com 20 cadastros/h trava o cadastro; com 10 pedidos/h trava a recuperação de senha; o refresh de usuários legítimos estoura em carga normal. O `ip_hash` da auditoria passa a ser o mesmo para todos (inutiliza forense e SEC-061).
- **Correção:** configurar `ForwardedHeadersOptions` no BFF (`ForwardedHeaders = XForwardedFor | XForwardedProto`, `ForwardLimit = 1`, `KnownIPNetworks` = rede do ingress/roteador) ou ler o último valor do `X-Forwarded-For` do HAProxy; manter a API só aceitando o XFF do BFF (NetworkPolicy + token de serviço). Teste ponta a ponta BFF->API com `X-Forwarded-For` real e peer não loopback. Resolver junto NR-10 (limiter compartilhado).
- **Agente:** dotnet-backend-engineer (código e teste), cloud-backend-engineer (manifestos, CIDR do router).

### NR-03 — O livro-razão de reautenticação é auto-emitível: a "prova no banco" de SR-007/SR-016 não prova nada contra SQL arbitrário

- **Severidade:** MÉDIO
- **Local:** `0001_init.sql:2470-2480` (`consume_reauth_jti` aceita `p_jti_hash`, `p_scope` e `p_expires` do chamador), `:2256-2266` (`reauth_bind` só confere que a linha existe, é do usuário e do escopo), `:2910` (EXECUTE para `nina_app`).
- **Exploração (executada):** Alice, sem senha nem token: `select nina.consume_reauth_jti(sha256('forged'),'OWNERSHIP_TRANSFER',null,now()+interval '2 min');` (`t`) seguido de `select nina.transfer_ownership(<A>, <membership da Carol>, sha256('forged'));` => propriedade transferida. Executei o mesmo para `ACCOUNT_DELETE` (`request_account_deletion(sha256('jj'))`, seguido de `erase_user` após a janela) e `PRIVACY_REQUEST` (`open_privacy_request('ANONYMIZATION',...)`); a mecânica é idêntica para `ACCOUNT_EMAIL_CHANGE` (não executado). O `reauth_bind` consegue vincular o jti uma única vez, então o uso único vale, mas o jti é inventado.
- **Correção:** a função de ledger só pode registrar um jti acompanhado de assinatura que o banco verifique (HMAC com segredo guardado no banco, igual `guard_secret`), cobrindo `user|scope|jti|exp`; ou o Identity escreve o ledger por um papel/login próprio (`nina_auth`) e o `nina_app` só **consome** (`reauth_bind`). Falhar se `p_expires` > 5 min.
- **Agente:** database-engineer; dotnet-backend-engineer.

### NR-04 — Locks consultivos previsíveis e papéis sem timeouts: DoS global da cadeia de auditoria e starvation silenciosa da retenção

- **Severidade:** MÉDIO
- **Local:** `0001_init.sql:1326` (`pg_advisory_xact_lock(hashtextextended('nina.audit_chain',0))`, mantido até o fim da transação por todo evento crítico/DENIED/não-USER), `:2065/:2117` (`pg_try_advisory_xact_lock` que **retorna vazio sem erro**), `:2190/:2230`; nenhum `ALTER ROLE ... SET statement_timeout/lock_timeout/idle_in_transaction_session_timeout` (os três valem 0).
- **Exploração (executada):** (a) Eve: `select pg_advisory_lock(hashtextextended('nina.audit_chain',0))` (qualquer papel pode chamar; a chave é determinística). Em seguida Alice chamando `nina.audit('auth.password_changed')` ficou bloqueada até o `statement_timeout` que eu impus; sem timeout, indefinidamente. Vale também para qualquer pedido de exclusão, transferência, convite aceito, etc. (todos geram evento crítico). (b) Eve segura `hashtextextended('nina.purge_expired_operational_data',0)` e o worker recebe `{}` sem erro: **a retenção para de rodar e ninguém é avisado**. (c) Sem precisar de SQL arbitrário: uma única requisição lenta (ex.: chamada externa depois de um evento crítico na mesma transação) serializa o sistema inteiro.
- **Correção:** `ALTER ROLE nina_app/nina_worker SET statement_timeout, lock_timeout, idle_in_transaction_session_timeout` (valores baixos para app); tirar o lock global do caminho da requisição (cadeia calculada em lote pelo worker ou lock em linha de tabela privada, que o app não consegue segurar); purgas devem falhar alto quando não obtêm o lock (e alertar).
- **Agente:** database-engineer; cloud-backend-engineer (settings de papel).

### NR-05 — Gatilhos `SECURITY DEFINER` rodam **antes** do `WITH CHECK` do RLS: oráculo entre tenants

- **Severidade:** MÉDIO (condicional: política `sleep.overlap_policy=REJECT` ligada e conhecimento do UUID do bebê)
- **Local:** `0001_init.sql:949-966` (`sleep_overlap_guard`, DEFINER, BEFORE INSERT), `:973-986` (`wake_event_session_guard`), `:660-667` (`sync_stamp_child`, NN003 para bebê excluído).
- **Exploração (executada):** com a política em `REJECT` (valor suportado pelo ADR-0009), Eve inseriu `sleep_session` com `baby_id` de Bob e intervalo sobreposto ao sono dele: erro `SLEEP_OVERLAP` (NN006). Com intervalo livre ou bebê inexistente: `violates row-level security`. Varredura de intervalos reconstrói a rotina de sono da criança. Com a política padrão `ACCEPT_AND_WARN` o vazamento não ocorre; por leitura de código (não executado), o mesmo desenho vaza "bebê excluído" (NN003) em `sync_stamp_child` e a existência do par (`baby_id`,`sleep_session_id`) em `wake_event_session_guard`.
- **Correção:** gatilhos que leem tabelas de tenant devem ser `SECURITY INVOKER` (o RLS do chamador já cobre) ou começar com `IF NOT nina.can_write_baby(NEW.baby_id) THEN RAISE insufficient_privilege`. Teste de regressão: INSERT com `baby_id` alheio nunca retorna erro diferente de `42501`.
- **Agente:** database-engineer.

### NR-06 — Autoria forjável: `created_by`/`last_modified_by` vêm do cliente

- **Severidade:** MÉDIO
- **Local:** `0001_init.sql:2874-2882` (INSERT total e `UPDATE(last_modified_by)` nas tabelas sincronizáveis), `:2869` (idem em `baby`); `sync_stamp_child` (`:660-692`) e `sync_stamp_baby` não sobrescrevem esses campos; `sync_log_change` copia `last_modified_by` para `change_log.actor_user_id`.
- **Exploração (executada):** Dave (CAREGIVER de B) inseriu um sono com `created_by = Bob` e `last_modified_by = Bob`, e atualizou `last_modified_by = Bob` num evento seu; `change_log.actor_user_id` ficou Bob. A suíte do autor só prova que `UPDATE created_by` falha, não o INSERT nem `last_modified_by`.
- **Impacto:** quebra de não-repúdio justamente no cenário de disputa de guarda (DJ-09); polui a trilha "registrado por".
- **Correção:** forçar `NEW.created_by := nina.current_user_id()` no INSERT e `NEW.last_modified_by := nina.current_user_id()` em INSERT/UPDATE dentro dos gatilhos `sync_stamp_*` (exceto sob a ficha de scrub); remover `last_modified_by` do GRANT de UPDATE.
- **Agente:** database-engineer.

### NR-07 — Exclusão de bebê pelo Owner é um `UPDATE deleted_at` direto: sem `erase_baby`, sem reauth, sem aviso, sem auditoria

- **Severidade:** MÉDIO (regressão de SR-011/SR-016; contradiz o contrato 1.0.1)
- **Local:** `0001_init.sql:2869` (`deleted_at` no GRANT de `baby`), teste do autor `SyncSchemaTests.cs:335-343` consagra o caminho; `database-spec.md` Q20 admite "apaga na hora, sem janela nem aviso".
- **Exploração (executada):** Alice (Owner) `UPDATE baby SET deleted_at=now(), display_name=NULL, birth_date=NULL ...` => aceito. Depois: `sleep_session` do bebê continua legível (1 linha), os 3 vínculos continuam ACTIVE, nenhuma linha em `audit_event`, nenhum `erasure_ledger`, nenhuma invalidação de exportações, nenhum evento de outbox. `readable_babies()` não exclui bebês deletados. Os dados só somem na purga de tombstone (>= 90 dias). O contrato 1.0.1 diz que `DELETE /babies/{id}` exige reauth `BABY_DELETE` e notifica todos os cuidadores; o banco não tem como impor nem um nem outro. O app não pode chamar `erase_baby` (só o worker).
- **Correção:** função `nina.delete_baby(p_baby, p_reauth_jti_hash)` DEFINER (Owner ativo, `reauth_bind` escopo `BABY_DELETE`, outbox para notificar cuidadores, chama o apagamento interno), tirar `deleted_at` do GRANT, e fazer `readable_babies()` ignorar bebê com `deleted_at`.
- **Agente:** database-engineer; api-contract-engineer (decisão V2-05: janela de arrependimento para bebê compartilhado).

### NR-08 — Pré-sequestro invertido no cadastro: quem se cadastra depois **substitui a senha** da conta ainda não verificada

- **Severidade:** MÉDIO
- **Local:** `AuthService.cs:125-146` (`RegisterCoreAsync`: para e-mail existente e não verificado faz `UpsertCredentialAsync` com a senha nova, invalida o código e emite outro).
- **Cenário:** a vítima V se cadastra com `v@x` e senha pV (ainda não confirmou). Passados 60 s, o atacante A cadastra `v@x` com senha pA: a credencial vira pA e um novo código é enviado à caixa de V. Se V digitar o código mais recente (o único válido), a conta é confirmada **com a senha do atacante**, V recebe sessão e A entra com pA. A proteção contra o pré-sequestro clássico (comentário "anti pré-sequestro") abriu o caso inverso.
- **Correção:** não alterar a credencial de conta não verificada; guardar a senha pendente junto do código (hash do código <-> hash da senha) e só aplicá-la na verificação do código que a originou, ou exigir a senha na própria verificação.
- **Agente:** dotnet-backend-engineer.

### NR-09 — Autoverificação de e-mail: o app pode escrever `email_verified_at` (e as funções de código do banco não são usadas)

- **Severidade:** MÉDIO
- **Local:** `0001_init.sql:2855-2856` (INSERT livre e `UPDATE(email_verified_at)` em `app_user`); `IdentityStore.MarkEmailVerifiedAsync`/`InsertUserAsync(verifiedAt)` usam esse caminho; `email_code_issue/verify` e `email_change_*` existem, estão testadas e **nenhum código as chama** (`database-spec.md`: "migração do Identity ... não feita").
- **Exploração (executada):** Eve: `UPDATE app_user SET email_verified_at = now() WHERE id = <ela>` => `UPDATE 1`; `INSERT app_user(... email='ceo@empresa.com', email_verified_at=now())` => aceito (conta pré-verificada com e-mail de terceiro). `email_verified_at` é o portão do login e do aceite de convite (SEC-016).
- **Correção:** migrar o Identity para `email_code_verify` (contador persistente de tentativas, atômico, uma função) e revogar `UPDATE(email_verified_at)`; registro/login social por função DEFINER (`register_user`) que decide `email_verified_at`.
- **Agente:** dotnet-backend-engineer + database-engineer.

### NR-10 — Limiter e contadores de tentativa em memória: por instância, perdidos no restart, `Clear()` fail-open

- **Severidade:** MÉDIO
- **Local:** `RateLimiter.cs:35,139-160` (ao chegar a 200 mil chaves faz `_buckets.Clear()`), `AuthService.cs:166-203` (tentativas do código de verificação contadas em `limiter` e não no banco), `:215-233` (chaves por e-mail digitado: um 429 já cria buckets, sem custo de hash). Duas réplicas dobram todos os limites; o `deviceKey` usa um UUID escolhido pelo cliente (rotacionar zera o limite por dispositivo).
- **Impacto:** o código de 8 dígitos (10^8 combinações) com 5 tentativas por instância e janela de 15 min continua inviável de forçar mesmo com reinícios, então o risco prático é moderado; mas o desenho contraria AD-34/SEC-041 (contador persistente já existe em `email_verification_code` e não é usado), os limites dobram com 2 réplicas e a falha é silenciosa. Combinado com NR-02, o limiter hoje protege pouco.
- **Correção:** store compartilhado (Redis ou tabela com UPSERT atômico) para as chaves de tentativa; usar `email_code_verify` (NR-09); `Clear()` deve descartar só chaves expiradas.
- **Agente:** dotnet-backend-engineer; cloud-backend-engineer (Redis/recurso).

### NR-11 — Outbox forjável pelo app

- **Severidade:** MÉDIO (depende do consumidor, que ainda não existe)
- **Local:** `0001_init.sql:2821` (`WITH CHECK (true)`), `:2888` (INSERT em todas as colunas, incluindo `processed_at`, `attempts`, `available_at`, `event_key`).
- **Exploração (executada):** Eve inseriu eventos `SharedBabyDeletedNotice`, `ExportFileInvalidated` e `BabyDeleted` com `aggregate_id` = UUID de Alice, um já marcado `processed_at`, e outro com `event_key` arbitrário (pode ocupar chaves que o sistema usará, forçando `UNIQUE` a falhar). O worker futuro trataria esses eventos como verdadeiros: aviso de exclusão falso a cuidadores, apagar arquivo de exportação alheio.
- **Correção:** o app só insere por `nina.enqueue(event_type, ...)` com lista de tipos permitidos e agregado validado contra o contexto, ou GRANT de colunas restrito (`aggregate_type, aggregate_id, event_type, payload`) com `CHECK` por tipo; `event_key` só do sistema.
- **Agente:** database-engineer.

### NR-12 — Oráculo de tempo em `POST /auth/password/forgot`

- **Severidade:** MÉDIO (latente: o mailer atual é um fake em memória)
- **Local:** `AuthService.cs:464-491`. Conta existente: `InsertRecoveryAsync` + auditoria + envio síncrono do e-mail; conta inexistente: retorna cedo. Com SMTP real a diferença é de centenas de milissegundos. O cadastro (`Register`) não sofre o mesmo, pois os dois ramos enviam e-mail.
- **Correção:** enfileirar o e-mail por outbox/fila e igualar o trabalho dos dois ramos (hash dummy ou escrita fictícia); teste estatístico de tempo em ambiente com provedor real.
- **Agente:** dotnet-backend-engineer.

### NR-13 — Oráculos e vazamentos residuais (BAIXO)

- `baby_ever_had_owner(uuid)` continua com EXECUTE para o app (`:2903`): revela se um UUID de bebê existe com Owner. `baby.id` é PK global: `INSERT baby` com id alheio dá `duplicate key` (confirmei). Exigem conhecer o UUID.
- `ProblemDetailsMiddleware.cs:44` devolve o nome da constraint em `errors[].field`.
- `auth_consume_recovery`, `email_code_verify`, `email_change_confirm` recebem `p_now` do chamador (a expiração pode ser dobrada por quem tem SQL e o hash).
- Convite PENDING pode ser criado direto para um `user_id` existente (convite não solicitado; FK serve de oráculo de existência).
- **Agente:** database-engineer; dotnet-backend-engineer.

### NR-14 — Pedido de exclusão de conta aceito sem reauth e sem aviso na solicitação (BAIXO)

- `request_account_deletion(p_reauth_jti_hash DEFAULT NULL)` (`:2599-2613`) cria o pedido sem prova; só gera auditoria. Uma sessão roubada agenda a exclusão da conta e do(s) bebê(s) sem cuidadores, sem e-mail ao titular (nenhum outbox no pedido). Reversível por 7 dias; a API precisa exigir `ACCOUNT_DELETE` e avisar.
- **Correção:** tornar o parâmetro obrigatório quando houver bebê sem outro cuidador; outbox `AccountDeletionRequested` no pedido.
- **Agente:** database-engineer; dotnet-backend-engineer.

### NR-15 — Retenção: pisos e parâmetros (BAIXO)

- `purge_audit` compara `interval` com `make_interval(months=>12)` = 360 dias (semântica de intervalo do PostgreSQL): eventos comuns podem sair aos 361 dias (`:2179-2184`).
- `validate_app_parameter` não valida `sync.*_retention_days`, `prediction.retention_days`, `push.token_inactivity_days`: um valor 0 ou negativo faz `purge_expired_sync_data` apagar tudo e expirar o cursor de todos (ato do `nina_config_admin`, que também escolhe o ator via GUC `nina.user_id`).
- **Agente:** database-engineer.

### NR-16 — Manifestos e runtime: lacunas que levarão a atalhos (BAIXO)

- `nina-api.yaml` só referencia `nina-db-app`; faltam Secrets para `Security__MasterKey`, `Jwt__SigningKeyPem`, `Identity__GoogleClientIds/AppleClientIds` (o pod falha fechado em Production, ótimo, mas os manifestos não sobem). A NetworkPolicy não libera egress da API para o JWKS do Google/Apple nem para SMTP: haverá pressão para abrir `0.0.0.0/0`. Defina um proxy de saída ou `EgressFirewall` com lista de hosts.
- `MigrationHostedService` (`SharedKernelExtensions.cs:76-92`) aplica migrações com credencial de dono dentro do pod da API se `Database:ApplyMigrations=true`; recusar a flag fora de Development e usar Job separado.
- **Agente:** cloud-backend-engineer; dotnet-backend-engineer.

### NR-17 — `ValidateSessionAsync` falha aberto sem `ISessionValidator` (BAIXO)

- `NinaAuthentication.cs:64`: se um host registrar a autenticação e esquecer o validador, a revogação de sessão deixa de valer sem erro. Hoje a API registra o validador no Identity (ok); tornar obrigatório (falhar na subida) e cobrir com teste.
- **Agente:** dotnet-backend-engineer.

---

## 6. Revisão do código Identity

**Pontos fortes confirmados (manter):** JWT ES256 fixo, `typ` (`at+jwt`/`reauth+jwt`), `kid` obrigatório sem tentativa de todas as chaves, `iss/aud/exp/nbf`, vida <= 900 s e `jti` obrigatórios, tolerância <= 60 s (`TokenValidation.cs`, `NinaAuthentication.cs`); chaves ausentes fora de Development impedem a subida; sessão checada no banco a cada requisição (revogação imediata) sob RLS; refresh opaco de 256 bits, só o SHA-256 guardado, rotação com `FOR UPDATE`, detecção de reuso revoga a sessão e apaga push tokens, `device_id` conferido; reauth com `sub`, `sid`, escopo opcional, `jti` de uso único consumido atomicamente no banco; Argon2id (19 MiB, t=2, p=1) com parâmetros lidos do hash, rehash, concorrência limitada e hash descartável para igualar o tempo (login, reauth); respostas uniformes em login/cadastro/verificação; comparação em tempo constante onde há segredo; HMACs com chave derivada por finalidade; reset de senha de 256 bits, 30 min, uso único, revoga todas as sessões; SQL 100% parametrizado; logs sem PII (só tipo de exceção); `X-Request-Id` saneado; BFF com lista fechada de rotas e de cabeçalhos, descarta `Cookie` e o `X-Forwarded-For` do cliente.

**Fragilidades (já listadas acima):** NR-02, NR-08, NR-09, NR-10, NR-12, NR-17; reauth sem escopo e reauth opcional no export (SR-013); verificador OIDC com `nonce` escolhido pelo cliente e sem armazenamento no servidor (um `id_token` capturado vale até o fim da sua validade, com o mesmo `nonce`, para login e reauth), JWKS com `HttpClient` sem timeout curto segurando o lock (latência de login social se o Google/Apple degradar); lista de senhas vazadas local de 28 entradas; `text_hash` de consentimento é `sha256(finalidade|versão)` e não do texto publicado.

---

## 7. Infra e CI (resumo)

Corrigido e verificado: compose sem superusuário na aplicação, bootstrap de papéis, porta do banco fechada, mounts mínimos, hardening de contêiner; NetworkPolicy e Route; Dockerfile; SHAs de ações; gitleaks; CodeQL; SBOM; Trivy; script de setup com checksum. Em aberto: SR-017/SR-018 (seção 3), NR-02, NR-16, Shadow IT S1-S12 (ADR-0006) sem responsável nomeado em `docs/project/status.md`.

---

## 8. Riscos residuais — proposta de aceite (cada um exige dono e data; nenhum foi aceito formalmente)

| # | Risco | Condição para aceitar |
|---|---|---|
| RA-1 | Funções `auth_lookup_*` (pré-auth) devolvem a linha da conta por e-mail/hash a quem executa SQL como `nina_app` (Q21) | Só depois de NR-01 (contexto assinado); sem isso, é a porta de NR-01. |
| RA-2 | `audit_auth_attempt` registra **falha** em nome de qualquer conta | Aceitável: não forja sucesso, exclusão nem evento crítico. |
| RA-3 | Sem `FORCE ROW LEVEL SECURITY` | Aceitável enquanto o dono não é membro de `nina_*` e o teste de catálogo roda na CI. |
| RA-4 | Cadeia de auditoria sem âncora externa (truncar o fim é indetectável) | Até CLOUD-001; obrigatório antes de dado real. |
| RA-5 | Um único `Security:MasterKey` para todos os HMAC (sem rotação planejada) | Definir procedimento de rotação antes de produção. |
| RA-6 | Worker comprometido apaga qualquer bebê (`erase_baby` sem pedido) e escolhe `p_actor` | Aceitável como poder do worker; exige credencial separada e monitoramento. |
| RA-7 | Bloqueio de login por conta (20 falhas/15 min) permite DoS direcionado | Aceitável (trade-off SEC-041) se houver aviso e recuperação. |
| RA-8 | `baby.id` global (oráculo de existência com UUID conhecido) | Aceitável: UUIDs não enumeráveis. |
| RA-9 | PA-09 (404 x 403 `ACCESS_REVOKED`) e `created_by` exposto | Decisão de produto registrada. |

---

## 9. Qualidade dos testes do autor

A suíte de banco (114 testes) é boa: roda como logins sem privilégio, usa banco novo por teste, tem testes de catálogo (matriz de privilégios, função por função, RLS em todas as tabelas, um login um papel, `search_path` fixo) e provou ser sensível a mutações. Lacunas encontradas por este reteste (nenhuma está coberta): INSERT com `created_by` alheio e `last_modified_by` (NR-06); `consume_reauth_jti` com escopo e jti inventados (NR-03); `set_config('nina.user_id')` + `auth_lookup_user_by_email` (NR-01); locks consultivos e timeouts (NR-04); erro diferente de `42501` em INSERT com `baby_id` alheio (NR-05); `email_verified_at` por UPDATE/INSERT (NR-09); outbox com tipo/colunas livres (NR-11); soft delete de bebê deixando dados e vínculos (NR-07). Nos testes de Identity e BFF: nenhum teste ponta a ponta BFF->API resolve o IP real (NR-02), nem de reinício/multi-instância do limiter (NR-10).

---

## 10. Plano de remediação por agente

| Prioridade | Agente | Itens |
|---|---|---|
| P0 (bloqueiam release) | dotnet-backend-engineer + cloud-backend-engineer | NR-02 |
| P0 | database-engineer + dotnet-backend-engineer | NR-01 (ou aceite formal) |
| P1 | database-engineer | NR-03, NR-07, NR-11, NR-06, NR-05, NR-04, NR-09 (lado banco), NR-14, NR-15 |
| P1 | dotnet-backend-engineer | NR-08, NR-09 (lado Identity), NR-10, NR-12, NR-17; escopo obrigatório no reauth (SR-013); reauth no export |
| P1 | api-contract-engineer | levantar o congelamento v1 enquanto não há cliente: V2-01, V2-02, V2-03 (`sex`), V2-05 |
| P2 | cloud-backend-engineer | NR-16, mTLS/token BFF<->API, dependabot, lockfile/`nuget.config`, lint OpenAPI na CI, CODEOWNERS, assinatura de imagem, Shadow IT S1-S12 |
| Contínuo | todos | Converter as provas das seções 4 e 5 em testes automatizados como `nina_app`/`nina_worker`/`nina_config_admin` |
