# Especificação de Privacidade e Segurança — Nina (PRIV-001)

Status: **rascunho para revisão jurídica**. Nenhuma base legal, prazo ou texto deste documento foi validado por advogado. Tudo marcado **[a validar pelo jurídico]** é proposta técnica, não parecer.

Fontes: `specs/discovery-napper-wonder-weeks.md` (§6.3 RNFs, §6.4 RBs, §11 analytics, §12 segurança/LGPD), `docs/project/specification.md` (escopo MVP: 45 RFs), `docs/project/architecture.md`.

Escopo: MVP (conta, bebê/cuidadores, sono/previsão, alimentação/fralda/pumping, notificações, assinatura, exportação/exclusão, offline/sync, analytics, sessões/auditoria). **Fora do MVP** (RIPD deve ser revisado antes de habilitar): diário, fotos/mídia, desenvolvimento/marcos, IA, comunidade, backup em nuvem de mídia, áudio.

---

## 1. Inventário de dados pessoais e classificação

### 1.1 Escala de classificação (proposta)

| Nível | Nome | Definição | Exemplos |
|---|---|---|---|
| C0 | Público | Sem titular identificável | Conteúdo editorial, tabelas de referência |
| C1 | Interno | Operacional, sem identificação direta | Métricas agregadas, flags de feature |
| C2 | Pessoal | Identifica ou torna identificável o adulto | E-mail, locale, timezone, IDs de dispositivo |
| C3 | Pessoal de criança / alto impacto | Dado de criança, saúde-adjacente ou comportamental da família | Nome, nascimento, sono, alimentação, fraldas |
| C4 | Crítico | Credenciais e segredos | Hash de senha, refresh tokens, tokens de push, chaves |

Tratamento de C3: o discovery (RNF-002) pede "alta sensibilidade operacional". **Se registros de sono/alimentação/fralda constituem "dado pessoal sensível referente à saúde" (art. 5º, II e art. 11 LGPD) é decisão jurídica pendente (DJ-01).** Até a decisão, a engenharia trata C3 como se fosse sensível (cautela máxima).

### 1.2 Dados do adulto (titular: cuidador)

| Dado | Entidade | Origem | Classe | Obrigatório | Destino/Operador |
|---|---|---|---|---|---|
| E-mail | User | Fornecido | C2 | Sim (login) | Postgres; provedor de e-mail transacional |
| Senha (hash Argon2id) | User | Fornecido | C4 | Sim (se login por senha) | Postgres |
| Identificador de provedor (Apple/Google), se SSO | User | Provedor | C2 | Não | Postgres |
| Locale, timezone | User | Dispositivo | C1/C2 | Sim | Postgres |
| Nome de exibição do cuidador | User | Fornecido | C2 | Não | Postgres; visível a outros cuidadores do bebê |
| Papel por bebê (Owner/Caregiver/ReadOnly) | CaregiverMembership | Sistema | C2 | Sim | Postgres |
| Convites (e-mail do convidado, token) | Invite | Fornecido | C2/C4 | Sim no fluxo | Postgres; e-mail |
| Token de push (APNs/FCM) | Device | Dispositivo | C2/C4 | Se push ativo | Postgres; APNs/FCM |
| Preferências de notificação, quiet hours | NotificationPreference | Fornecido | C2 | Não | Postgres |
| Sessões/dispositivos (id, user-agent, último uso) | Session | Sistema | C2 | Sim | Postgres |
| IP e metadados de requisição | Logs/Audit | Sistema | C2 | Sim (segurança) | Logs com retenção curta |
| Entitlement/assinatura (plano, loja, validade, id de transação) | Subscription | Loja | C2 | Se premium | Postgres; Apple/Google. **Dados de pagamento não são coletados/armazenados pela Nina** |
| Consentimentos (finalidade, versão, data, revogação) | ConsentRecord | Fornecido | C2 | Sim | Postgres |
| Eventos de auditoria | AuditEvent | Sistema | C2 | Sim | Postgres (append-only) |
| Pseudônimo de analytics | Analytics | Sistema | C1/C2 (pseudonimizado) | Condicionado a consentimento | Ferramenta de analytics **[a validar]** |

### 1.3 Dados da criança (titular: criança; exercício por responsável)

| Dado | Entidade | Classe | MVP | Observação |
|---|---|---|---|---|
| Nome/apelido do bebê (display_name) | Baby | C3 | Sim | Minimização: permitir apelido; não exigir nome civil |
| Data de nascimento | Baby | C3 | Sim | Necessária à previsão por idade |
| Data prevista do parto (opcional) | Baby | C3 | Sim | Armazenar separada (RB-004); relevante a prematuros; sugere condição de saúde |
| Timezone do bebê | Baby | C1/C3 | Sim | |
| Sexo/gênero | Baby | C3 | **Não coletar** | Não necessário ao MVP |
| Foto do bebê (photo_ref) | MediaAsset | C3 | **Fora do MVP** | Exige RIPD revisado e controles de upload |
| Sessões de sono (início, fim, tipo, fonte) | SleepSession | C3 | Sim | Saúde-adjacente (DJ-01) |
| Previsões de sono + confiança | SleepPrediction | C3 | Sim | Derivado; recalculável (RB-001) |
| Alimentação (tipo, lado, volume) | FeedingSession | C3 | Sim | Saúde-adjacente |
| Extração (pumping) | PumpingSession | C3 (também dado da mãe/lactante) | Sim | Dado de saúde do adulto potencialmente; avaliar no RIPD |
| Fraldas (tipo, horário) | DiaperEvent | C3 | Sim | Texto livre `notes` é risco: ver 1.4 |
| Notas em eventos (texto livre) | várias | C3 (pode conter qualquer coisa) | Sim se mantido | Recomenda-se limitar tamanho e avisar para não inserir dados de saúde sensíveis/terceiros; **excluir de analytics, logs e IA** |
| Tombstones de eventos excluídos | Sync | C3 (apenas ids/versão) | Sim | Sem conteúdo |
| Diário, marcos, sinais observados | DiaryEntry etc. | C3 | Fora do MVP | |
| Conversas de IA | AIConversation | C3 | Fora do MVP | |

### 1.4 Riscos específicos de classificação
- Texto livre (`notes`) pode revelar doenças, medicação, terceiros. Tratar como C3 e nunca registrar em log/analytics.
- Combinação de nascimento + sono + localização aproximada (timezone, IP) é identificável; manter separação lógica entre analytics e dados de conta.
- Dados de múltiplos cuidadores (nome, papel) são dados de terceiros adultos dentro do mesmo bebê.

---

## 2. Finalidades e bases legais propostas

> Todas as bases abaixo são **propostas** **[a validar pelo jurídico]**. Papel proposto: Nina como **controladora**; provedores cloud, push, e-mail, analytics e lojas como **operadores/suboperadores** ou controladores independentes (Apple/Google, para pagamento) — a definir (DJ-08).

| # | Finalidade | Dados | Base legal proposta (LGPD) | Obrigatória? | Consentimento separado? |
|---|---|---|---|---|---|
| F1 | Criar e autenticar conta | E-mail, credenciais, sessão | Execução de contrato (art. 7º, V) | Sim | Não (aceite de Termos) |
| F2 | Registro de rotina do bebê (sono, alimentação, fralda, pumping), timeline, gráficos | Dados C3 | **Pendente**: execução de contrato (art. 7º, V) vs. consentimento específico e em destaque do responsável (art. 14, §1º; art. 11, I) — **DJ-01/DJ-02** | Sim (núcleo do serviço) | **Recomendado sim**, cautela, até decisão |
| F3 | Previsões de sono (motor por regras) | Idade, histórico | Mesma base de F2 (é o serviço contratado) | Sim | Ver F2 |
| F4 | Compartilhamento entre cuidadores | Dados do bebê + papel | Execução de contrato + ação do Owner; confirmar se o convite exige aceite informado do convidado | Opcional (ação do usuário) | Não, mas com aviso claro no convite |
| F5 | Notificações operacionais (lembretes de soneca, convites) | Token push, preferências | Execução de contrato (art. 7º, V) / consentimento do sistema operacional para permissão de push | Opcional | Permissão do SO + toggle in-app |
| F6 | Assinatura e entitlement | Plano, transação | Execução de contrato; obrigação legal para registros fiscais (art. 7º, II) | Se premium | Não |
| F7 | Segurança, antifraude, auditoria, prevenção a abuso | IP, logs, AuditEvent | Legítimo interesse (art. 7º, IX) e cumprimento de obrigação legal/regulatória (art. 7º, II, e Marco Civil, registros de acesso) — **DJ-06** | Sim | Não; teste de balanceamento documentado |
| F8 | Analytics de produto essencial (sem PII, agregado, ex.: crash e contagens operacionais) | Eventos da taxonomia §11, pseudônimo | Legítimo interesse **ou** consentimento — **DJ-05** | Opcional | **Recomendado opt-in** até decisão |
| F9 | Analytics de produto não essencial / experimentação / marketing | Eventos, pseudônimo | Consentimento (art. 7º, I) | Opcional | **Sim, separado** |
| F10 | Comunicações de marketing | E-mail | Consentimento | Opcional | **Sim, separado** |
| F11 | Exercício de direitos do titular (DSAR) | Dados necessários à verificação | Cumprimento de obrigação legal (art. 7º, II; art. 18) | Sob demanda | Não |
| F12 | Defesa em processos / obrigação legal / retenção mínima pós-exclusão | Registros mínimos | Art. 7º, II, VI | Sob demanda | Não |
| F13 (futuro) | IA, diário/fotos, comunidade, pesquisa | Vários | Consentimento específico (RB-011) | Opcional | **Sim, um por finalidade**; fora do MVP |

Princípios aplicados (art. 6º): finalidade, adequação, necessidade (minimização), transparência, segurança, prevenção, não discriminação, responsabilização.

---

## 3. Consentimentos separados e versionados

### 3.1 Regras
1. Um registro por finalidade; sem "aceito tudo" agrupado. Termos de Uso/Política de Privacidade são **aceites** (documentos), distintos de **consentimentos** (finalidades opcionais).
2. Nenhuma caixa pré-marcada. Recusar consentimento opcional não pode bloquear o núcleo do serviço (F1–F7).
3. Cada registro é versionado e imutável; mudança material de finalidade/texto cria nova versão e exige novo consentimento.
4. Revogação tão fácil quanto a concessão (tela de Privacidade); efeito imediato nos clientes e no servidor (para analytics: parar de enviar e apagar o pseudônimo).
5. Evidência: quem, quando, versão do texto, hash do texto, idioma, plataforma/versão do app, método (UI). Sem armazenar mais dados do que necessário.
6. Consentimento do responsável pelo bebê: coleta separada e destacada (art. 14, §1º), com registro da declaração de que o usuário é pai/mãe/responsável legal — **DJ-02**.

### 3.2 Catálogo inicial de finalidades de consentimento

| purpose_key | Descrição | Padrão | Versão inicial | MVP |
|---|---|---|---|---|
| `terms_of_use` | Aceite dos Termos de Uso | obrigatório | 1.0.0 | Sim |
| `privacy_policy` | Ciência da Política de Privacidade | obrigatório | 1.0.0 | Sim |
| `child_data_guardian` | Declaração de responsável legal + tratamento de dados do bebê (art. 14) | obrigatório p/ criar bebê **[DJ-01/02]** | 1.0.0 | Sim |
| `analytics_product` | Analytics de produto | desligado | 1.0.0 | Sim (RF-048) |
| `marketing_email` | E-mails promocionais | desligado | 1.0.0 | Opcional |
| `push_notifications` | Registro de token e envio de push | permissão SO + toggle | 1.0.0 | Sim |
| `ai_assistant`, `diary_media`, `research`, `community` | Futuras | desligado | n/a | Não |

### 3.3 Modelo de dados (refina `ConsentRecord`)
`consent_id, user_id, subject_baby_id (nulo se do adulto), purpose_key, policy_version, text_hash, locale, status (granted|revoked|superseded), granted_at, revoked_at, source (onboarding|settings|prompt), app_version, platform`. Tabela append-only; estado corrente derivado do último registro por (user, baby, purpose). Cliente guarda cache e **o servidor decide** (alinha com RB-009).

### 3.4 Aceite de convite
Convidado (cuidador) precisa aceitar Termos/Política na própria conta. Ele não consente em nome do Owner; o Owner declara ser responsável legal. Situações de guarda compartilhada/separação de cuidadores: **DJ-09**.

---

## 4. Política de retenção proposta

Prazos são **propostas de engenharia [a validar pelo jurídico]** (DJ-06). Princípio: reter pelo menor tempo necessário; exclusão efetiva, não só lógica.

| Categoria | Retenção proposta (ativo) | Após exclusão/encerramento | Observações |
|---|---|---|---|
| Conta (User) | Enquanto ativa | Exclusão em até 30 dias; ver "janela de arrependimento" | Inatividade: aviso e política de inatividade (ex.: 24 meses) **[validar]** |
| Dados do bebê e eventos (C3) | Enquanto o perfil existir | Exclusão com o perfil; tombstones removidos após convergência (ex.: 90 dias) | RB-007: só Owner exclui perfil |
| Previsões (derivadas) | Recalculáveis; manter janela curta (ex.: 90 dias) | Exclusão com o perfil | RB-001 |
| Consentimentos | Enquanto a conta existir **+ prazo prescricional** (ex.: 5 anos) como prova **[validar]** | Mantido minimizado (ids, finalidade, versão, datas), sem dado de criança | F12 |
| Auditoria (AuditEvent) | 12 meses online; arquivo até 5 anos para eventos de segurança/DSAR **[validar]** | Pseudonimizar ator após exclusão; sem conteúdo de dados | Metadata_safe apenas |
| Logs de aplicação (sem PII) | 30 dias | N/A | RNF-008 |
| Registros de acesso (IP/horário) | 6 meses (referência Marco Civil, art. 15) **[validar]** | N/A | F7 |
| Sessões / refresh tokens | Expiração: 30 dias absolutos / rotação por uso; revogação imediata | Apagar na exclusão | |
| Tokens de push | Até logout/invalidação pelo provedor ou 60 dias de inatividade | Apagar | |
| Convites pendentes | Expiram em 7 dias | Apagar após expirar + 30 dias | |
| Dados de assinatura | Enquanto ativa + prazo fiscal/contábil (ex.: 5 anos) **[validar]** | Reter somente o mínimo fiscal | F6/F12 |
| Analytics | 14 meses (ou menos) agregados; evento bruto 90 dias **[validar]** | Apagar pseudônimo ao revogar | Sem PII |
| Backups | Ciclo de 35 dias | Dados excluídos desaparecem dos backups no ciclo; **restore deve reaplicar fila de exclusões** | RNF-012; documentar ao titular |
| Exportações DSAR geradas | 7 dias, link assinado de uso limitado | Apagar | |
| Mídia, diário, IA (futuro) | Definir no RIPD de cada módulo | | RB-011 |

Exclusão de conta: janela de arrependimento de 7 dias (ADR-0010; parâmetro privacy.deletion_grace_days) com conta desativada (opcional, **validar**), depois exclusão definitiva e job de purga com prova (AuditEvent sem dados). Bebês com múltiplos cuidadores: ver 5.4.

---

## 5. Fluxos DSAR (art. 18 LGPD)

SLA proposto: confirmação em até 48 h; atendimento em até 15 dias (prazo para resposta completa — **validar**, DJ-07). Canal: in-app (Configurações > Privacidade) + e-mail do encarregado (DPO) **[nomear]**.

### 5.1 Princípios comuns
- Autenticação reforçada (reautenticação recente, <5 min) antes de solicitar exportar/excluir; notificação por e-mail a cada pedido.
- Verificação de titular: o adulto autenticado exerce direitos próprios e, como responsável, os do bebê **sob sua guarda** (Owner). Pedido por terceiros (outro responsável, e-mail externo): fluxo manual com verificação reforçada (DJ-09).
- Toda solicitação gera `DsarRequest {id, user_id, tipo, status, created_at, due_at, closed_at}` e `AuditEvent`.
- Respostas sem revelar existência de conta a quem não for o titular (anti-enumeração).

### 5.2 Acesso e confirmação (art. 18, I e II)
Visualização in-app dos dados (perfil, bebês, cuidadores, consentimentos, sessões, assinatura). Resumo das finalidades, bases, operadores e retenção na Política de Privacidade; resposta específica a pedido sob demanda.

### 5.3 Correção (art. 18, III)
Edição direta in-app de dados da conta e do bebê (nome, nascimento, data prevista, timezone). Mudar nascimento/data prevista dispara recálculo (RB-003) mas **não** altera eventos. Correção de eventos pelo próprio fluxo de edição (versão + sync). Casos fora da UI via suporte, registrados.

### 5.4 Exportação / portabilidade (art. 18, V; RF-044)
- Formato: ZIP com JSON versionado (`schema_version`) + CSV opcional; timestamps em UTC com timezone (RB-014).
- Conteúdo: conta, bebês onde o solicitante é Owner (todos os eventos, previsões opcionalmente), consentimentos, preferências, histórico de DSAR. Para bebê com outros cuidadores: dados do bebê vão ao Owner; dados pessoais de **outros** cuidadores são limitados a nome de exibição e papel.
- Geração assíncrona (job idempotente), arquivo cifrado em repouso, link assinado de curta duração (ex.: 24 h, uso único), aviso por e-mail, sem anexar dados no e-mail.
- Cuidador não-Owner exporta apenas o que gerou? **Decisão de produto/jurídico pendente (DJ-09).**

### 5.5 Exclusão (art. 18, VI; RF-045)
Dois escopos:
1. **Excluir conta**: apaga User, sessões, tokens, consentimentos (mantendo mínimo probatório), vínculos. Bebês em que é único Owner: exige transferir propriedade (RB-007) ou excluir o bebê. Se há outros cuidadores e o Owner exclui a conta sem transferir: política a definir (transferir ao cuidador mais antigo vs. exclusão do bebê) — **DJ-09**.
2. **Excluir perfil do bebê** (apenas Owner): apaga todos os eventos, previsões, preferências do bebê; gera tombstone de sync e invalida caches (RB-008/RB-015).
- Fluxo: reautenticação → confirmação explícita com texto de consequências → janela de arrependimento (opcional) → job de purga (Postgres, filas, caches, storage, push tokens) → solicitar remoção a operadores (analytics, e-mail) → confirmação ao usuário → registro em AuditEvent.
- Clientes: ao receber 401/410 de exclusão, apagar banco local (SQLite/GRDB, Room) e chaves no Keychain/Keystore.
- Backups: ver §4.

### 5.6 Outros direitos
Oposição e revogação de consentimento (in-app, imediata), informação sobre compartilhamento (lista de operadores), revisão de decisões automatizadas (art. 20: previsões de sono são orientativas; documentar lógica por regras, sem decisão com efeito jurídico), anonimização/bloqueio sob demanda via suporte.

---

## 6. Regras de analytics sem PII (taxonomia §11)

### 6.1 Regras
1. **Allowlist rígida**: apenas os eventos e propriedades da tabela §11 do discovery. Qualquer novo evento/propriedade passa por revisão de privacidade (PR com `analytics-schema.json` versionado e teste de contrato).
2. **Proibido** em eventos: nome/apelido do bebê, e-mail, texto livre (notas, diário), fotos, conteúdo de conversa, datas exatas de nascimento, coordenadas, IP persistido, IDs de dispositivo de publicidade (IDFA/AAID), IDs internos reutilizáveis (user_id, baby_id).
3. **Buckets em vez de valores**: `age_bucket` (ex.: 0-3m, 4-6m...), `duration_bucket`, `delta_minutes_bucket`, `confidence_bucket`. Idade do bebê nunca exata. Buckets coarsos o suficiente para evitar reidentificação (k-anonimato mínimo a definir).
4. **Pseudônimo**: ID aleatório gerado no cliente (ou HMAC com chave rotacionável no servidor), não derivável de e-mail/ID de conta, resetável pelo usuário e apagado na revogação. **Nunca** cruzar com baby_id.
5. **Consentimento** (F8/F9, DJ-05): sem opt-in o cliente não inicializa o SDK / não envia. Crash reporting: sem PII, breadcrumbs sanitizados.
6. **Sanitização server-side**: gateway de ingestão rejeita propriedades fora do schema e padrões de PII (regex de e-mail, UUIDs de usuário).
7. **Terceiros**: preferir analytics first-party ou self-hosted; se SaaS, DPA, região, sem SDKs de publicidade, desativar coleta automática de IP/geolocalização/screen text. Transferência internacional: **DJ-04**.
8. **Propriedades de alto risco**: `content_id`/`track_id`/`activity_key` são chaves editoriais, não do usuário (ok). `store`, `plan`, `offer` ok. `topic_taxonomy` (IA) é fora do MVP e nunca texto livre.
9. Eventos `ai_question_sent`, `diary_entry_created`, `activity_*`, `signal/skill` pertencem a módulos **fora do MVP**; manter fora do SDK até habilitação.
10. Logs e traces seguem as mesmas regras (RNF-008): máscara de e-mail/tokens, nenhum payload de evento do bebê, correlação por request_id.
11. Dados de conta usados para métricas de negócio (ex.: churn) são calculados no backend em agregados, sem exportar linhas pessoais.

### 6.2 Eventos MVP permitidos (resumo)
`onboarding_started`, `baby_profile_created` (age_bucket, has_due_date), `caregiver_invited` (role), `sleep_timer_started`, `sleep_logged`, `prediction_viewed`, `prediction_outcome`, `feeding_logged`, `notification_opened`, `paywall_viewed`, `trial_started`, `subscription_started`. Propriedades conforme §11. Observação: `acquisition_channel` deve ser categórico, sem identificadores de campanha por usuário. Consultar o jurídico sobre `prediction_outcome` (deriva de dados C3 em bucket) — **DJ-05**.

---

## 7. Requisitos de segurança

Cada requisito recebe ID para rastreio em testes e no `SECURITY-REVIEW-001`.

### 7.1 Autorização / RBAC por bebê
- **SEC-001** Autorização server-side em toda consulta e mutação; BFF e API validam; nunca confiar no cliente (RB-009).
- **SEC-002** Papéis por bebê: Owner (tudo, incl. remover cuidador, transferir, excluir bebê — RB-007), Caregiver (ler/escrever eventos), ReadOnly (ler). Matriz de permissões em testes automatizados.
- **SEC-003** Toda query de dados de bebê filtra por `baby_id` com verificação de `CaregiverMembership ativo`. Considerar Row-Level Security do PostgreSQL como defesa em profundidade.
- **SEC-004** Acesso só após convite/aceite ou criação própria (RB-006). Convites: token aleatório ≥128 bits, hash armazenado, expira em 7 dias, uso único, vinculado ao e-mail convidado, revogável, limite de convites por Owner/dia.
- **SEC-005** Revogação de cuidador/compartilhamento: cessa acesso imediato, invalida sessões escopadas, caches e tokens (RB-015); cliente remove dados locais do bebê na próxima sync.
- **SEC-006** Prevenção de IDOR: IDs não sequenciais (UUID v4/v7), mas **não** depender disso; respostas 404 uniformes para recurso inexistente e sem permissão.
- **SEC-007** Último Owner não pode ser removido sem transferência.

### 7.2 Autenticação e sessões
- **SEC-010** Senhas: Argon2id (ou bcrypt cost adequado), política de tamanho mínimo, checagem contra senhas vazadas (k-anonymity), sem regras de composição obrigatórias.
- **SEC-011** Access token curto (≤15 min, JWT/PASETO assinado, escopo mínimo); refresh token opaco, rotativo, uso único, com detecção de reuso (revoga a família).
- **SEC-012** Tokens no Keychain (iOS) / Keystore (Android); proibido em logs, em backups do SO e em URLs.
- **SEC-013** Lista de sessões/dispositivos visível ao usuário, revogação individual e "sair de todos" (RF-054); revogar todas ao trocar senha/e-mail.
- **SEC-014** Reautenticação para ações sensíveis: exportar, excluir, trocar e-mail/senha, transferir propriedade.
- **SEC-015** MFA/passkeys: recomendado, fora do MVP (registrar como backlog); SSO Apple/Google conforme decisão de produto.
- **SEC-016** Verificação de e-mail antes de aceitar convites ou liberar exportação.
- **SEC-017** Recuperação de senha: token único de curta duração, resposta genérica, invalida sessões.

### 7.3 Criptografia e segredos
- **SEC-020** TLS 1.2+ (preferir 1.3), HSTS; certificate pinning avaliado (risco operacional) **[decisão de segurança]**; ATS/Network Security Config restritivos.
- **SEC-021** Criptografia em repouso: disco/volume do Postgres e backups com chaves gerenciadas por KMS; avaliar criptografia em nível de campo para `Baby.display_name`, `notes` e nascimento **[recomendado; ADR]**.
- **SEC-022** Banco local dos apps: proteção de arquivo do SO (Data Protection iOS; criptografia Android) e, preferencialmente, SQLCipher com chave no Keychain/Keystore; excluir do backup em nuvem do SO dados sensíveis conforme decisão **[validar]**.
- **SEC-023** Segredos em cofre, nunca no repositório ou no app; rotação periódica; secret scanning no CI.
- **SEC-024** Recibos de loja e chaves de API validados somente no backend.
- **SEC-025** Mídia (futuro): URLs assinadas de curta duração (≤5 min), bucket privado.

### 7.4 Auditoria
- **SEC-030** AuditEvent append-only, sem conteúdo de dados (metadata_safe): login/falha, logout, troca de credenciais, convite criado/aceito/revogado, mudança de papel, remoção de cuidador, transferência de propriedade, concessão/revogação de consentimento, solicitação/entrega de exportação, exclusão, ações administrativas, acesso de suporte.
- **SEC-031** Campos: ator, ação, entidade, entity_id, timestamp UTC, request_id, resultado, IP truncado/hasheado **[validar]**.
- **SEC-032** Acesso a auditoria restrito; integridade (hash encadeado ou armazenamento WORM) para eventos críticos.
- **SEC-033** Acesso administrativo/suporte a dados de bebê: just-in-time, justificado, registrado e revisado; sem acesso padrão de engenharia a produção.

### 7.5 Rate limit e abuso
- **SEC-040** Rate limit por IP, conta e dispositivo em login, cadastro, recuperação de senha, convite, exportação, reenvio de e-mail, validação de recibo; backoff progressivo e bloqueio temporário.
- **SEC-041** Proteção contra credential stuffing: limitação por conta+IP, detecção de senhas vazadas, CAPTCHA/prova de trabalho adaptativo, notificação ao usuário de login novo.
- **SEC-042** Limites em sync (tamanho de lote, taxa de mutações), payload máximo, paginação.
- **SEC-043** Tamanho máximo de texto livre; sanitização/escape; validação estrita de esquema (OpenAPI) na entrada.

### 7.6 Proteção contra enumeração
- **SEC-050** Login, cadastro e recuperação de senha retornam mensagens e tempos **uniformes** (execução de hash fictício); "se o e-mail existir, enviaremos instruções".
- **SEC-051** Convites não revelam se o e-mail possui conta; aceite requer o token.
- **SEC-052** IDs não enumeráveis; 404 uniforme; sem endpoints de busca de usuários.
- **SEC-053** Erros genéricos ao cliente, detalhe apenas em logs internos sem PII.

### 7.7 Outros
- **SEC-060** Segurança de dependências: SCA, SAST, DAST no CI; imagens mínimas; SBOM.
- **SEC-061** Logs/crash sem PII (RNF-008); proibido logar corpo de requisições de dados do bebê.
- **SEC-062** Ambientes não produtivos usam dados sintéticos; nunca cópias de produção.
- **SEC-063** Resposta a incidentes: runbook, classificação, comunicação à ANPD e aos titulares em prazo razoável (art. 48; Resolução ANPD vigente **[validar prazo, p.ex. 3 dias úteis]**), registro de incidentes. DJ-10.
- **SEC-064** Backups cifrados, testes de restauração periódicos, RPO/RTO definidos (RNF-012), restore com reaplicação de exclusões.
- **SEC-065** Headers de segurança no BFF, CORS restrito, proteção CSRF se houver web/admin.
- **SEC-066** Conteúdo de saúde: previsões e textos com disclaimers; não diagnosticar (RNF-014, RB-005). Revisão de redação do app por especialista e jurídico.
- **SEC-067** Upload (futuro): validação MIME/magic bytes, antivírus, reprocessamento de imagem, remoção de EXIF/geolocalização.

---

## 8. Modelo de ameaças inicial (STRIDE resumido)

Ativos: contas, dados de bebê (C3), tokens/sessões, convites, canal de sync, recibos de assinatura, tokens de push, backups, ferramenta de analytics.
Fronteiras de confiança: app ↔ BFF; BFF ↔ API; API ↔ Postgres/fila; API ↔ provedores (APNs/FCM, lojas, e-mail, analytics); operadores humanos ↔ produção.

| Categoria | Ameaça | Ativo | Mitigação (ref.) | Risco residual |
|---|---|---|---|---|
| **S**poofing | Credential stuffing / roubo de conta | Conta | SEC-010, 040, 041, 013 | Médio |
| S | Convite interceptado ou reutilizado | Acesso ao bebê | SEC-004, 051, 016 | Médio |
| S | Recibo de loja forjado (entitlement) | Subscription | SEC-024, RB-009 | Baixo |
| **T**ampering | Alteração de eventos por cuidador não autorizado | Eventos | SEC-001..003, versão/sync | Baixo |
| T | Mutação replay / sync adulterado | Sync | UUID por mutação, idempotência, validação de versão | Baixo |
| T | Adulteração de auditoria | Audit | SEC-032 | Baixo |
| **R**epudiation | Cuidador nega ter editado/excluído | Eventos | SEC-030, autoria nos eventos | Baixo |
| R | Disputa sobre consentimento | Consent | Registro versionado imutável (§3.3) | Baixo |
| **I**nformation disclosure | IDOR/BOLA entre bebês | C3 | SEC-001..003, 006, testes de matriz | Médio |
| I | Vazamento via logs, analytics ou crash | C3/C2 | §6, SEC-061 | Médio |
| I | Vazamento de banco/backup | Tudo | SEC-021, 023, 064 | Médio |
| I | Enumeração de contas | C2 | SEC-050..053 | Baixo |
| I | Ex-cuidador mantém dados locais ou acesso | C3 | SEC-005, RB-015 | Médio |
| I | Perda/roubo do dispositivo | Banco local | SEC-012, 022, bloqueio de tela, logout remoto | Médio |
| I | Link de exportação vazado | Export | Link único, curto, reautenticação, aviso por e-mail | Baixo |
| **D**enial of service | Abuso de API/sync/e-mail (spam por convites) | Disponibilidade | SEC-040, 042, filas | Médio |
| D | Falha de worker/jobs de notificação | Operação | Idempotência, DLQ (RNF-011) | Baixo |
| **E**levation of privilege | ReadOnly escreve; Caregiver remove Owner | RBAC | SEC-002, 007, testes | Baixo |
| E | Abuso de acesso de suporte/admin | C3 | SEC-033 | Médio |
| E | Dependência vulnerável / cadeia de suprimentos | Plataforma | SEC-060 | Médio |

Itens a aprofundar no `SECURITY-REVIEW-001` e no ADR de sync: conflito multi-cuidador e tombstones como canal de vazamento (tombstone sem conteúdo), segurança do modo offline, ataques a deep links de convite, privacidade das notificações push (conteúdo da notificação **não** deve incluir nome do bebê ou detalhes na tela de bloqueio por padrão).

---

## 9. RIPD — esboço (Relatório de Impacto à Proteção de Dados Pessoais)

Rascunho para o encarregado e o jurídico, a ser revisado antes de usuários reais (Privacy Gate). Referência: art. 5º, XVII e art. 38 LGPD. **[Necessidade de RIPD formal e seu conteúdo: DJ-03.]**

1. **Identificação**: controlador Nina [razão social/CNPJ a preencher]; encarregado [a nomear]; versão, data, aprovadores.
2. **Descrição do tratamento**: aplicativo de acompanhamento de rotina de bebês (sono, alimentação, fraldas) com previsões por regras; multi-cuidador; offline-first; assinatura; notificações. Volume estimado, perfil dos titulares (adultos responsáveis; crianças de 0 a ~3 anos), fluxo de dados (§1 e arquitetura).
3. **Natureza, escopo, contexto, finalidades**: §1 e §2. Contexto: dados de criança, vulnerabilidade, usuários em situação de cansaço/sensibilidade emocional, possibilidade de inferência de saúde.
4. **Necessidade e proporcionalidade**: minimização (sem sexo, foto, nome civil obrigatório, localização precisa); bases legais; alternativas consideradas (processamento local, analytics opt-in).
5. **Partes envolvidas**: operadores e suboperadores (cloud, e-mail transacional, APNs/FCM, Apple/Google, analytics, crash) com país, DPA, garantias de transferência internacional (DJ-04).
6. **Riscos identificados** (probabilidade × impacto, escala a definir):

| ID | Risco ao titular | Origem | Mitigação | Residual |
|---|---|---|---|---|
| R1 | Acesso indevido a dados do bebê | BOLA, conta roubada | §7.1/7.2 | Médio |
| R2 | Reidentificação via analytics | Eventos + buckets | §6 | Baixo/Médio |
| R3 | Inferência de saúde/condição (prematuridade, distúrbios do sono) | Dados C3 | Minimização, sem decisões automatizadas relevantes, disclaimers | Médio |
| R4 | Exposição por ex-cuidador/guarda conflituosa | Compartilhamento | SEC-005, revogação imediata, DJ-09 | Médio |
| R5 | Retenção excessiva / dado não excluído (backups, operadores) | Exclusão incompleta | §4, §5.5 | Médio |
| R6 | Transferência internacional sem garantias | Cloud/analytics | DJ-04, preferir região Brasil | A definir |
| R7 | Conteúdo/previsões interpretados como diagnóstico | Produto | RNF-014, SEC-066 | Médio |
| R8 | Notificações revelam dados na tela de bloqueio | Push | Conteúdo neutro por padrão | Baixo |
| R9 | Vazamento por incidente | Plataforma | §7, SEC-063 | Médio |
| R10 | Consentimento inválido para dados de criança | Processo | §3, DJ-01/02 | Alto até resolução |

7. **Medidas de salvaguarda**: §3 a §7.
8. **Conclusão e aprovação**: parecer do encarregado, parecer jurídico, decisão do controlador, plano de ação com responsáveis e datas, gatilhos de revisão (novos módulos: diário/foto/IA/comunidade; novo operador; incidente; mudança de base legal).

---

## 10. Checklist do Privacy Gate

Gate bloqueante antes de usuários reais (ref. `specification.md`). Cada item exige evidência (link/PR/documento). Itens marcados (J) dependem de decisão jurídica.

**Governança e jurídico**
- [ ] (J) Decisões DJ-01 a DJ-10 respondidas e registradas em ADR
- [ ] (J) Bases legais por finalidade aprovadas (§2)
- [ ] (J) Encarregado (DPO) nomeado e canal de contato publicado
- [ ] (J) RIPD revisado e aprovado (§9)
- [ ] (J) Política de Privacidade e Termos de Uso publicados (PT-BR), linguagem clara; aviso de privacidade específico para dados de criança
- [ ] (J) Inventário de operadores, DPAs assinados, transferência internacional coberta
- [ ] (J) Registro das operações de tratamento (ROPA) criado

**Consentimento**
- [ ] Consentimentos por finalidade, sem pré-marcação, versionados e auditados (§3)
- [ ] Revogação disponível in-app e efetiva nos clientes e no servidor
- [ ] Recusar opcionais não bloqueia o núcleo
- [ ] Teste: sem consentimento de analytics nenhum evento sai do app

**Minimização e inventário**
- [ ] Sem coleta de sexo, foto, localização precisa, IDs de publicidade
- [ ] Texto livre limitado e excluído de logs/analytics
- [ ] Mapa de dados atualizado e conferido com o código

**Direitos do titular**
- [ ] Exportação funcional (JSON versionado, UTC+timezone), testada ponta a ponta (RF-044)
- [ ] Exclusão de conta e de bebê funcional, incluindo filas, caches, push tokens, operadores e clientes (RF-045)
- [ ] Correção in-app
- [ ] SLA e processo de DSAR manual documentados, registro `DsarRequest`
- [ ] Teste de restauração de backup reaplica exclusões

**Segurança (ref. §7)**
- [ ] Matriz RBAC testada automaticamente (Owner/Caregiver/ReadOnly, negativos)
- [ ] Testes de IDOR/BOLA em todos os endpoints de bebê
- [ ] Sessões: rotação de refresh, detecção de reuso, listagem e revogação (RF-054)
- [ ] Rate limit e anti-enumeração verificados (testes de resposta uniforme)
- [ ] Criptografia em trânsito/repouso, segredos em cofre, scanner de segredos no CI
- [ ] Auditoria implementada e restrita (RF-055)
- [ ] Logs/crash sem PII verificados por amostragem e teste automatizado
- [ ] Runbook de incidente e comunicação à ANPD/titulares
- [ ] `SECURITY-REVIEW-001` sem achados críticos/altos abertos

**Analytics (ref. §6)**
- [ ] `analytics-schema.json` com allowlist e teste de contrato
- [ ] Pseudônimo desacoplado de user_id/baby_id e apagável
- [ ] Nenhum SDK de terceiros com coleta automática de identificadores

**Produto e transparência**
- [ ] Disclaimers de saúde/previsão presentes (RNF-014, RB-005)
- [ ] Notificações sem dados sensíveis na tela de bloqueio por padrão
- [ ] Textos de privacidade just-in-time (onboarding do bebê, convite, push)
- [ ] Fluxo de convite e remoção de cuidador revisado (RB-006/007/015)

**Retenção**
- [ ] Jobs de retenção/purga implementados e monitorados (logs, tombstones, convites, sessões, exportações)
- [ ] Tabela de retenção (§4) aprovada pelo jurídico

---

## 11. Decisões jurídicas pendentes

| ID | Decisão | Por que importa |
|---|---|---|
| DJ-01 | Enquadramento dos dados de rotina do bebê (sono, alimentação, fraldas, data prevista do parto/prematuridade, pumping): "dado pessoal sensível referente à saúde" (arts. 5º, II e 11) ou dado pessoal comum | Define bases legais admissíveis, nível de controles e conteúdo do RIPD |
| DJ-02 | Tratamento de dados de criança (art. 14): base legal (consentimento específico e em destaque de um dos pais/responsável legal, §1º, ou hipótese do art. 11 / melhor interesse); como verificar, de forma razoável, que o usuário é responsável legal (§5º); forma da declaração e do aviso de privacidade (§6º, linguagem simples) | Validade do tratamento do núcleo do produto |
| DJ-03 | Obrigatoriedade, escopo e momento do RIPD; necessidade de nomear encarregado (DPO) e consulta à ANPD | Privacy Gate |
| DJ-04 | Transferência internacional (cloud fora do Brasil, analytics, e-mail, push): mecanismo (cláusulas-padrão, adequação) e requisito de região Brasil | Escolha de cloud (pergunta aberta na especificação) |
| DJ-05 | Base legal de analytics de produto (legítimo interesse vs. consentimento) e se eventos em buckets derivados de dados C3 (ex.: `prediction_outcome`, `age_bucket`) são aceitáveis | Taxonomia §11, RF-048 |
| DJ-06 | Prazos de retenção (§4): registros de acesso, auditoria, consentimentos como prova, dados fiscais, backups, inatividade e janela de arrependimento | Política de retenção |
| DJ-07 | Prazos e formato de atendimento a DSAR (15 dias, confirmação), canal e verificação de identidade, tratamento de pedidos de terceiros | Fluxos §5 |
| DJ-08 | Papéis (controlador/operador/controlador conjunto) incluindo Apple/Google (pagamentos), analytics e provedores de infra; conteúdo mínimo dos DPAs | Contratos |
| DJ-09 | Multi-cuidador e família: quem exerce direitos do bebê (Owner vs. outros responsáveis legais), guarda compartilhada/conflito, exclusão de conta do Owner com outros cuidadores, exportação por cuidador não-Owner, tratamento de dados de terceiros adultos (cuidadores) | RB-006/007/015, DSAR |
| DJ-10 | Procedimento e prazos de comunicação de incidentes à ANPD e aos titulares; critérios de "risco relevante" | Runbook SEC-063 |
| DJ-11 | Idade mínima do usuário adulto (adolescentes pais/mães, art. 14 aplicável ao próprio usuário menor de 18) | Onboarding, Termos |
| DJ-12 | Pumping como dado de saúde da lactante e uso futuro de IA/diário/fotos/comunidade: exigem novo RIPD e consentimentos próprios | Roadmap pós-MVP |
| DJ-13 | Dever de aviso sobre limites do serviço (não é produto médico; eventual enquadramento regulatório de software como dispositivo médico) e redação de disclaimers | RNF-014, RB-005 |

Observação: este documento não constitui parecer jurídico. Aprovação de PRIV-001 requer revisão do jurídico/encarregado; a engenharia pode prosseguir com controles técnicos (§7) que independem das decisões acima.
