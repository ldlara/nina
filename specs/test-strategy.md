# Estratégia de Testes e Aceite — Nina (QA-001)

Status: proposta para aprovação · Autor: software-quality-engineer · Data: 2026-10-08
Fontes: `specs/discovery-napper-wonder-weeks.md` (§6.2–6.4, §13, §14), `docs/project/specification.md`, `architecture.md`, `backlog.md`, ADR-0001..0004.

Observação de ambiente: `status.md` registra que o container atual não tem `dotnet` SDK. Esta estratégia define **o que** e **como** testar; a execução do backend depende de CLOUD-001 (CI) ou de instalação do SDK. Nenhum resultado de teste é alegado aqui.

Premissas herdadas das ADRs (ainda "propostas"): ADR-0003 (UUID de mutação, `base_version`, cursor monotônico por bebê, LWW por campo com auditoria, tombstones) e ADR-0004 (regras por idade, mediana ponderada, confiança, fallback cold start). Pontos pendentes das ADRs (janela de retenção de tombstone, formato do cursor, tabelas de referência validadas por especialista) estão marcados como **[PENDENTE]** e os testes correspondentes usam parâmetros configuráveis até a decisão.

---

## 1. Princípios

1. **Risco primeiro.** Maior investimento onde o erro é irreversível ou afeta confiança: sync (perda/ressurreição de dados), autorização por bebê (vazamento de dado de criança), motor de sono (previsão enganosa), LGPD.
2. **Pirâmide, não sorvete.** Maioria de testes rápidos e determinísticos (unidade/propriedade); integração com Postgres real; poucos E2E.
3. **Determinismo.** Relógio, timezone, UUID e aleatoriedade sempre injetados (`TimeProvider`, `Clock` no Swift, `kotlinx-datetime`/`Clock` no Kotlin). Nenhum teste depende de `DateTime.Now`, rede externa ou ordem de execução.
4. **Contrato único.** OpenAPI (`/contracts`) é a fonte; BFF, iOS e Android são testados contra ele.
5. **Sem PII real** em qualquer ambiente de teste (seção 10).
6. **Teste negativo é requisito.** Todo endpoint com `babyId` tem teste de acesso negado.
7. **Previsão não é diagnóstico (RNF-014).** Testes verificam linguagem probabilística e ausência de afirmações diagnósticas.

---

## 2. Pirâmide de testes por camada

Proporção-alvo (por quantidade): ~70% unidade/propriedade, ~20% integração/contrato, ~8% UI/componente, ~2% E2E/exploratório guiado.

### 2.1 Backend .NET (xUnit)

| Nível | Escopo | Ferramentas | Regras |
|---|---|---|---|
| Unidade (domínio) | Entidades, regras RB-xxx, motor de sono (biblioteca pura), cálculo de idade corrigida, resolução de conflito, política de papéis | xUnit, FluentAssertions, `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing), AutoFixture/Bogus (seed fixa) | Sem I/O. < 100 ms por teste. Projeto `*.Tests.Unit` por módulo (Identity, Family, Tracking, SleepIntelligence, Notifications, Subscriptions, Privacy). |
| Propriedade | Merge/sync, idempotência, invariantes do motor (seção 4, 5) | FsCheck.Xunit | Seeds registradas no log para reproduzir falhas. |
| Arquitetura | Fronteiras do monólito modular (ADR-0002): módulos só dependem de contratos públicos; BFF sem regra de negócio | NetArchTest / ArchUnitNET | Quebra de fronteira falha o build. |
| Integração | Repositórios EF/SQL, migrations, endpoints HTTP in-process, outbox/worker | `WebApplicationFactory`, Testcontainers for .NET (PostgreSQL, mesma major do ambiente), Respawn para reset | Postgres **real**, nunca SQLite/InMemory. Migrations aplicadas do zero em cada execução de suite. |
| Contrato | Respostas/requests × OpenAPI | Schemathesis (fuzz estruturado) e/ou validação com `Microsoft.OpenApi` + `JsonSchema.Net`; diff de breaking change com `oasdiff` | Ver 2.3. |
| Concorrência | Duas transações simultâneas sobre o mesmo bebê (cursor, versão) | Testcontainers + `Task.WhenAll` | Verifica unicidade e monotonicidade do cursor sob concorrência. |

Convenções: nomes `Metodo_Cenario_Resultado`; um assert lógico por teste; builders de teste em `Nina.TestKit` (dados sintéticos, seção 10); proibido `Thread.Sleep` (usar relógio fake / `Eventually` com timeout).

### 2.2 Integração com Postgres (Testcontainers / docker compose)

- **Local e CI de PR:** Testcontainers (container efêmero por classe/coleção; imagem fixada por digest).
- **Ambiente de integração contínua ampliado (nightly):** `docker compose -f infra/compose.test.yml up` com Postgres + worker + stub de APNs/FCM + stub de lojas, para testes de fluxo multi-processo (API + worker de outbox).
- Verificações específicas de banco: constraints de unicidade (`mutation_id` por bebê), índices usados nas consultas de pull (via `EXPLAIN` em teste de plano para consultas críticas), isolamento (`READ COMMITTED`/`SERIALIZABLE` conforme DB-001), migrations **forward-only** com teste "upgrade a partir do schema N-1 com dados semente", e teste de rollback de aplicação (app N-1 com schema N).
- Row-level security / filtros de tenancy (se adotados em DB-001) têm testes diretos em SQL.
- Teste de restauração de backup (RNF-012) em nightly: dump → restore → checagem de contagem/integridade.

### 2.3 Contrato OpenAPI e BFF

- **Contrato congelado (API-001)** antes das ondas paralelas (regra do backlog). Mudança em `/contracts` exige: diff `oasdiff` sem breaking change, ou bump de versão + ADR + aprovação.
- **Provider tests:** Schemathesis roda contra a API/BFF em Testcontainers (stateful links onde existirem) validando status codes documentados, schemas e ausência de 5xx.
- **Consumer tests:** iOS e Android geram clientes/DTOs a partir do OpenAPI (ou validam fixtures JSON) e executam testes de decodificação sobre **fixtures canônicas** versionadas em `/contracts/fixtures` (mesmo conjunto para os dois clientes e para o servidor). Campos desconhecidos são ignorados; campos obrigatórios ausentes falham.
- **BFF:** testes de composição (agregação de timeline + previsão + entitlement), DTO por cliente, propagação de autorização (o BFF nunca amplia permissão), mapeamento de erros (ProblemDetails RFC 9457), timeouts/degradação quando um módulo falha (resposta parcial documentada), ausência de regra de negócio (teste de arquitetura). Wiremock/in-process fakes para módulos downstream em testes de unidade do BFF; integração real com a API em Testcontainers.
- **Compatibilidade de versão de cliente:** matriz "cliente N-1 × servidor N" executada no nightly com fixtures do contrato anterior.

### 2.4 iOS (Swift)

| Nível | Ferramentas | Escopo |
|---|---|---|
| Unidade | XCTest (+ swift-testing se baseline permitir) | ViewModels, sync engine (fila, retry, estados), cálculo local de baseline de sono, formatação/i18n, mapeamento DTO. Relógio e rede injetados. |
| Persistência | XCTest + GRDB com banco em memória/arquivo temporário | Migrations GRDB (N-1 → N com dados), consultas da timeline, fila de mutações, tombstones locais, rollback em falha. |
| Snapshot | swift-snapshot-testing | Telas-chave em PT/EN/ES, Dynamic Type máximo, dark mode, VoiceOver labels (A11Y). |
| UI/E2E | XCUITest | Fluxos críticos (seção 11.3) contra backend stub local (servidor fake com contrato) e, no nightly, contra ambiente de integração. Modo avião simulado via flag de rede injetável (não depender de Network Link Conditioner). |
| Contrato | XCTest + fixtures | Decodificação das fixtures canônicas. |
| Plataforma | StoreKit Testing (arquivo `.storekit`) | Compra, restauração, renovação, falha, reembolso. |

### 2.5 Android (Kotlin)

| Nível | Ferramentas | Escopo |
|---|---|---|
| Unidade | JUnit5/JUnit4, Turbine, MockK, kotlinx-coroutines-test | ViewModels/StateFlow, sync engine (WorkManager worker testado via `TestListenableWorkerBuilder`), baseline de sono, mapeamento DTO. |
| Persistência | Room testing (`MigrationTestHelper`, banco in-memory) | Migrations com schemas exportados, DAOs, fila de mutações, tombstones. |
| UI componente | Compose UI tests (`createComposeRule`), Paparazzi/Roborazzi para snapshot | Componentes e telas em PT/EN/ES, fonte 200%, TalkBack semantics (`contentDescription`, `Role`). |
| Instrumentado | Espresso/Compose + `AndroidJUnitRunner` em emulador gerenciado (Gradle Managed Devices) | Fluxos críticos; WorkManager com `WorkManagerTestInitHelper`. |
| Contrato | JUnit + fixtures | Decodificação (kotlinx.serialization). |
| Plataforma | Play Billing: `FakeBillingClient` / Play Billing Library testing | Compra, restauração, pendente, falha. |

### 2.6 E2E e exploratório

- Poucos (≤ 15) cenários E2E automatizados entre cliente real + API real + Postgres (nightly), cobrindo o caminho crítico do backlog: registrar offline → sync → ver em segundo dispositivo.
- Sessões exploratórias com charters por onda (sync multi-dispositivo, troca de fuso em viagem real, notificações em segundo plano) antes de cada release candidate. Resultados registrados no relatório de readiness (RELEASE-001).

---

## 3. Testes não funcionais (resumo)

| Tema | Teste | Gate |
|---|---|---|
| Performance (RNF-005) | Carga k6 sobre sync push/pull e timeline: p95 < 500 ms no perfil definido por PERF-001; timeline local abre sem rede (teste de UI com rede bloqueada) | PERF-001 / nightly com orçamento; falha de PR apenas em regressão > 20% em microbenchmarks do motor (BenchmarkDotNet) |
| Resiliência (RNF-011) | Falha injetada em worker/outbox (kill no meio), retries com backoff, DLQ | integração |
| Acessibilidade (RNF-009) | Auditorias automáticas (Accessibility Inspector via XCUITest `performAccessibilityAudit`; Android Accessibility Test Framework) + revisão manual VoiceOver/TalkBack | A11Y-001 |
| i18n (RNF-010) | Pseudolocalização, pluralização PT/EN/ES, formatos de data/unidade, strings sem hardcode (lint) | PR |
| Segurança (RNF-001) | SAST, dependências (SCA), secrets scan, DAST básico em ambiente efêmero, TLS, headers | SEC-001 |
| Observabilidade (RNF-008, RB-011) | Teste que captura logs/telemetria durante suítes e falha se encontrar padrões de PII (seção 9.3) | PR |
| Compatibilidade (RNF-016) | Matriz iOS/Android mínimo e máximo suportados [PENDENTE: baseline] | nightly/release |

---

## 4. Motor de sono (RF-009..014, RB-001..004, ADR-0004)

Biblioteca pura: entrada = (histórico, data de nascimento, data prevista/idade corrigida, timezone, preferências) → saída = (próxima soneca, bedtime, confiança, explicação). Todos os testes abaixo são de **unidade/propriedade**, sem I/O, com relógio injetado; pontos de integração (recalcular ao criar/editar/excluir) são testados na integração.

### 4.1 Invariantes (propriedade, FsCheck)
- I1 Determinismo: mesma entrada → mesma saída.
- I2 **Nunca modifica eventos reais** (RB-001/002): o histórico de entrada é imutável (comparação profunda antes/depois); previsão é tipo distinto de evento.
- I3 Saídas sempre em UTC com timezone contextual; horários previstos > instante de referência; bedtime dentro de limites fisiológicos plausíveis definidos pela tabela.
- I4 Confiança ∈ [0,1] (ou enumeração definida) e monotônica não decrescente com volume de dados consistentes, ceteris paribus.
- I5 Idempotência/ordem: embaralhar a ordem de entrada dos eventos não altera a saída.
- I6 Explicação nunca vazia quando há previsão; texto passa no verificador de linguagem (sem termos diagnósticos/garantias — lista de termos proibidos configurável).

### 4.2 Cold start
| ID | Cenário | Esperado |
|---|---|---|
| SE-CS-01 | Zero eventos, bebê com idade X | Usa baseline da tabela por idade; confiança "baixa"; explicação cita idade e ausência de histórico |
| SE-CS-02 | 1–2 sonecas registradas (abaixo do limiar de volume) | Ainda baseline (ou mistura com peso mínimo definido); confiança baixa |
| SE-CS-03 | Limiar de dados atingido exatamente (N-1 vs N eventos) | Transição documentada baseline → personalizado; teste de fronteira |
| SE-CS-04 | Bebê recém-nascido (0–6 semanas) | Tabela de faixa neonatal; sem imposição de número fixo de sonecas |
| SE-CS-05 | Prematuro com data prevista (RB-004, RF-005) | Usa idade corrigida; data de nascimento e data prevista armazenadas separadamente; resultado difere do cronológico [PENDENTE: política de corrigida — dúvida aberta] |
| SE-CS-06 | Fronteira de faixa etária (dia anterior/posterior à transição de faixa) | Sem salto absurdo; transição suave/definida |
| SE-CS-07 | Só sono noturno registrado, sem sonecas | Baseline para sonecas; bedtime personalizado com confiança limitada |

### 4.3 Outliers
| ID | Cenário | Esperado |
|---|---|---|
| SE-OUT-01 | Soneca de 8 s (toque acidental) ou 0 min | Descartada/ponderada a ~0 pela regra de qualidade; não afeta mediana |
| SE-OUT-02 | Soneca de 14 h / fim antes do início / duração negativa | Rejeitada na validação do domínio (erro), ou excluída do cálculo; nunca quebra o motor |
| SE-OUT-03 | Dia de doença/vacina com sono muito atípico entre dias normais | Mediana ponderada limita influência; previsão varia dentro de tolerância máxima definida |
| SE-OUT-04 | Timer esquecido ligado (sono "aberto" > limite) | Evento aberto não entra no histórico fechado; previsão ignora e/ou sinaliza |
| SE-OUT-05 | Sonecas sobrepostas (dois cuidadores registram o mesmo sono) | Deduplicação/sobreposição tratada; contagem de sonecas não dobra |
| SE-OUT-06 | Alta variância (horários caóticos) | Confiança baixa; explicação reflete irregularidade |
| SE-OUT-07 | Lacuna longa sem registros (ex.: 5 dias) | Peso de dados antigos decai; volta a baseline se janela recente vazia |
| SE-OUT-08 | Número de sonecas acima do máximo plausível no dia | Não gera previsão além do teto da tabela |

### 4.4 Troca de timezone (RB-010, RB-014, critério §14 nº 9)
| ID | Cenário | Esperado |
|---|---|---|
| SE-TZ-01 | Evento gravado em UTC + timezone contextual; leitura em outro fuso | Instante idêntico; exibição local muda; **registro persistido inalterado** |
| SE-TZ-02 | Família viaja de America/Sao_Paulo para Europe/Lisbon (+3/+4 h) | Eventos históricos não são reinterpretados; previsões passam a respeitar o novo fuso do bebê/família conforme regra de produto (adaptação gradual vs imediata documentada) |
| SE-TZ-03 | Dia de transição de horário de verão (DST) — dia de 23 h e de 25 h (ex.: America/New_York, Europe/Lisbon; Brasil sem DST desde 2019, testado como controle) | Durações calculadas por diferença de instantes UTC, não por relógio de parede; totais diários corretos; sem soneca duplicada/perdida na hora repetida/inexistente |
| SE-TZ-04 | Sono noturno atravessando meia-noite local e mudança de fuso no meio | Atribuição ao "dia" do bebê consistente com regra (dia de início ou dia local do bedtime) |
| SE-TZ-05 | Dois cuidadores em fusos diferentes registram eventos | Ambos convertem para o mesmo instante UTC; timeline ordenada por UTC |
| SE-TZ-06 | Fuso com offset fracionário (Asia/Kolkata +5:30, Australia/Lord_Howe 30 min DST) | Cálculos corretos |
| SE-TZ-07 | Mudança de timezone do perfil do bebê | Re-agrupamento de totais diários é recomputável; nenhum evento alterado; auditoria registra mudança |
| SE-TZ-08 | Atualização do tzdata (nome IANA renomeado/obsoleto) | Nome legado resolve ou migra; teste com alias (ex.: "America/Buenos_Aires" → "America/Argentina/Buenos_Aires") |

### 4.5 Edição retroativa (RF-009, RF-012, RB-001..003, critério §14 nº 2)
| ID | Cenário | Esperado |
|---|---|---|
| SE-ED-01 | Alterar fim de soneca passada | Duração, wake window seguinte, total diário e próxima previsão recalculados; **evento real editado exatamente como informado**, nenhum outro evento real alterado |
| SE-ED-02 | Excluir soneca (tombstone) | Totais e previsão recalculados sem o evento; evento marcado deletado, histórico de auditoria mantido |
| SE-ED-03 | Inserir soneca esquecida no passado | Wake windows vizinhas recalculadas; previsão muda dentro do esperado |
| SE-ED-04 | Edição que cria sobreposição | Rejeitada ou sinalizada conforme regra; estado consistente |
| SE-ED-05 | Edição de evento com mais de N dias | Aceita; métricas históricas e gráficos recomputados; previsões consideram só a janela recente |
| SE-ED-06 | Editar e reverter (A→B→A) | Previsão idêntica à original (idempotência/recomputabilidade, RB-001) |
| SE-ED-07 | Duas edições concorrentes vindas do sync | Resultado independe da ordem de chegada após convergência |
| SE-ED-08 | Recompute disparado por evento (integração) | Job de recálculo idempotente; reprocessar o mesmo evento não gera previsões duplicadas; previsão antiga fica superada, não apagada indevidamente |
| SE-ED-09 | Previsão dependente de evento editado já notificado | Gera novo estado de notificação (seção 7) |

### 4.6 Qualidade da tabela de referência
- Teste de dados: a tabela por idade é carregada de arquivo versionado; teste valida cobertura contínua de faixas (sem lacunas/sobreposição), limites min ≤ típico ≤ máx, e hash/versão registrado na previsão.
- **[PENDENTE]** Validação de conteúdo por especialista (ADR-0004) é gate humano, registrado como evidência (ata/aprovação) — não substituível por teste automatizado.
- Golden tests: conjunto de "bebês sintéticos" (perfis A–F: regular, irregular, 1 soneca, prematuro, mudança de fase, etc.) com saídas esperadas versionadas; mudança de saída exige revisão explícita (aprovação de snapshot).
- Paridade cliente/servidor: o baseline offline calculado no cliente (ADR-0004) é comparado ao do servidor com as mesmas fixtures; divergência > tolerância configurada falha o build. Fixtures compartilhadas em `/contracts/fixtures/sleep-engine`.

---

## 5. Sync offline (RF-007, RF-046, RF-047, RNF-007, ADR-0003)

Camadas: (a) servidor — domínio de sync com propriedade e integração Postgres; (b) cliente — sync engine iOS e Android com mesmos cenários via fixtures; (c) E2E com 2 dispositivos (spike ARCH-003 gera o harness inicial).

### 5.1 Harness
Simulador de dispositivos (`FakeDevice`) no TestKit .NET: cada dispositivo tem banco local, fila de mutações, relógio próprio (com skew configurável) e conectividade controlável (online/offline/intermitente, queda no meio do push). Permite escrever cenários determinísticos multi-dispositivo e propriedade "após convergência, todos os dispositivos têm o mesmo estado". Os clientes nativos reimplementam o mesmo roteiro em seus frameworks, consumindo os mesmos arquivos JSON de cenário (`/contracts/fixtures/sync-scenarios/*.json`), garantindo paridade iOS/Android/servidor.

### 5.2 Conflito entre cuidadores
| ID | Cenário | Esperado |
|---|---|---|
| SY-CF-01 | A e B criam eventos diferentes offline | Merge por entidade: ambos persistem; timeline com ambos |
| SY-CF-02 | A e B editam **campos diferentes** do mesmo evento | LWW **por campo**: ambos os campos preservados |
| SY-CF-03 | A e B editam o **mesmo campo** | Vence maior `client_created_at` (com desempate determinístico por device_id/versão); valor perdedor registrado em auditoria/histórico |
| SY-CF-04 | Skew de relógio (dispositivo A 10 min adiantado) | Regra não permite que relógio errado "vença" indefinidamente: ordenação usa versão canônica do servidor quando aplicável; limite de tolerância/clamp documentado [PENDENTE: ADR-0003 detalhar] |
| SY-CF-05 | A edita, B exclui o mesmo evento | Política definida (exclusão vence ou edição ressuscita? — deve estar na ADR); resultado idêntico independentemente da ordem de chegada |
| SY-CF-06 | A edita com `base_version` defasada | Servidor aplica regra de merge, devolve versão canônica; cliente reconcilia sem perder edição local pendente |
| SY-CF-07 | Três dispositivos, ordens de entrega permutadas | **Convergência** (propriedade: todas as permutações produzem o mesmo estado final) |
| SY-CF-08 | Diário (V1) | Fora do MVP; teste registrado como pendente para V1 (preserva histórico) |
| SY-CF-09 | Auditoria mínima (RF-020) | Toda edição/exclusão de dado compartilhado gera registro com quem, quando, antes/depois |

### 5.3 Idempotência
| ID | Cenário | Esperado |
|---|---|---|
| SY-ID-01 | Mesmo push reenviado 1x, 2x, N vezes (timeout após commit no servidor) | Um único efeito; resposta idêntica; cursor não avança em duplicidade |
| SY-ID-02 | Push parcial: lote com 50 mutações, conexão cai na 30ª | Reenvio completo aplica só as faltantes; sem duplicatas |
| SY-ID-03 | Mesmo `mutation_id` com payload diferente (bug/ataque) | Rejeitado com erro de conflito de idempotência (não aplica nem sobrescreve silenciosamente) |
| SY-ID-04 | Mesmo `mutation_id` de bebês/usuários diferentes | Escopo da chave inclui bebê (e usuário); não há colisão cruzada nem vazamento |
| SY-ID-05 | Concorrência: dois pushes simultâneos com mesmo `mutation_id` | Constraint única no Postgres; exatamente um aplicado |
| SY-ID-06 | Pull repetido com mesmo cursor | Resposta estável; pull com cursor atual retorna vazio |
| SY-ID-07 | Cursor monotônico sob escrita concorrente | Sem lacunas visíveis que façam o cliente perder eventos (ex.: transação longa commitando com versão menor) — teste de concorrência no Postgres |
| SY-ID-08 | Cursor inválido/adulterado/de outro bebê | 400/403 sem vazamento; cliente força re-sync completo |
| SY-ID-09 | Crash do cliente entre aplicar pull e persistir cursor | Reaplicação idempotente; sem duplicar eventos locais |
| SY-ID-10 | Ordem: mutações do mesmo dispositivo aplicadas na ordem de criação | Dependências (criar → editar → excluir) respeitadas mesmo com reenvio |

### 5.4 Tombstones
| ID | Cenário | Esperado |
|---|---|---|
| SY-TB-01 | Exclusão propaga para todos os dispositivos | Evento removido da timeline; dado de conteúdo apagado/anonimizado conforme política, mantendo apenas identificador/versão |
| SY-TB-02 | Dispositivo offline por menos que a janela de retenção volta com evento antigo editado | Tombstone vence ou conflito resolvido conforme SY-CF-05; **sem ressurreição** |
| SY-TB-03 | Dispositivo offline **além** da janela de retenção de tombstone | Servidor detecta cursor expirado → cliente executa re-sync completo; dados locais não enviados são reconciliados (não se perdem silenciosamente; pendentes são reenviados como novas mutações com revisão) [PENDENTE: janela e formato do cursor] |
| SY-TB-04 | Expurgo de tombstones (job) | Só remove os mais antigos que a janela e já convergidos; teste de fronteira (janela−1 s, janela+1 s) com relógio fake; idempotente |
| SY-TB-05 | Recriar evento com mesmo UUID após exclusão | Rejeitado (UUID reservado) ou tratado como nova identidade, conforme ADR; nunca ressuscita silenciosamente |
| SY-TB-06 | Exclusão em cascata (exclusão de bebê pelo Owner, RB-007) | Tombstones de todos os eventos; dispositivos limpam banco local |
| SY-TB-07 | Previsão/métricas após tombstone | Recalculadas (liga com SE-ED-02) |

### 5.5 Revogação de cuidador (RF-006, RB-006/007/015, critério §14 nº 4)
| ID | Cenário | Esperado |
|---|---|---|
| SY-RV-01 | Owner revoga cuidador online | Próximo request do revogado (push, pull, REST, mídia assinada) retorna 403/401 imediatamente; tokens/sessões ligadas ao bebê invalidados |
| SY-RV-02 | Revogado tem mutações pendentes offline | Push rejeitado integralmente; nada aplicado; cliente descarta dados do bebê localmente após ciência (wipe) e informa o usuário |
| SY-RV-03 | Revogado tenta pull com cursor antigo | 403; cursor não utilizável; sem eventos novos |
| SY-RV-04 | Cache (cliente e CDN/HTTP) | Respostas autenticadas com `Cache-Control: private, no-store` onde aplicável; teste verifica cabeçalhos; URLs de mídia assinadas expiram e não renovam após revogação |
| SY-RV-05 | Push notifications do bebê para o revogado | Tokens de dispositivo desvinculados do bebê; teste de que o job de notificação não envia a revogado (liga com seção 7) |
| SY-RV-06 | Revogação concorrente com push em andamento | Atomicidade: push ou aceito antes (e fica atribuído) ou rejeitado; sem meio-termo |
| SY-RV-07 | Eventos criados pelo revogado antes da revogação | Permanecem no bebê com autoria preservada (política) |
| SY-RV-08 | Somente Owner revoga (RB-007); Caregiver/ReadOnly tentam | 403 |
| SY-RV-09 | Revogação auditada (RF-055) e notificação de segurança (§10 Sistema) | Registro e notificação gerados uma vez |
| SY-RV-10 | ReadOnly tenta push de mutação | Rejeitado (403); pull permitido |
| SY-RV-11 | Re-convite do cuidador revogado | Novo vínculo exige novo aceite; não recupera cursor/tokens antigos |

---

## 6. Testes negativos de autorização — IDOR por bebê (critério §14 nº 3, RB-006, RB-015, SECURITY-REVIEW-001)

### 6.1 Estratégia sistemática
Cada recurso com identificador (bebê, evento, convite, cuidador, sessão/dispositivo, export, entitlement, notificação, mídia) é coberto por **uma matriz gerada a partir do OpenAPI**: para cada operação que tenha parâmetro de path/query/body referente a recurso de tenant, o teste:
1. cria dois usuários independentes U1 (dono do bebê B1) e U2 (dono do bebê B2);
2. executa a operação como U2 sobre IDs de B1 → esperado **404 (preferível, não revela existência) ou 403**, nunca 200/204 nem corpo com dados de B1;
3. repete com papéis: sem autenticação (401), token expirado, token de outro dispositivo revogado, ReadOnly em operação de escrita (403), Caregiver em operação de Owner (403).

Um **teste de cobertura da matriz** falha o build se um endpoint novo com identificador de tenant não estiver na matriz (a lista vem do OpenAPI, então não depende de disciplina manual).

### 6.2 Casos obrigatórios
| ID | Caso | Esperado |
|---|---|---|
| AZ-01 | GET/PUT/DELETE `/babies/{id}` de outro usuário | 404/403, corpo sem dados |
| AZ-02 | GET/PUT/DELETE de evento (`/babies/{b}/events/{e}`) com `e` de outro bebê mas `b` próprio (ID misturado) | 404; a consulta filtra **por bebê e evento** |
| AZ-03 | IDs sequenciais/previsíveis | IDs são UUID/ULID; teste verifica que IDs gerados não são sequenciais e que enumeração (varrer 1000 IDs) devolve respostas indistinguíveis entre "inexistente" e "sem permissão" (mesmo status, corpo e tempo aproximado) |
| AZ-04 | Pull de sync com `babyId` alheio ou cursor de outro bebê | 403/404 |
| AZ-05 | Push com mutações referenciando evento/bebê alheio dentro de lote com mutações legítimas | Lote inteiro rejeitado (ou item rejeitado sem aplicar nada alheio) conforme contrato; **nada aplicado ao bebê alheio** |
| AZ-06 | Mass assignment: body com `babyId`, `ownerId`, `role`, `createdBy` forjados | Ignorados/rejeitados; valores vêm do contexto autenticado |
| AZ-07 | Convites: aceitar convite de outro e-mail; reutilizar/adivinhar token; convite expirado/revogado | Rejeitado; tokens com entropia ≥ 128 bits; uso único; rate limit |
| AZ-08 | Escalada de papel: Caregiver se promove a Owner, altera papel de terceiros, remove Owner | 403; auditoria da tentativa |
| AZ-09 | Sessões/dispositivos (RF-054): listar/revogar sessão de outro usuário | 404/403 |
| AZ-10 | Exportação/exclusão (RF-044/045) de outro usuário; baixar arquivo de export por URL de outro | 403/404; URL de export assinada, curta e vinculada ao titular |
| AZ-11 | Mídia/foto (se presente no MVP: foto de bebê opcional RF-004) | URL assinada de curta duração, escopo ao bebê; sem URL pública previsível; expiração testada com relógio fake |
| AZ-12 | Entitlement: consultar/alterar assinatura de outra família; enviar recibo de outro usuário | 403; recibo vinculado a conta única (impede reuso entre contas) |
| AZ-13 | Push notification payload | Conteúdo não contém dados sensíveis além do necessário; não enviado a dispositivo desvinculado/revogado; ID de bebê no payload não permite abrir bebê alheio (abertura passa por autorização) |
| AZ-14 | Cache: respostas autenticadas | `Cache-Control: no-store/private`, `Vary: Authorization`; teste simulando proxy compartilhado |
| AZ-15 | BFF: agregações cruzando bebês (ex.: lista de bebês, dashboard) | Só bebês autorizados; filtro server-side, nunca no cliente |
| AZ-16 | Enumeração de contas (login, recuperação, convite por e-mail) | Respostas e tempos uniformes; rate limit e lockout (credential stuffing) |
| AZ-17 | Token de uma sessão usado após logout / "encerrar sessões" (RF-002) | 401 imediato |
| AZ-18 | JWT: `alg=none`, assinatura inválida, `aud`/`iss` errados, claims de papel forjadas | 401; papel é verificado contra o banco/estado de autorização, não só no claim |
| AZ-19 | Logs/erros | Mensagens de erro não vazam existência, IDs internos, stack trace ou SQL |
| AZ-20 | Admin/operadores (quando CMS existir) | Fora do MVP; registrar pendência |

Testes são executados em integração (Postgres real) para pegar falhas de filtro SQL, e novamente na camada BFF. SECURITY-REVIEW-001 reutiliza esta matriz como evidência e acrescenta revisão manual.

---

## 7. Notificações idempotentes (RF-037..039, RB-010, MSG-001, critério §14 nº 5)

Camadas: unidade (regras de agendamento), integração (outbox + worker + Postgres), contrato (APNs/FCM stubs).

| ID | Cenário | Esperado |
|---|---|---|
| NT-01 | Previsão de soneca gera notificação X minutos antes | Uma entrada de notificação com chave determinística (`babyId + categoria + instante-alvo + versão da previsão`) |
| NT-02 | Retry do job após falha de envio (timeout do provedor) | Backoff exponencial com jitter (relógio fake); **no máximo um envio efetivo**; status registrado (RF-039) |
| NT-03 | Worker reiniciado/duas instâncias processando a mesma mensagem de outbox | Lock/`SELECT … FOR UPDATE SKIP LOCKED` ou chave de idempotência garante um envio |
| NT-04 | Falha após enviar ao provedor e antes de gravar status | Reentrega não duplica ao usuário (idempotency key / `apns-collapse-id` / `collapse_key` FCM verificada no stub) |
| NT-05 | Previsão muda (evento editado/criado) antes do envio | Notificação anterior cancelada/reagendada; apenas a vigente é enviada; teste de recálculo (liga com SE-ED-09) |
| NT-06 | Previsão muda **depois** do envio | Nova notificação só se regra de produto permitir; não reenvia a antiga |
| NT-07 | Quiet hours (RB-010) em timezone do bebê/família | Envio adiado ou suprimido conforme regra; casos: janela atravessando meia-noite, DST, mudança de fuso entre agendar e disparar |
| NT-08 | Preferências: categoria desligada / antecedência alterada | Respeitadas; pendentes reavaliadas |
| NT-09 | Vários cuidadores/dispositivos | Entrega a todos os dispositivos autorizados, uma vez por dispositivo; dispositivo revogado/token inválido (APNs 410/FCM UNREGISTERED) é removido |
| NT-10 | DLQ | Após N tentativas vai à DLQ; métrica/alerta emitidos; reprocessamento manual é idempotente |
| NT-11 | Evento "stale" (soneca já registrada pelo cuidador antes da notificação) | Cancelada |
| NT-12 | Notificações de sistema/segurança (novo login, cuidador adicionado/removido) | Uma por ocorrência; não sujeitas a quiet hours se política assim definir [PENDENTE] |
| NT-13 | Carga: milhares de notificações no mesmo minuto | Sem duplicação; sem estourar rate do provedor (batching) |
| NT-14 | Cliente: notificação local vs remota | Se houver agendamento local no app, deduplicação entre local e push (mesma chave) |
| NT-15 | Idempotência do processador de outbox (propriedade) | Processar qualquer mensagem k vezes ≡ 1 vez |

---

## 8. Assinatura (RF-041..043, RB-009; critério §14 nº 8)

- Validação de recibo com stubs de Apple (App Store Server API/notifications v2) e Google (Play Developer API/RTDN): recibo válido, expirado, revogado/reembolsado, forjado, de outro app (bundle/package errado), de ambiente sandbox em produção.
- **Restauração em novo dispositivo/conta:** mesmo recibo → mesmo entitlement, **sem criar segunda assinatura/cobrança**; idempotência por `originalTransactionId`/`purchaseToken`.
- Recibo já vinculado a outra conta: política definida (transferir/negar) [PENDENTE: política familiar RF-043]; teste conforme decisão.
- Entitlement é calculado só no servidor; cliente com flag local forjada continua sem acesso premium (teste negativo no BFF).
- Webhooks duplicados/fora de ordem: processamento idempotente e ordenado por timestamp/versão.

---

## 9. LGPD (RF-003, RF-040, RF-044, RF-045, RF-048, RF-055, RB-011, RB-014, RNF-002/003/008)

### 9.1 Exportação (RF-044, critério §14 nº 6)
| ID | Cenário | Esperado |
|---|---|---|
| LG-EX-01 | Exportar conta com bebê, eventos de todos os tipos, cuidadores, consentimentos | Arquivo estruturado (JSON; CSV complementar) completo; validado contra **JSON Schema** versionado |
| LG-EX-02 | Metadados (RB-014) | Cada timestamp com timezone/UTC; `schema_version` presente |
| LG-EX-03 | Completude | Teste de "inventário de dados": lista de tabelas/colunas com dado pessoal (catálogo mantido por DB-001/PRIV-001); falha se coluna pessoal nova não estiver mapeada ao export **ou** marcada como isenta com justificativa |
| LG-EX-04 | Escopo | Só dados do titular e dos bebês que ele administra/tem permissão; dados de outros cuidadores limitados ao necessário (nome/papel), sem e-mail/dados sensíveis de terceiros [conforme PRIV-001] |
| LG-EX-05 | Tombstones | Eventos excluídos não aparecem no export (exceto log de auditoria do titular, se aplicável) |
| LG-EX-06 | Entrega segura | Geração assíncrona (job idempotente), link assinado de curta duração vinculado ao titular, uso único/expiração, auditoria do pedido (RF-055); AZ-10 |
| LG-EX-07 | Volume | Conta com 100k eventos exporta sem estourar memória/timeout (streaming); orçamento de tempo |
| LG-EX-08 | Diário em formato humano (§14 nº 6) | Fora do MVP (diário é V1); teste registrado como pendente V1 |
| LG-EX-09 | Idempotência | Dois pedidos simultâneos geram um job ou jobs independentes sem corrupção; reexecução não duplica arquivos |

### 9.2 Exclusão (RF-045)
| ID | Cenário | Esperado |
|---|---|---|
| LG-DL-01 | Excluir conta do Owner único de um bebê | Dados do bebê, eventos, tombstones, previsões, notificações pendentes, tokens de push, sessões, entitlement local e vínculos removidos ou anonimizados conforme política de retenção; verificação por **varredura do inventário de dados** (mesmo catálogo de LG-EX-03): nenhuma linha restante referencia o titular, exceto retenções legais explícitas |
| LG-DL-02 | Bebê com múltiplos cuidadores | Regra de produto (transferência de propriedade vs exclusão) [PENDENTE]; Caregivers não ficam com acesso a dado órfão |
| LG-DL-03 | Cuidador (não Owner) exclui a própria conta | Remove vínculo e dados pessoais; eventos que criou permanecem no bebê com autoria anonimizada |
| LG-DL-04 | Retenções legais | Registros que a lei exige manter (ex.: fiscal de assinatura, auditoria mínima) permanecem, **sem** dados desnecessários, com prazo definido; teste verifica o que fica e que expira |
| LG-DL-05 | Dispositivos | Cliente apaga banco local, tokens, caches e fotos ao receber a ordem/ao detectar conta removida; sync com cursor de conta excluída → 401/410 e wipe local |
| LG-DL-06 | Backups | Política documentada (exclusão efetiva no ciclo de rotação de backups); teste de **job de reaplicação de exclusões após restauração** (restore não ressuscita titular excluído) |
| LG-DL-07 | Idempotência e atomicidade | Exclusão é job retomável: falha no meio e retry termina sem erro; estado intermediário não expõe dados parciais; auditoria registra |
| LG-DL-08 | Pós-exclusão | Login falha; e-mail pode ser reutilizado conforme política; convites pendentes invalidados; analytics sem possibilidade de reidentificar |
| LG-DL-09 | Janela de arrependimento (se existir) [PENDENTE] | Cancelar exclusão dentro do prazo restaura estado; após prazo é irreversível |
| LG-DL-10 | Exclusão de assinatura | Não cancela cobrança na loja automaticamente; usuário é informado (teste de texto/fluxo) |

### 9.3 Demais controles LGPD
- **Consentimento versionado (RF-003):** aceite de Termos/Política grava versão, instante, base; mudança de versão exige novo aceite; consentimentos opcionais (analytics não essencial) separados e revogáveis (RF-040). Testes: sem consentimento de analytics, nenhum evento de analytics é emitido (teste no cliente com spy do SDK); revogar para de emitir imediatamente.
- **Analytics sem PII (RF-048, RB-011):** esquema de eventos de analytics validado por allowlist de campos; teste automatizado que falha se um evento contiver e-mail, nome/apelido do bebê, data de nascimento exata, texto livre/observações, coordenadas, IDs que sirvam de identificador direto. IDs de analytics são pseudônimos rotativos.
- **Logs/telemetria sem PII (RNF-008):** scanner de PII (regex para e-mail, telefone, CPF, tokens JWT, nomes do dataset sintético "canário") roda sobre logs coletados nas suítes de integração; "dados canário" únicos inseridos nos fixtures não podem aparecer em logs/traces/crash reports.
- **Auditoria (RF-055):** criação/aceite/remoção de cuidador, consentimentos, exportação, exclusão, alterações de conta geram registro imutável; teste de que o registro não pode ser alterado pela API.
- **Segurança de dados de criança (RNF-001/002):** criptografia em repouso verificada por configuração de infraestrutura (IaC scan); TLS 1.2+ mínimo (teste de handshake em ambiente de integração); pinning/ATS/Network Security Config validados nos builds de cliente.
- **Privacy Gate:** evidências desta seção + RIPD (PRIV-001) são pré-requisito do gate; a revisão jurídica é humana e não é substituída por teste.

---

## 10. Dados de teste sem PII real

Regras:
1. **Proibido** usar dados reais de pessoas, bebês, e-mails reais, fotos reais, ou cópias/dumps de produção em qualquer ambiente que não seja produção. Sem "anonimização" de dump de produção como substituta — só geração sintética.
2. E-mails: domínio reservado `example.com`/`example.test` (RFC 2606) com sufixos únicos (`qa+<guid>@example.test`). Telefones: faixas fictícias. Nomes/apelidos de bebê: lista sintética (ex.: "Bebê Teste 01") ou Bogus com seed e locale pt_BR.
3. Datas de nascimento sintéticas relativas à data de referência do teste (ex.: `hoje - 10 semanas`), nunca de pessoas reais.
4. Fotos: imagens geradas/sem pessoas (ruído/gradiente) ou de domínio público/licenciadas para teste; metadados EXIF removidos/ausentes.
5. Recibos/assinaturas: contas sandbox das lojas (Apple Sandbox/StoreKit config; Google license testers) com contas dedicadas de QA, nunca contas pessoais de equipe; credenciais em cofre/secrets de CI.
6. Tokens/segredos de teste distintos dos de produção; nunca commitados (secrets scan no CI).
7. **Dados canário:** valores marcadores únicos (`CANARY-<guid>`) em campos de PII para detectar vazamento em logs, analytics, export de terceiros e respostas de erro.
8. Geração: `Nina.TestKit` (builders determinísticos por seed) produz "bebês sintéticos" com perfis de sono (regular, irregular, prematuro, mudança de fase, histórico esparso, 12 meses de dados) usados nos golden tests (seção 4) e carga (PERF-001). Fixtures versionadas em `/contracts/fixtures`.
9. Dados de carga: gerados em massa por script com seed; volume dimensionado por PERF-001.
10. Revisão: PR que adicione fixtures passa por checagem automática de padrões de PII (CPF, e-mails fora de domínios reservados, telefones válidos, tokens) — hook de pré-commit + job de CI.
11. Retenção: bancos efêmeros destruídos ao fim do job; ambiente de staging reiniciado com seed periodicamente.

---

## 11. Ambientes de teste

| Ambiente | Finalidade | Dados | Execução |
|---|---|---|---|
| Local dev | Unidade, integração com Testcontainers, UI em simulador/emulador | Sintéticos, seed local | Sob demanda; `make test` / `dotnet test` / `xcodebuild test` / `./gradlew test` |
| CI PR (efêmero) | Gates de PR (seção 13) | Sintéticos, containers descartáveis | A cada PR |
| CI nightly | Integração ampliada via docker compose, contrato com fuzz, E2E, matriz de compatibilidade, carga leve, restore de backup | Sintéticos | Diário |
| Integração/Staging | API+BFF+worker+Postgres reais com stubs/sandbox de APNs/FCM e lojas; E2E com clientes reais (TestFlight interno / Firebase App Distribution) | Sintéticos; seed periódico; sem dump de produção | Deploy contínuo de `main`; E2E nightly; exploratório por onda |
| Pré-produção / Perf | Réplica de dimensionamento para PERF-001 e ensaio de migração | Volume sintético em escala | Sob demanda e antes de release |
| Produção | **Sem testes destrutivos.** Apenas smoke pós-deploy read-only/sintético (conta canário dedicada) e monitoramento sintético (SRE-001) | Conta canário isolada, sem dado de usuário | Pós-deploy |

Controles: segredos por ambiente; APNs/FCM sandbox; buckets e bancos isolados; ambientes de teste sem saída para provedores de produção; relógio e feature flags controláveis em staging; devices reais para amostra (matriz mínima: 2 iPhones e 3 Androids de fabricantes/versões distintos, incluindo um de baixa memória) [PENDENTE: baseline RNF-016].

---

## 12. Matriz de rastreabilidade: critérios de aceite do discovery §14 → testes

Legenda de nível: U = unidade/propriedade, I = integração (Postgres), C = contrato, UI = XCUITest/Compose UI, E2E = dois dispositivos, M = manual/exploratório, G = gate humano.

| # | Critério (discovery §14) | RFs/RBs | Testes que o comprovam | Níveis | Tarefas donas |
|---|---|---|---|---|---|
| 1 | Cuidador registra sono offline em poucos toques e vê o evento em outro dispositivo após reconexão | RF-008, 009, 046, 007, RNF-007 | **UI:** registrar sono em modo avião em ≤ N toques (orçamento medido no teste: iniciar timer, parar, salvar) — iOS/Android; SY-CF-01, SY-ID-01/02/06, SY-ID-09; E2E: dispositivo A offline cria → reconecta → dispositivo B recebe via pull (SLA de sync em staging: p95 definido por PERF-001); timeline local imediata sem rede | UI, I, E2E, U | IOS-002/003, AND-002/003, BE-004a |
| 2 | Alterar fim de soneca recalcula wake window, totais e previsão sem modificar o registro real | RF-010, 012, RB-001..003 | SE-ED-01..09; invariante I2 (histórico imutável); integração: edição via API dispara recálculo e o evento persistido == valor informado; UI: edição reflete novos totais/previsão | U, I, UI | BE-005, BE-003, IOS-004, AND-004 |
| 3 | Usuário não autorizado nunca recebe dados de bebê por ID previsível, cache, push ou mídia | RB-006, RF-006, RNF-002 | AZ-01..06, 09..19 (matriz OpenAPI com cobertura obrigatória); AZ-03 (ID não previsível, respostas indistinguíveis); AZ-14 (cache); AZ-13 (push); AZ-11 (mídia); SY-RV-04 | I, C | BE-001/002/003, BFF-001, SECURITY-REVIEW-001 |
| 4 | Revogar cuidador remove acesso ativo e invalida sessões/recursos compartilhados | RB-007, RB-015, RF-006, RF-054, RF-055 | SY-RV-01..11; AZ-17; cliente: wipe local do bebê revogado (UI/instrumentado); auditoria | I, UI, E2E | BE-002, BE-001, BE-004b, IOS/AND-003 |
| 5 | Notificações não duplicam após retries e são recalculadas quando a previsão muda | RF-037..039, RB-010, RNF-011 | NT-01..15 (em especial NT-02/03/04/15 idempotência; NT-05/06/11 recálculo); SE-ED-09 | U, I | BE-006, MSG-001 |
| 6 | Exportação contém dados do titular em formato estruturado e diário em formato humano quando solicitado | RF-044, RB-014, RF-027 | LG-EX-01..09; schema do export; **diário humano: fora do MVP (V1)** — critério parcialmente aplicável, ver nota | I, C, G | BE-008, PRIV-001, IOS/AND-005 |
| 7 | IA não apresenta diagnóstico; contexto autorizado; política de segurança | RF-035/036, RNF-013, RNF-014 | **Fora do MVP (S5).** Aplicável no MVP o subconjunto RNF-014: teste de linguagem probabilística da previsão (I6) e disclaimers em UI (snapshot). Testes de IA (avaliação de respostas, red-team, isolamento de prompt) ficam planejados para V2 | U, UI | BE-005, IOS/AND-004 |
| 8 | Assinatura restaurada em novo dispositivo recupera entitlement sem duplicar cobrança | RF-041..043, RB-009 | Seção 8: restauração idempotente por `originalTransactionId`/`purchaseToken`; StoreKit Testing e Play Billing fake nos clientes; stub das lojas no backend; negativo: flag local forjada | I, UI, E2E (sandbox) | BE-007, IOS-005, AND-005 |
| 9 | Troca de timezone não corrompe eventos históricos; UTC + timezone contextual | RB-010, RB-014, RNF-010 | SE-TZ-01..08; integração: persistência UTC+tz no Postgres (roundtrip); export com tz (LG-EX-02); UI: exibição correta em fusos diferentes; quiet hours NT-07 | U, I, UI | BE-003, BE-005, IOS/AND-002 |

Notas:
- Critério 6 (parte diário) e critério 7 não são entregáveis do MVP segundo `specification.md`; a matriz mantém a rastreabilidade para evitar perda de requisito quando entrarem (V1/V2) e declara explicitamente o escopo reduzido para o gate do MVP, sujeito à confirmação do product owner.
- Cada teste automatizado deve citar no nome ou em atributo (`[Trait("Criterio","14.2")]`, `@Tag("criterio-14-2")`, `// criterio: 14.2`) o critério/RF que cobre; o CI gera relatório de rastreabilidade a partir disso e falha se algum critério do MVP ficar sem teste associado.
- Rastreabilidade adicional RF → teste é mantida em `docs/project/` quando REQ-001 publicar os critérios de aceite por RF (esta matriz será estendida então).

---

## 13. Metas de cobertura e qualidade

Cobertura é indicador, não objetivo; metas abaixo combinam cobertura de linha/ramo com mutation testing nas áreas de maior risco.

| Área | Linha | Ramo | Mutation score (Stryker.NET) | Observação |
|---|---|---|---|---|
| Motor de sono (biblioteca pura) | ≥ 95% | ≥ 90% | ≥ 80% | + golden tests e propriedades |
| Domínio de sync (merge, idempotência, tombstones, cursor) | ≥ 95% | ≥ 90% | ≥ 80% | + propriedades de convergência |
| Autorização / políticas de papel / tenancy | ≥ 95% | ≥ 90% | ≥ 75% | + matriz IDOR 100% dos endpoints com tenant |
| Notificações (agendamento, outbox) | ≥ 90% | ≥ 85% | ≥ 70% | |
| Privacy (export/exclusão) | ≥ 90% | ≥ 85% | — | + inventário de dados 100% mapeado |
| Demais módulos backend e BFF | ≥ 80% | ≥ 70% | — | |
| Sync engine iOS / Android | ≥ 85% | ≥ 75% | — | cenários compartilhados 100% executados |
| ViewModels/camada de estado iOS/Android | ≥ 80% | — | — | |
| UI (views/composables) | não medida por linhas | — | — | medida por cobertura de fluxos críticos e snapshots |

Metas de processo:
- **Cobertura de código novo (diff coverage) ≥ 85%** em PRs (≥ 95% nas áreas críticas acima).
- **Cobertura da matriz de autorização = 100%** dos endpoints OpenAPI com identificador de tenant.
- **Rastreabilidade §14 = 100%** dos critérios aplicáveis ao MVP com ≥ 1 teste automatizado verde.
- **Fluxos críticos com UI test:** 100% dos fluxos da lista abaixo em iOS e Android.
- Taxa de testes instáveis (flaky) < 1% das execuções; teste flaky é quarentenado em ≤ 1 dia útil com bug aberto e dono; quarentena > 1 semana bloqueia release.
- Bugs: zero abertos de severidade bloqueante/crítica para release; críticos de segurança/privacidade/perda de dados tratados antes de qualquer outra coisa.

**Fluxos críticos de UI (iOS e Android):** (1) criar conta e consentimento; (2) criar bebê; (3) registrar sono com timer, retroativo e edição; (4) registrar mamada/mamadeira/fralda/pumping; (5) timeline offline→online; (6) convidar/aceitar/revogar cuidador; (7) ver previsão e explicação de confiança; (8) configurar notificações e quiet hours; (9) assinar/restaurar; (10) exportar e excluir conta; (11) trocar idioma PT/EN/ES.

---

## 14. Definition of Done por tarefa

### 14.1 DoD geral (todas as tarefas de código)
1. Código revisado (REVIEW-001) e aprovado; sem comentários bloqueantes abertos.
2. Testes novos/alterados para o comportamento, no nível correto da pirâmide; falhas reproduzíveis por seed/relógio fixos.
3. Build, lint/analisadores (warnings como erro no código novo), testes e scans do CI verdes (seção 15).
4. Metas de cobertura da seção 13 atendidas no diff.
5. Critérios de aceite da tarefa rastreáveis a testes (trait/tag); matriz §12 atualizada se aplicável.
6. Sem PII em código, fixtures, logs; secrets scan limpo.
7. Contrato OpenAPI atualizado e sem breaking change não aprovado; fixtures atualizadas.
8. Migrations (se houver) forward-only, testadas com dados do schema anterior.
9. Logs/métricas/traces relevantes adicionados, sem PII; alertas/runbook atualizados quando mudar comportamento operacional.
10. Documentação (ADR/README/docs) atualizada; pendências **[PENDENTE]** registradas como decisões abertas, não ignoradas.
11. Strings externalizadas e traduzidas PT (EN/ES quando aplicável) em tarefas de UI; acessibilidade verificada (labels, foco, contraste, Dynamic Type/fonte grande).
12. Status do backlog só vai a Done com evidência (link de pipeline verde + resultados), não por declaração.

### 14.2 DoD específico
| Tarefa | Acréscimos ao DoD geral |
|---|---|
| DB-001 | Migrations aplicam do zero e a partir do N-1 em Testcontainers; constraints de unicidade de `mutation_id`; catálogo de inventário de dados pessoais (base de LG-EX-03/LG-DL-01); testes de índices das consultas de pull |
| API-001 | OpenAPI válido (lint Spectral), exemplos, erros ProblemDetails, esquemas de sync/idempotência, fixtures canônicas; Schemathesis rodando; congelamento formal antes do trabalho paralelo |
| BE-001 | Matriz AZ-16..19 verde; rate limit/lockout testados; sessões/dispositivos (RF-054) com revogação |
| BE-002 | SY-RV-01..11 e AZ-07/08 verdes; papéis Owner/Caregiver/ReadOnly cobertos por teste de tabela de permissão (todas as operações × todos os papéis) |
| BE-003 | CRUD de todos os tipos de evento; validações (duração, sobreposição); UTC+tz; matriz IDOR dos endpoints; timeline paginada |
| BE-004a/b | SY-ID-*, SY-CF-*, SY-TB-* verdes; propriedade de convergência com ≥ 1000 execuções aleatórias; concorrência SY-ID-05/07; auditoria; harness de dispositivos entregue |
| BE-005 | SE-* verdes; golden tests aprovados; mutation ≥ 80%; benchmark sem regressão; linguagem não-diagnóstica; tabela validada por especialista (gate humano) antes de release |
| BFF-001 | Testes de composição/DTO/erros; teste de arquitetura "sem regra de negócio"; autorização propagada; contrato de cliente |
| MSG-001 / BE-006 | NT-* verdes; chaos simples (kill do worker); DLQ e métricas; idempotência por propriedade |
| BE-007 | Seção 8 verde com stubs; webhooks fora de ordem/duplicados |
| BE-008 | LG-EX-* e LG-DL-* verdes; job retomável; evidência para o Privacy Gate; revisão de PRIV-001 |
| BE-009 | Agregações verificadas contra cálculo de referência em dados sintéticos; fusos/DST; consistência com edições/tombstones |
| IOS-001/AND-001 | Fluxos 1–2 com UI test; decodificação de fixtures; i18n; acessibilidade básica; sem tokens em armazenamento inseguro (Keychain/Keystore, teste) |
| IOS-002/AND-002 | Migrations locais testadas N-1→N; timeline offline; fluxos 3–5; snapshots |
| IOS-003/AND-003 | Cenários compartilhados de sync 100% verdes; falha de rede injetada; crash recovery (SY-ID-09); revogação com wipe local; sem perda de mutação pendente em kill do app |
| IOS-004/AND-004 | Paridade baseline offline × servidor; fluxos 7–8; push (payload e abertura autorizada); gráficos com dados sintéticos e fusos |
| IOS-005/AND-005 | StoreKit/Play Billing em teste; fluxos 9–11; exportar/excluir com wipe local; i18n completa |
| A11Y-001 | Auditoria manual VoiceOver/TalkBack dos fluxos críticos; defeitos A/AA zerados |
| PERF-001 | Orçamentos e relatório de carga; p95 < 500 ms nos endpoints interativos no perfil definido |
| SEC-001 | Pipeline com SAST/SCA/secrets/IaC; política de falha configurada |
| SECURITY-REVIEW-001 | Relatório; matriz IDOR e SY-RV como evidência; achados críticos/altos corrigidos e retestados |
| SRE-001 | Monitor sintético com conta canário; alertas de DLQ, falha de sync e latência |
| RELEASE-001 | Todos os gates da seção 15 verdes; relatório de readiness; nenhuma publicação em loja/produção sem autorização explícita |

---

## 15. Gates de CI e de release

### 15.1 Gate de PR (bloqueante, alvo ≤ 15 min)
1. Build backend/iOS/Android + restore com lockfiles; reprodutível.
2. Lint e formatação (`dotnet format`, analisadores Roslyn, SwiftLint, ktlint/detekt, Spectral para OpenAPI).
3. Unidade + propriedade (seeds fixas no PR; seeds aleatórias no nightly).
4. Testes de arquitetura (fronteiras ADR-0002).
5. Integração com Postgres via Testcontainers (subconjunto rápido: migrations, autorização, sync, outbox).
6. Contrato: validação OpenAPI, `oasdiff` (breaking change bloqueia sem aprovação), decodificação de fixtures em backend/iOS/Android.
7. Matriz IDOR (cobertura 100% dos endpoints com tenant).
8. Scans: secrets, SCA (vulnerabilidades altas/críticas bloqueiam), SAST, scanner de PII em fixtures e logs de teste.
9. Cobertura de diff (seção 13) e relatório de rastreabilidade §14.
10. Testes de unidade/snapshot de UI e Room/GRDB migrations nos clientes (UI instrumentada completa fica no nightly, com subconjunto "smoke" no PR quando o runner permitir).
11. i18n: strings ausentes/hardcoded, pseudolocalização.

### 15.2 Gate de merge em `main` / nightly (bloqueante para promover a release candidate)
1. Tudo do gate de PR com seeds aleatórias e mais iterações de propriedade (convergência de sync ≥ 10k).
2. docker compose completo (API + BFF + worker + Postgres + stubs); E2E de 2 dispositivos; XCUITest e Compose/Espresso instrumentados nos fluxos críticos.
3. Schemathesis (fuzz) e matriz de compatibilidade cliente N-1 × servidor N.
4. Mutation testing nas áreas críticas (score da seção 13; queda > 3 p.p. bloqueia).
5. Carga leve (regressão de p95 > 20% sinaliza), BenchmarkDotNet do motor.
6. DAST básico em staging; teste de restore de backup; teste de migração N-1→N.
7. Tendência de flaky e quarentena.

### 15.3 Gates de release (RELEASE-001; alinhados a `specification.md`)
| Gate | Evidência de testes exigida |
|---|---|
| API | Contrato congelado/versionado, provider/consumer verdes, sem breaking change |
| Qualidade funcional | Matriz §12 100% (escopo MVP) verde; fluxos críticos UI em iOS e Android; exploratório por charter concluído; sem bugs críticos/bloqueantes |
| Privacy | LG-EX/LG-DL e controles 9.3 verdes; inventário de dados mapeado; RIPD (PRIV-001) e revisão jurídica registrada (humano) |
| Security | SECURITY-REVIEW-001 concluído: matriz IDOR, SY-RV, AZ-* verdes; scans sem achados altos/críticos abertos |
| Accessibility | A11Y-001 concluída |
| Performance | PERF-001: orçamentos atendidos |
| Operational Readiness | SRE-001: SLOs, alertas, runbooks, monitor sintético; backup/restore testado; rollback ensaiado |
| Release | Todos acima; validação de especialista da tabela do motor de sono; autorização explícita para qualquer publicação em loja/produção |

### 15.4 Regras de governança dos gates
- Gate vermelho bloqueia merge; "re-run até passar" não é aceito sem diagnóstico de flaky registrado.
- Desabilitar/pular teste exige issue vinculada, dono e prazo; não é permitido para testes das seções 5, 6, 7 e 9.
- Alterações em golden tests/snapshots exigem aprovação explícita de revisor com a justificativa no PR.
- Exceções a gates de release são decididas pelo release-manager com registro de risco aceito pelo product owner.

---

## 16. Riscos de qualidade e pendências

| Risco / pendência | Impacto | Mitigação / dono |
|---|---|---|
| Sem `dotnet` SDK no container atual | Backend não compila/testa aqui | CLOUD-001 entrega CI; ou instalar SDK; nenhum resultado alegado até lá |
| ADR-0003 incompleta (janela de tombstone, formato do cursor, política edição×exclusão, skew de relógio) | Testes SY-TB-03, SY-CF-04/05 dependem de decisão | ARCH-001/003 fecham antes de BE-004b; testes parametrizados até lá |
| Tabelas de referência de sono sem validação por especialista | Previsão enganosa | Gate humano antes de release; golden tests protegem regressão, não correção clínica |
| Política de idade corrigida (RF-005), plano free×premium, política familiar de entitlement (RF-043), regra de exclusão com múltiplos cuidadores | Testes SE-CS-05, seção 8, LG-DL-02 dependem | REQ-001/PRIV-001 |
| Baseline de versões iOS/Android (RNF-016) | Define matriz de devices | Product owner/arquitetura mobile (ARCH-002) |
| Testes de push real e lojas exigem contas e dispositivos | Cobertura limitada a stubs/sandbox | Exploratório em staging com contas QA dedicadas |
| Critérios §14 nº 6 (diário) e nº 7 (IA) fora do MVP | Risco de esquecimento | Mantidos na matriz como pendentes V1/V2 |
| Revisão jurídica LGPD é humana | Não automatizável | Privacy Gate |

## 17. Próximos passos
1. Aprovar esta estratégia (REQ-001 deve publicar critérios de aceite por RF para estender a matriz).
2. CLOUD-001: pipeline com os gates 15.1/15.2; SEC-001: scans.
3. ARCH-003: entregar o harness `FakeDevice` e cenários JSON de sync reutilizados pelos clientes.
4. DB-001/API-001: inventário de dados pessoais e fixtures canônicas.
5. BE-005: perfis de "bebês sintéticos" e golden tests antes da implementação (TDD do motor).
