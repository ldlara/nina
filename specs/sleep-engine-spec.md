# Especificação do motor de previsão de sono (BE-005)

Status: **implementado como biblioteca pura; tabela de referência em RASCUNHO, exige validação de especialista clínico antes de qualquer release.**
Data: 2026-10-08 · Modelo: `rules-2026.10.1` · Tabela: `0.1.0-draft`
Fontes: ADR-0004 (motor), ADR-0009 (idade corrigida, `WakeEvent`, `night_awakenings`), `specs/product-spec.md` (RF-010..014, RB-001/002, RNF-014), `specs/domain-model.md` (seções 2.4, 6; INV-04/05/06), `specs/test-strategy.md` (seção 4), `contracts/openapi.yaml` v1.0.1 (congelado: `SleepPrediction`, `SleepPredictionResponse`, `SleepPreferences`) e `specs/ux-spec.md` (4.5, 11.2).

Código: `backend/src/Modules/Nina.SleepIntelligence` · Testes: `backend/tests/Nina.SleepIntelligence.Tests`.

---

## 1. Princípios e escopo

1. **Regras determinísticas, sem ML.** Mesma entrada + mesmo instante de referência = mesma saída, byte a byte (RF-011-A3, RF-012-A6). Toda a aritmética de estatística e confiança é **inteira** (sem ponto flutuante), para que iOS/Android reproduzam o baseline com o mesmo resultado (paridade, seção 10).
2. **Biblioteca pura.** Sem I/O, banco, HTTP nem estado. O único acesso a "agora" é o `TimeProvider` injetado (ou o parâmetro `asOf`). Ler o arquivo da tabela é a única operação de disco e fica na borda (`ReferenceTable.LoadFromFile`); o padrão é o arquivo embutido no assembly.
3. **Previsão nunca sobrescreve o real (RB-001, INV-05).** `SleepPredictionItem` é um tipo distinto de `SleepRecord`; o motor recebe o histórico como somente leitura, nunca o altera nem cria sessão. Testado por comparação antes/depois e por propriedade em 250 cenários aleatórios.
4. **Linguagem probabilística (RNF-014, RB-005, RB-013).** O motor emite **chaves de mensagem + parâmetros**, nunca prosa; os textos pt-BR de referência (`Data/messages.pt-BR.json`) passam por um verificador de linguagem (`LanguageGuard`) que barra diagnóstico, imperativos, julgamento, garantia e comparação com outras crianças. Faixas de horário, não horas exatas. A meta de sonecas é um alvo do cuidador, nunca um padrão que a criança "deveria" cumprir.
5. **Valores clínicos são dado, não código** (RF-011-A9). Nenhum número de wake window, nº de sonecas ou bedtime por idade está no C#.

Fora de escopo desta entrega (por instrução): endpoints, persistência, jobs/outbox de recálculo, notificações, outros módulos. O módulo Tracking chamará a API da seção 9.

## 2. Entradas e saídas

Entrada (`SleepPredictionRequest`): `BabyProfile(BirthDate, DueDate?, TimeZoneId)`, `History` (lista de `SleepRecord(Id, StartAt, EndAt?, Kind, TimeZoneId?)`), `Preferences?` (`SleepPreferences(TargetNapCount?, BedtimeFrom?, BedtimeTo?)`). Instantes em UTC; `TimeZoneId` do registro é o fuso vigente quando foi gravado (RB-014). Eventos excluídos (tombstone) **não** devem ser enviados.

Saída (`SleepPredictionResult`): equivale a `SleepPredictionResponse` do contrato mais metadados de reprodutibilidade.

| Campo | Significado |
|---|---|
| `Status` | `Available` / `Sleeping` / `InsufficientData` → `AVAILABLE` / `SLEEPING` / `INSUFFICIENT_DATA` |
| `DisclaimerKey` | sempre `prediction.estimate_not_diagnosis` |
| `Predictions[]` | `Kind` (`NEXT_NAP`/`BEDTIME`), `PredictedStart`, `PredictedEnd` (faixa), `Confidence`, `ConfidenceScore` (diagnóstico), `Explanation` (`Key` + `Params`), `BaselineOnly` |
| `StatusExplanation` | chave quando não há previsões (`sleeping_now`, `age_out_of_range`, `timezone_unknown`, `nap_target_reached`) |
| `ModelVersion`, `ReferenceTableVersion`, `ReferenceTableHash`, `ReferenceTableIsDraft` | rastreabilidade (RF-011-A7; teste 4.6 pede hash/versão por previsão) |
| `ComputedAt` | o instante de referência (relógio injetado) |
| `InputsFingerprint` | hash estável da entrada (independe da ordem dos eventos e de `asOf`); permite ao Tracking detectar recálculo sem mudança |
| `Age`, `Plan`, `DataQuality` | idade usada (`AgeCalculation`), estrutura de rotina (RF-014) e contagens de qualidade de dados (sem PII) |

**Confiança** (contrato v1.0.1 usa `BUILDING`/`FAIR`/`GOOD`): `ConfidenceLevel.Low` = `BUILDING` ("Em construção"), `Medium` = `FAIR` ("Razoável"), `High` = `GOOD` ("Boa"); mapeamento em `ContractNames`. `ConfidenceScore` (0–100) é só diagnóstico e **não** deve ser exibido (UX 4.5: nunca porcentagem na superfície principal).

**Mapeamento ao contrato (para o Tracking, sem alterar o OpenAPI):** `SleepPrediction.id/baby_id/version/inputs_version` são do Tracking (o motor não os conhece); `kind`, `predicted_start`, `predicted_end`, `confidence.level`, `explanation.{key,params}`, `baseline_only`, `model_version`, `computed_at` vêm direto do resultado.

## 3. Idade (ADR-0009, RIC-02)

`AgeCalculator.Calculate(birth, due, hojeLocal, opções)`:

- `chronological_days = max(0, hoje − birth)` (data local do bebê).
- Correção **somente** se `due` existe e `MinPrematurityDays ≤ (due − birth) ≤ MaxPrematurityDays` e `chronological_days ≤ MaxChronologicalDays`. Caso contrário `corrected_days = null`, `correction_applied = false` (nunca `0` para "não se aplica").
- `corrected_days = chronological_days − (due − birth)` (pode ser negativo antes da DPP; o motor usa `EffectiveDays = max(0, corrected ?? chronological)`).
- Idade corrigida **nunca é persistida**; `birth_date` e `due_date` não são alteradas.

Parâmetros provisórios (D-01 em aberto; no produto são editáveis no banco, ADR-0005): `MinPrematurityDays=1`, `MaxPrematurityDays=180` (acima disso a DPP é tratada como provável erro), `MaxChronologicalDays=730`. O fim da correção é **abrupto** nesta versão; a suavização entre faixas de referência (seção 4) atenua o efeito.

## 4. Tabela de referência (dado configurável)

Arquivo: `Data/sleep-reference-table.json`, embutido no assembly, carregado por `ReferenceTable` (também `Parse(string)` e `LoadFromFile`). Cabeçalho obrigatório e visível: **"RASCUNHO — requer validação por especialista clínico"**; `status = DRAFT_REQUIRES_CLINICAL_VALIDATION`; `provisional = true`; aviso em `_notice`. `IsDraft` é verdadeiro enquanto o cabeçalho contiver "RASCUNHO" e é devolvido em cada previsão.

Por faixa etária (dias de idade efetiva, `[de, até)`): `nap_wake_minutes` e `pre_bedtime_wake_minutes` (mín/típico/máx), `nap_count` (mín/típico/máx; típico `null` = sem número fixo), `nap_duration_minutes`, `bedtime_local` (mais cedo/típico/mais tarde; `null` na faixa neonatal). Valores atuais (provisórios e conservadores, **não são recomendação clínica**):

| Faixa | Wake window (mín/típ/máx min) | Antes do bedtime (típ) | Sonecas (mín/típ/máx) | Bedtime (cedo/típ/tarde) |
|---|---|---|---|---|
| 0–6 sem | 30/45/60 | 45 | 0/—/8 | sem horário fixo |
| 6 sem–3 m | 45/75/105 | 80 | 3/4/6 | 19:30/20:30/22:00 |
| 3–5 m | 75/105/135 | 120 | 3/4/5 | 19:00/20:00/21:30 |
| 5–8 m | 105/135/180 | 150 | 2/3/4 | 18:45/19:45/21:00 |
| 8–11 m | 135/165/210 | 180 | 2/2/3 | 18:30/19:30/20:45 |
| 11–16 m | 165/195/240 | 210 | 1/2/2 | 18:30/19:30/20:45 |
| 16–24 m | 195/240/300 | 255 | 1/1/2 | 18:45/19:45/21:00 |
| 24–36 m | 240/300/360 | 300 | 0/1/1 | 19:00/20:00/21:15 |

Também: `plausibility` (bedtime 17:30–23:30, máx. 8 sonecas — D-21), `transition_days` (14) e `max_age_days` (1095; acima disso → `INSUFFICIENT_DATA`, `age_out_of_range`).

**Validação ao carregar** (falha com `FormatException`): cabeçalho e `version` presentes; faixas contíguas a partir de 0, sem lacuna nem sobreposição; `0 < mín ≤ típico ≤ máx`; bedtime `cedo ≤ típico ≤ tarde`; `max_age_days` = fim da última faixa; horários `HH:mm`. O hash (SHA-256, 16 hex) do conteúdo acompanha cada previsão.

**Suavização entre faixas (SE-CS-06):** nos `transition_days/2` dias antes e depois de cada fronteira os valores são interpolados linearmente (50% na fronteira); o número típico de sonecas muda no ponto médio e o intervalo [mín, máx] passa a ser a união das duas faixas. O salto diário máximo de wake window típica fica ≤ 8 min (testado).

## 5. Pipeline de cálculo

### 5.1 Normalização e qualidade do histórico (SE-OUT-*)

Nunca lança por dado ruim; nunca altera a entrada; independe da ordem (I5). Em ordem:

1. Deduplica por `Id` (desempate determinístico); duplicatas contam em `IgnoredInvalid`.
2. Descarta: fim ≤ início (`IgnoredInvalid`); fim no futuro (> `asOf` + 5 min) ou início futuro em aberto (`IgnoredFuture`); duração < 5 min (`IgnoredTooShort`, SE-OUT-01); soneca > 300 min ou noite > 960 min (`IgnoredTooLong`, SE-OUT-02).
3. **Sessão em aberto** (RF-011-A8): se existe e tem ≤ 360 min (soneca) / 960 min (noite) → `Status = Sleeping`, sem previsões. Acima do limite é **timer esquecido**: ignorada e sinalizada (`OpenSessionIgnored`, SE-OUT-04). Com várias abertas vale a mais recente.
4. Ordena e **unifica**: sobreposição entre sessões do mesmo tipo → uma só (SE-OUT-05, dois cuidadores); sessões do mesmo tipo com intervalo < 10 min → uma só; sobreposição entre tipos diferentes → a posterior é recortada. Contagem em `MergedOverlaps`. A unificação evita dobrar a contagem de sonecas.

### 5.2 Wake windows observadas (RF-010-A3)

Para cada par consecutivo de sessões fechadas, wake window = `início(próxima) − fim(anterior)` em **minutos inteiros por diferença de instantes UTC** (correta em dias de 23/25 h). Classificação: janela que termina em soneca → "pré-soneca"; que termina em noite → "pré-bedtime". Só entram janelas da **janela de histórico** (14 dias).

**Outliers:** observação fora de `[40% do mín, 250% do máx]` da faixa etária é descartada e contada (`WakeWindowOutliers`) — isso elimina lacunas de registro (noite sem log gera "janela" de 15 h) e toques acidentais. Dias atípicos (vacina/doença) dentro dos limites pesam pouco pela mediana e ficam limitados pela folga (5.3).

### 5.3 Personalização (RF-011-A2)

Por série (pré-soneca, pré-bedtime, horário de dormir):

- `n < 5` observações (**D-20, provisório**) → só a referência (baseline), peso 0, `BaselineOnly = true`, confiança baixa.
- `n ≥ 5` → **mediana ponderada** com peso por recência de meia-vida de 4 dias (pesos inteiros 1000, 841, 707, 595, 500, …, mínimo 1). Empate exato entre dois valores → média arredondada para cima.
- Mistura: `valor = (w·mediana + (100−w)·baseline)/100`, com `w` crescendo linearmente de 50% (n=5) a 90% (n≥15).
- Limite de influência: o resultado é restringido a `[mín·85%, máx·115%]` da faixa etária (folga de 15%).
- Fronteira documentada baseline → personalizado: teste `n=4` vs `n=5` (SE-CS-03).

### 5.4 Próxima soneca (`NEXT_NAP`)

- **Âncora:** fim da última sessão fechada. Sem histórico, ou se terminou há mais que 250% do máximo da faixa (lacuna longa, SE-OUT-07), assume-se "acordado agora" (`anchor_assumed = true`); é uma limitação (seção 12).
- **Centro** = âncora + wake window personalizada. **Faixa** = centro ± meia-largura, com meia-largura = (máx−mín)/2 da tabela × {50% baixa, 35% média, 25% alta} (mín. 5 min). Início = `max(início, fim do último sono, agora + 1 min)`; fim ≥ início + 10 min; ambos arredondados **para cima** ao minuto. Janela já em curso começa "agora".
- **Número de sonecas:** `cap = meta do cuidador ?? min(esperado, máx da tabela)`; esperado = mediana de sonecas/dia dos últimos 7 dias (≥ 3 dias com dados, limitada a [mín, máx] da faixa) ou o típico da tabela. Se `sonecas hoje ≥ cap`, não há `NEXT_NAP` (SE-OUT-08). "Hoje" = dia local do bebê.
- **Corte tardio:** se `fim da faixa + duração típica da soneca + wake window mínima pré-bedtime` ultrapassa o limite superior do bedtime, a soneca **não** é prevista.

### 5.5 Bedtime (`BEDTIME`)

- **Faixa efetiva:** a preferência do cuidador (se válida) ou `[cedo, tarde]` da tabela. Faixa neonatal sem preferência → **sem `BEDTIME`**.
- **Horário-âncora:** mediana ponderada do início das últimas **noites no fuso vigente** (minutos desde 12:00 para evitar a virada de dia), misturada ao típico da tabela como em 5.3, limitada à faixa efetiva.
- **Sem mais sonecas hoje:** se a última sessão foi uma soneca de hoje, centro = ponto médio entre o horário-âncora e `fim da soneca + wake window pré-bedtime`. **Com soneca prevista:** o centro é no mínimo `fim da soneca + duração + wake window mínima`. O centro é então limitado à faixa efetiva, e a faixa prevista fica dentro da preferência.
- **Data do bedtime:** a data local da âncora (fim do último sono, ou `asOf`); se o horário já passou do fim do último sono, o dia seguinte. Hora local inexistente (início de horário de verão) avança até a primeira hora válida; hora repetida usa a primeira ocorrência (`TimeZones.ToUtc`).
- **Restrições físicas vencem a preferência:** a previsão nunca fica antes de `agora + 1 min`, do fim do último sono nem do fim da soneca prevista. Ex.: se são 21:30 e a faixa preferida é 19:00–20:00, a faixa prevista começa em 21:31.
- Ordem: previsões saem ordenadas por início; `NEXT_NAP.start < BEDTIME.start` quando ambas existem.

## 6. Confiança e explicação (RF-013)

Entradas: nº de observações, dias distintos, dispersão relativa (desvio absoluto **médio** / mediana, em %; para bedtime, desvio em minutos ÷ 120), idade da observação mais recente. Pontos (inteiros): volume `min(100, n·100/15)`·40% + consistência `clamp(100−(dispersão−10)·4)`·30% + cobertura `min(100, dias·100/5)`·15% + recência (≤1 dia 100, ≤3 dias 70, ≤7 dias 40, senão 0)·15%.

- **Baixa (`BUILDING`)**: baseline (n<5); ou dispersão > 25% ("irregular"); ou observação mais recente com > 7 dias; ou score < 45.
- **Alta (`GOOD`)**: score ≥ 75 **e** n ≥ 12 **e** ≥ 4 dias **e** dispersão ≤ 15% **e** observação mais recente ≤ 3 dias.
- **Média (`FAIR`)**: demais casos (observação com 4–7 dias limita a média).
- Monotônica não decrescente com o volume, ceteris paribus (testado com 15 subconjuntos crescentes).

Chaves de mensagem (`MessageKeys`; textos pt-BR de referência em `Data/messages.pt-BR.json`, EN/ES a cargo dos clientes): `prediction.estimate_not_diagnosis`, `prediction.explain.age_only`, `.history_and_age`, `.irregular_history`, `.bedtime_age_only`, `.bedtime_history_and_age`, `.bedtime_preference`, `.age_out_of_range`, `.timezone_unknown`, `.nap_target_reached`, `.sleeping_now`. Parâmetros (chaves em ordem estável; valores `int`/`string`/`bool`): `age_months` (meses completos da idade efetiva), `age_basis` (`CHRONOLOGICAL`/`CORRECTED`), `typical_wake_minutes`, `days_used`, `records_used`, `anchor_assumed`, `naps_today`, `typical_bedtime_local` (`HH:mm`), `preference_range_applied`. O exemplo do contrato (`history_and_age` com `age_months`, `days_used`, `records_used`, `typical_wake_minutes`) é atendido.

## 7. Preferências (RF-014)

`SleepPreferencesValidator.Validate` devolve códigos estáveis para o Tracking mapear a `400`: `SLEEP_PREF_NAP_COUNT_OUT_OF_RANGE` (fora de 0..8), `SLEEP_PREF_BEDTIME_INCOMPLETE` (só um dos limites), `SLEEP_PREF_BEDTIME_ORDER` (início ≥ fim; **sem virada de meia-noite**), `SLEEP_PREF_BEDTIME_IMPLAUSIBLE` (fora de 17:30–23:30, D-21 provisório), `SLEEP_PREF_BEDTIME_TOO_NARROW` (< 15 min). Preferências nulas/removidas devolvem o comportamento padrão por idade (RF-014-A4). A autorização (ReadOnly não altera) é do Tracking.

## 8. Fuso horário, DST e dia (RB-014, SE-TZ-*)

- Verdade em **UTC**; durações por diferença de instantes (dias de 23/25 h corretos); resultado sempre em UTC (`Offset 0`), sem segundos.
- Cada registro carrega seu fuso; o fuso do perfil do bebê é o "vigente". Nomes IANA legados/renomeados resolvem por alias (`America/Buenos_Aires`, `Asia/Calcutta`, `Europe/Kiev`, …); fuso desconhecido → `INSUFFICIENT_DATA` com `timezone_unknown` (não exceção).
- **Troca de fuso (decisão provisória, "adaptação imediata"):** wake windows (duração) continuam valendo; o **horário de dormir** só usa noites registradas no fuso vigente (não transfere hábito do fuso antigo). Sem noites no novo fuso o bedtime volta ao baseline da idade (`DataQuality.TimezoneChangedInHistory`). A alternativa "gradual" depende de decisão de produto/especialista.
- **Dia / meia-noite (D-13, provisório):** uma sessão pertence ao **dia local do seu início**, no fuso do registro (RF-010-A8). Noite que cruza a meia-noite conta inteira no dia em que começou (`SleepMetrics.DailyTotals`). Dois cuidadores em fusos diferentes registrando o mesmo sono são unificados (mesmo instante UTC).
- Offsets fracionários (Asia/Kolkata +5:30, Australia/Lord_Howe 30 min de DST) cobertos por teste.

## 9. API para o módulo Tracking

```csharp
services.AddSleepIntelligenceModule();             // ISleepPredictionEngine (singleton), ReferenceTable, SleepEngineOptions, TimeProvider
ISleepPredictionEngine.Predict(request)            // usa o TimeProvider injetado
ISleepPredictionEngine.Predict(request, asOf)      // recálculo reprodutível
SleepPreferencesValidator.Validate(prefs, table)   // 400 do PUT /sleep-preferences
AgeCalculator.Calculate(birth, due, hojeLocal)     // AgeCalculation do contrato
SleepMetrics.WakeWindows / DailyTotals / NightAwakenings / IsAwakeningTrackingActive
ContractNames.Of(...)                              // BUILDING|FAIR|GOOD, NEXT_NAP|BEDTIME, AVAILABLE|SLEEPING|INSUFFICIENT_DATA
```

**Recálculo (RF-012, INV-06):** o Tracking reconstrói o histórico recente (últimos ≥ 14 dias, sem tombstones) a cada criação/edição/exclusão de sono, alteração de nascimento/DPP/fuso/preferências, ou passagem de faixa etária, e chama `Predict`. Por ser função pura do estado final, duas mutações em sequência rápida resultam no cálculo sobre o estado final (RF-012-A4); reaplicar é idempotente (`InputsFingerprint` igual ⇒ nada a substituir/reagendar, SE-ED-08). O motor **não** persiste nem emite eventos; `SleepPredictionRecalculated` e a substituição da previsão vigente são do Tracking. Previsão não ocorrida some quando o real é registrado ou a janela passa, sem rótulo de erro (UX 4.5).

**Despertares noturnos (ADR-0009):** `SleepMetrics.NightAwakenings(noite, wakeEvents, trackingActive)` → `null` (soneca, noite aberta/curta ou sem acompanhamento), `0` (acompanhamento suficiente, nenhum) ou `N`. Critério de "suficiente" provisório e parametrizável (`NightAwakeningsPolicy`): noite fechada ≥ 180 min **e** ao menos um `WakeEvent` nas últimas 14 noites; despertares < 60 s não contam.

## 10. Parâmetros (resumo)

Todos em `SleepEngineOptions` (valores provisórios; mudar qualquer um exige novo `ModelVersion`, regenerar e rever os golden tests): janela 14 dias; mín. 5 e pleno 15 observações; peso pessoal 50–90%; folga 15%; outlier 40%–250%; sono válido 5–300 min (soneca) e até 960 (noite); timer esquecido 360/960 min; unificação < 10 min; tolerância de futuro 5 min; antecedência 1 min; faixa mínima 10 min; limiares de confiança 75/45, irregular > 25%, alta ≤ 15%, obsoleto 3/7 dias; larguras 50/35/25%.

## 11. Testes e evidências

`backend/tests/Nina.SleepIntelligence.Tests` (xUnit, sem I/O, `FakeTimeProvider`):

| Grupo | Cobertura |
|---|---|
| Tabela | cabeçalho de rascunho; contiguidade; mín≤típ≤máx; suavização sem saltos; hash; rejeição de tabelas inválidas; troca por tabela validada sem tocar no código |
| Cold start SE-CS-01..07 | sem histórico, 1–2 sonecas, fronteira n−1/n, neonatal, prematuro (idade corrigida), fronteira de faixa, só noturno |
| Outliers SE-OUT-01..08 | 8 s/0 min, 14 h/negativa, dia atípico, timer esquecido, duplicidade, caos, lacuna, teto de sonecas |
| Timezone SE-TZ-01..08 | UTC×fuso, viagem, DST (23/25 h, hora inexistente/repetida), meia-noite, dois fusos, offsets fracionários, regrupamento, aliases |
| Edição retroativa SE-ED-01..08 | editar, excluir, inserir, sobreposição, evento antigo, A→B→A, ordem, idempotência, relógio injetado |
| Preferências | validação (códigos), faixa respeitada, meta de sonecas, remoção, corte tardio, restrição física vence |
| Propriedades (250 sementes × 8) | I1 determinismo e I5 ordem; I2 imutabilidade da entrada; I3 previsão > agora, ≥ fim do último sono, ordenada, em UTC; I4/I6 confiança e explicação válidas e sem termos proibidos; consistência de status; estabilidade (±1 min ⇒ ≤ 20 min); fuzz sem exceções; parâmetros primitivos estáveis |
| Golden (10 perfis A–J) | regular, irregular, 1 soneca, prematuro, mudança de fase, esparso, recém-nascido, DST, troca de fuso, histórico longo com preferências |
| Linguagem | todas as chaves têm texto; guarda barra termos proibidos e aceita frases factuais; mapeamento ao contrato |

**Golden tests:** `tests/Nina.SleepIntelligence.Tests/Golden/*.json` guardam `request` + `expected`. Qualquer divergência falha o teste. Mudar a saída exige aprovação explícita: regenerar com `NINA_UPDATE_GOLDEN=1 dotnet test --filter FullyQualifiedName~GoldenTests` e justificar no PR (test-strategy 15.4). **Paridade cliente/servidor:** os mesmos arquivos (hoje no projeto de testes) devem ser promovidos a `/contracts/fixtures/sleep-engine` para que iOS/Android comparem o baseline offline (cold start: perfis F e G) com tolerância configurada; o motor usa só aritmética inteira para tornar a paridade exata.

## 12. Limitações conhecidas

- **Tabela é rascunho:** valores plausíveis e conservadores, **sem validação clínica**. Os golden tests protegem regressão, não correção clínica.
- Sem âncora real (sem histórico ou lacuna longa) a próxima soneca é estimada a partir de "agora"; pode induzir leitura de "em X horas". O texto da explicação cita a idade, mas a UX deve avaliar ocultar `NEXT_NAP` nesse caso.
- Uma só wake window típica por faixa para todas as sonecas do dia (sem distinguir a 1ª janela da manhã das seguintes) e sem modelar duração real das sonecas; a duração típica da tabela só decide o corte tardio.
- Bedtime depois da meia-noite local (ex.: bebê acordado à 00:30) cai no fallback "agora"/dia seguinte; não há modelo de noites fragmentadas além das sessões registradas.
- Dispersão alta é tratada como "irregular" sem investigar causa (e sem nunca sugerir causa clínica).
- Não considera sinais externos (doença, vacinas, fases de desenvolvimento — fora do MVP), nem alimentação.
- Desempenho: ~O(n log n) no tamanho do histórico; não foi medido com BenchmarkDotNet (pendente, BE-005 pede sem regressão de microbenchmark).
- Cobertura de linhas/mutation não medidas nesta entrega (metas: ≥ 95% linhas, mutation ≥ 80%).

## 13. Pontos que exigem validação clínica / decisão de produto

1. **Toda a tabela** da seção 4 (wake windows, nº de sonecas, duração de sonecas, janelas de bedtime) e os limites de plausibilidade — gate humano obrigatório antes de release (ADR-0004, BE-005). Ao aprovar: substituir o JSON, trocar o cabeçalho e registrar a ata como evidência.
2. **D-01 — idade corrigida:** critério de prematuridade, fim da correção (abrupto × gradual), opt-out do cuidador, uso desde o primeiro dia.
3. **D-20 — volume mínimo** de histórico (5 observações), pesos, meia-vida de 4 dias, janela de 14 dias.
4. **D-21 — limites plausíveis** de bedtime (17:30–23:30) e de sonecas (0–8).
5. **D-13 — atribuição de dia** de sessões que cruzam a meia-noite e corte do "dia".
6. **Troca de fuso:** adaptação imediata (atual) × gradual.
7. **Limiares de confiança** e rótulos (Em construção/Razoável/Boa) — engenharia + especialista + UX (ux-spec, perguntas em aberto: confiança).
8. Critério de **"acompanhamento suficiente"** de despertares noturnos (ADR-0009).
9. **Textos de explicação e aviso de saúde** (revisão de UX, jurídico e especialista; EN/ES).
10. Se `NEXT_NAP` sem âncora real deve ser exibido ou omitido na UI.
