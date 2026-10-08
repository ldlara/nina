# Nina iOS (IOS-001 + IOS-002)

App SwiftUI (iOS 17+) + Swift Package local `NinaCore` (modelos do contrato, cliente HTTP, repositórios, ViewModels).
IOS-001: conta, bebê, cuidadores. **IOS-002: tracking offline-first** (sono com timer, mamada, mamadeira, fralda, bomba,
despertares, timeline por dia, fila de mutações). A seção "IOS-002" abaixo é a que você precisa ler primeiro.

> **ATENÇÃO: NADA DESTE CÓDIGO FOI COMPILADO NEM EXECUTADO.**
> Foi escrito em um container Linux sem Xcode e sem `swift` (`which swift` retornou vazio, nas duas tarefas). Nenhum `swift build`,
> `swift test`, `xcodegen` ou `xcodebuild` rodou. Espere erros de compilação pequenos na primeira vez (ver
> "Pontos com maior risco de ajuste"). Os testes foram escritos, mas **nunca foram executados**: "passam" é uma hipótese até você rodar.
> Nada do IOS-002 foi validado em compilador, simulador ou aparelho. Telas SwiftUI nunca foram vistas renderizadas.

## Escopo entregue

Fonte do IOS-001: `specs/ux-spec.md`, `specs/product-spec.md` (RF-001..007), `contracts/openapi.yaml` (congelado), `specs/api-spec.md`, ADR-0007/0009/0010.
Fonte do IOS-002: ver a seção "IOS-002". O contrato congelado atual é a **v1.0.1** (a v1.0.0 citada no IOS-001 só difere por esclarecimentos de
sync: ordem de resolução pelo servidor, `CLIENT_CLOCK_SKEW`, `ENTITY_ID_UNAVAILABLE`, cotas).

| Tela / fluxo | Onde | RF |
|---|---|---|
| Onboarding (boas-vindas, "Começar", "Já tenho conta") | `App/Nina/Features/Onboarding` | UX 4.1 E1 |
| Cadastro e login e-mail+senha; consentimentos separados e desmarcados (E3); Sign in with Apple; Google (stub); "Esqueci minha senha" (pedido) | `Features/Auth` | RF-001/002/003 |
| Confirmação de e-mail (código, reenvio com contagem) | `Features/Auth/EmailVerificationView.swift` | ADR-0009 |
| Criar/editar bebê (nome, nascimento, data prevista opcional, sexo opcional, fuso, declaração de responsável legal); idade exibida vem de `age`/`age_calculation` da API | `Features/Babies` | RF-004/005 |
| Lista de cuidadores, convidar, reenviar, cancelar, remover, sair, trocar papel | `Features/Caregivers` | RF-006 |
| Aceitar/recusar convite (prévia sem nome completo do bebê; link `nina://invite?token=`) | `Features/Caregivers/InvitationAcceptView.swift` | RF-007 |
| Mais: cuidadores, aceitar convite, tema (Sistema/Claro/Noite), sair da conta | `Features/More` | UX §8 |

Fora do escopo do IOS-001 (o IOS-002 entregou timer de sono e timeline; o resto continua fora): E5 rotina inicial, E6 primeiro registro guiado,
agenda/previsão, gráficos, paywall, **motor de sync de rede (`/sync/push`, `/sync/pull`, Onda 5)**, push, exportar/excluir conta, sessões e dispositivos.

## Estrutura

```
ios/
  project.yml                         # XcodeGen (o .xcodeproj NÃO é versionado)
  Packages/NinaCore/                  # Swift Package puro (sem UIKit/SwiftUI)
    Sources/NinaCore/
      Models/                         # Codable do OpenAPI: enums tolerantes, CivilDate, Baby, Membership, ...
      Models/Tracking/                # IOS-002: TrackedEvent, EventDraft, enums de tracking, JSONValue, validação
      Tracking/                       # IOS-002: fila, TrackingState, TrackingStore, EventRepository, Sync/ (wire + stub)
      Networking/                     # HTTPTransport (URLSession), Endpoint, APIClient (refresh single-flight)
      Storage/                        # TokenStore (Keychain / memória)
      Repositories/                   # Auth, Baby (+BabyCache), Caregiver, Consent: protocolo + implementação
      ViewModels/                     # @Observable @MainActor, testáveis sem UI
      Design/DesignTokens.swift       # tokens do Nina DS v0 + contraste WCAG
    Tests/NinaCoreTests/              # XCTest com URLProtocol stub (zero rede real)
  App/Nina/                           # app SwiftUI
    Theme/ Features/ (inclui Tracking/) Persistence/ Support/ Resources/ (pt-BR, en, es)
  App/NinaTests/                      # testes do app (SwiftData em memória, paridade de localização, tema, tracking)
```

## Como abrir no Mac (passo a passo)

Requisitos: macOS 14+, Xcode 15.4+ (iOS 17 SDK; foi escrito em Swift 5.9, modo de linguagem 5), [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
brew install xcodegen
cd ios
xcodegen generate          # cria Nina.xcodeproj (ignorado pelo git)
open Nina.xcodeproj
```

1. Selecione o esquema **Nina** e um simulador iPhone (iOS 17+).
2. Em *Signing & Capabilities* do target Nina, escolha seu Team (ou preencha `DEVELOPMENT_TEAM` em `project.yml` e rode `xcodegen generate` de novo).
   Simulador roda sem Team; **Sign in with Apple** só funciona com Team + capability (o entitlement `com.apple.developer.applesignin` já é gerado).
3. URL do backend: build setting `API_BASE_URL` em `project.yml` (padrão `https://api.staging.nina.app/v1`, **domínio placeholder do contrato**). Troque pelo BFF real.
4. Run (Cmd+R).

### Testes

```bash
# 1) Pacote NinaCore no host (macOS). Não precisa de simulador.
cd ios/Packages/NinaCore
swift build
swift test

# 2) Pacote NinaCore no simulador (cobre o caminho do Keychain real)
cd ios/Packages/NinaCore
xcodebuild test -scheme NinaCore -destination 'platform=iOS Simulator,name=iPhone 15'

# 3) Testes do app (SwiftData em memória, localização, tema)
cd ios
xcodegen generate
xcodebuild test -project Nina.xcodeproj -scheme Nina -destination 'platform=iOS Simulator,name=iPhone 15'
```

Se `iPhone 15` não existir na sua versão do Xcode, liste com `xcrun simctl list devices available` e troque o nome.
`KeychainTokenStore` tem teste que se auto-ignora (`XCTSkip`) se o host de `swift test` não tiver Keychain (erro -34018); rode o item 2.

## IOS-002: tracking offline-first

Fonte: `specs/ux-spec.md` (2.1 metas de toques, 4.2 timer de sono, 4.3 alimentação/fralda/bomba, 4.4 timeline, 4.12/4.13 estados),
`specs/product-spec.md` (RF-008..020, RF-046/047), `contracts/openapi.yaml` v1.0.1 (`/sync/push`, `/sync/pull`, `EventType`, `DiaperType`,
`FeedingType`, `MilkType`, `WakeEvent`, `night_awakenings`), `specs/api-spec.md` (AD-08, AD-33, seção 3), ADR-0003 e ADR-0009.

### O que foi entregue

| Item | Onde | Observação |
|---|---|---|
| Modelo local de eventos (sono, mamada `BREASTFEEDING`/`BOTTLE`, bomba, fralda, `WakeEvent`) | `NinaCore/Models/Tracking` | `TrackedEvent` achatado, enums tolerantes (`.unknown(raw)`), `nil` = não se aplica |
| Fila de mutações offline | `NinaCore/Tracking/MutationQueue.swift`, `TrackingState.swift` | UUID `mutation_id`, `client_created_at`, `base_version`, `device_id`, `entity_id`, estado (`pending`/`inFlight`/`rejected`), backoff, tombstone local |
| Persistência | `NinaCore/Tracking/TrackingStore.swift` (protocolo + memória), `App/Nina/Persistence/SwiftDataTrackingStore.swift` | SwiftData atrás de `TrackingStore`; evento + mutação gravados em um único `save()` |
| Repositório offline-first | `NinaCore/Tracking/EventRepository.swift` | escreve local primeiro, valida, papel (`ReadOnly` não escreve), nunca toca na rede |
| ViewModels `@Observable` | `NinaCore/ViewModels/TrackingViewModel`, `EventFormViewModel`, `BreastfeedingTimerViewModel` | relógio injetado, fuso do bebê, desfazer 8 s |
| `SyncEngine` (protocolo + **stub**), formato de rede do push, indicador de sync | `NinaCore/Tracking/Sync` | o motor real é a Onda 5 |
| Telas | `App/Nina/Features/Tracking` + `Babies/HomeView` (Hoje) + `Babies/MainView` (abas) + `More/MoreView` | ver abaixo |
| i18n pt-BR / en / es | `Resources/*.lproj` | 353 chaves idênticas nos 3 idiomas + 3 plurais novos no `.stringsdict` |
| Testes | `NinaCoreTests/*` (fila, repositório, wire, DST, VMs, timer) e `NinaTests/*` (SwiftData, i18n, formatação) | **nunca executados** |

Telas: **Hoje** (cartão "Agora" com cronômetro, resumo do dia, "Dormiu"/"Acordou" na base, "Foi antes…", "Registrar outro"), **timer de sono**
(corrigir início -5/-10/-15/-30 ou hora exata, tipo, cancelar com confirmação, tela "Sono registrado" com ajuste do fim), **ação rápida** (5 botões por uso
recente; fralda salva em 1 toque dentro da folha), **mamada** (escolhe o lado = inicia o timer, "Trocar de lado", "Terminar", ou manual), **mamadeira**
(volume com stepper de 10 ml iniciando no último volume; tipo de leite só aqui), **fralda** (enum), **bomba** (volume opcional), **timeline por dia**
(navegação de dia, chips Tudo/Sono/Comida/Fralda/Pumping, totais, vazio/erro/offline/sincronizando, marcador "Aguardando envio"), **detalhe/edição/exclusão**
(confirmação + "Desfazer" 8 s; despertares da sessão podem ser adicionados/excluídos) e **Mais > Sincronização** (indicador + contador de pendências).

### Como o offline-first funciona

1. Toda ação grava **primeiro** no banco local: o evento (`syncStatus = pending`, `version = 0`) e a mutação correspondente, atomicamente
   (`TrackingStore.create/update/delete`). Nada espera rede.
2. A fila é ordenada por `sequence` local. `nextBatch` devolve até 100 mutações em ordem, respeitando: backoff (`nextAttemptAt`), mutação anterior da
   mesma entidade ainda não enviável (não "fura a fila") e dependências (um `WAKE_EVENT` só sai depois do `CREATE` da sessão de sono).
3. `base_version` é **prevista**: versão confirmada + mutações da entidade já na fila (criar + parar timer = `CREATE` base 0, `UPDATE` base 1, igual ao
   exemplo do contrato). Quando uma mutação é confirmada, as seguintes da mesma entidade são re-baseadas (`MutationQueueState.markApplied`).
4. Idempotência: reenfileirar o mesmo `mutation_id` não duplica; o `mutation_id` nunca muda entre tentativas; mutação "em voo" que sobrou de queda do
   app volta a `pending` (`recoverInFlight`, chamado na abertura) e o reenvio é seguro (`DUPLICATE`).
5. Excluir: se a criação **nunca saiu do aparelho**, descarta evento + mutações (nenhum `DELETE`). Senão, tombstone local (some das telas) + `DELETE` retido por
   8 s (janela do "Desfazer"); o evento só é removido quando o servidor confirma. Excluir um sono esconde os despertares dele (o servidor apaga em cascata).
6. `TrackingState.apply(PushItemResult)` já sabe reconciliar `APPLIED`/`DUPLICATE` (versão, `synced`), `REJECTED` com `retryable=true` (backoff),
   `REJECTED` definitivo (fica visível como "Não enviado" até o usuário descartar) e `ENTITY_DELETED`/`ENTITY_NOT_FOUND` em exclusão (idempotente).
   `SyncWire.pushBody` monta o corpo do `POST /sync/push` e `PushResponse` decodifica a resposta (status/resolução desconhecidos não derrubam).
7. Sessão encerrada pelo servidor **não** apaga a fila (RF-001-A4). Logout explícito apaga eventos e fila (aparelho compartilhado; evita enviar dados de uma
   conta em outra) e a confirmação avisa quantos registros ainda não foram enviados.

### O que NÃO foi feito (de propósito ou por limite)

- **Motor de sync de rede (Onda 5).** `StubSyncEngine.syncNow` devolve `.notImplemented`: **nada é enviado nem recebido**; todo registro fica "Aguardando envio" e
  "Tentar agora" só zera o backoff. Faltam: push/pull reais, aplicar resultados do push com a entidade canônica (`entity`), pull por cursor (snapshot/delta/tombstone,
  `410 SYNC_CURSOR_EXPIRED` preservando a fila, `403 ACCESS_REVOKED` apagando o bebê), `Retry-After`/`429`, reconciliação de conflitos (sheet "Qual versão manter?"),
  `warnings` (`SLEEP_OVERLAP`, `OPEN_SLEEP_EXISTS`, `CLIENT_CLOCK_SKEW` com aviso de relógio do UX 4.13), `ENTITY_ID_UNAVAILABLE` (gerar novo UUID) e disparo por
  conectividade/background. Os pontos de encaixe estão documentados em `SyncEngine.swift` e `TrackingState.apply`.
- Pull do servidor não existe: eventos criados por outros cuidadores/aparelhos **não aparecem**; `night_awakenings` e autoria `created_by` só viriam do pull.
- Histórico de alterações do evento (`GET /events/{id}/history`, RF-020): mostrado apenas "última alteração por/às" local.
- Previsão/agenda, gráficos, unidades ml/oz (só ml), preferência de "último bebê", notificações.
- Aviso de sono sobreposto/ajuste do outro registro (UX 4.2) e conflito de timers entre aparelhos: dependem do servidor/sync.
- Dados de bebê excluídos por `ACCESS_REVOKED` ainda ficam em disco (a lista deixa de mostrar o bebê); a limpeza dos eventos dele é da Onda 5.

### Premissas e lacunas do IOS-002 (decidir/validar)

1. **Mamada por lado = uma sessão por lado.** O contrato não guarda duração por lado; "Trocar de lado" no timer grava uma `FEEDING_SESSION` por lado, cada uma
   com `end_at` (obrigatório, ADR-0010). O timer de mamada vive só no aparelho (UserDefaults), como o contrato pede, e sobrevive ao app fechado.
2. **Tipo de sono sugerido (soneca x noturno):** 19:00-05:59 no fuso do bebê = noturno, senão soneca (`SleepTypeRule`). É premissa: a rotina inicial (E5) não existe.
   Sempre editável.
3. **Dia de um sono que cruza a meia-noite:** conta no dia do **início** (D-13 segue em aberto). O dia de cada evento usa o `tz` gravado nele (RF-010-A8); fuso
   inválido cai no fuso do bebê. Totais só somam sonos **fechados**; o em andamento não entra (RF-010-A6).
4. **`night_awakenings`:** só exibido quando vier do servidor e for > 0; o app **não** deriva "0 = acompanhamento suficiente" (critério é parâmetro editável do servidor).
5. **Volume:** limites locais 1..5000 ml (teto do contrato). O limite efetivo (`limits.bottle_volume_ml_max` de `/reference-data`) ainda não é consumido.
6. **Horário futuro:** recusado além de 5 min (tolerância de relógio). Fim <= início é recusado (mamadeira aceita fim = início).
7. **Sem fuso/horário de verão no armazenamento:** instantes em UTC (segundos), fuso IANA do evento ao lado; dias de 23/25 h tratados por `DayCalendar` (testado com
   America/New_York e o dia sem meia-noite de São Paulo em 2018).
8. **Timer longo:** pergunta "ainda está dormindo?" após 6 h (constante em `TrackingViewModel.longSleepThreshold`; ainda não configurável).
9. **Desfazer exclusão** só vale enquanto o `DELETE` não foi enviado (retido 8 s). Desfazer a exclusão de um registro descartado (nunca enviado) o recria com o mesmo id,
   mas **não** recria os despertares dele.
10. **Fila e logout:** a fila é apagada no logout explícito. Se você preferir manter para reenvio após novo login (mesma conta), é uma decisão de produto/privacidade.
11. Papel desconhecido ou `READ_ONLY` não escreve (botões de registro somem e há aviso). Autor (`created_by`) local usa o nome do usuário atual; o servidor reatribui no sync.
12. O mesmo `ModelContainer` guarda cache de bebês **e** a fila. `LocalStore.makeContainer` ainda cai para memória se o arquivo estiver corrompido: nesse caso registros pendentes
    **somem em silêncio no próximo fechamento do app**. Antes de produção, trocar por falha explícita/recuperação e considerar `NSFileProtectionComplete`.

### Pontos com maior risco de compilação (IOS-002)

Além da lista do IOS-001 (abaixo), apostaria nestes:

- `@Observable @MainActor` com `init` que atribui muitas propriedades antes de usar `self` (`EventFormViewModel.init`, `TrackingViewModel.init`).
- Closures `[self] in` passadas a `write(undoMessage:_:)` (async, não escapante) retornando tuplas `(TrackedEvent, UndoAction.Kind?)` com membro implícito (`.created(...)`).
- `@ModelActor actor SwiftDataTrackingStore: TrackingStore`: assinaturas `async throws` sem `await` interno (avisos), `#Predicate` com `Optional<UUID>` (`sleepSessionId == target`),
  genérico `transact<Result>(eventIds:_:)` com closure `inout`, tupla nomeada devolvida por `TrackingState.changes(from:)`.
- `JSONValue` (`Codable` à mão) e atribuições `data["x"] = .instant(...)` em `[String: JSONValue]` (membro estático via `Optional`).
- `Date.FormatStyle`/`DateFormatter.setLocalizedDateFormatFromTemplate` e `DateComponentsFormatter` (apenas no app, `EventFormatting`).
- SwiftUI: `@Bindable var vm = viewModel` dentro de `body`, `Picker` com `Optional` + `.tag(Optional(x))`, `DatePicker(..., in: ...date)` com `.environment(\.timeZone, ...)`,
  `Text(LocalizedStringKey(...))`, `NavigationLink(value:)` + `navigationDestination(for: QuickAction.self)` dentro de sheet, `.onChange(of:initial:)` de 2 parâmetros,
  `TimelineView(.periodic(from:by:))` dentro de `@ViewBuilder`.
- Símbolos SF usados (`moon.zzz.fill`, `icloud.slash`, `exclamationmark.icloud`, `checkmark.icloud`, `arrow.triangle.2.circlepath`, `list.bullet.rectangle`): se algum não existir na sua versão, troque em `EventFormatting.symbol` / `TrackingComponents`.
- `Package.swift` não mudou; os testes novos usam `@testable import NinaCore` (já era o padrão).

### O que verificar no Mac (IOS-002)

Comandos (nesta ordem; pare no primeiro erro de compilação e me mande a saída):

```bash
cd ios/Packages/NinaCore
swift build 2>&1 | head -80          # compila só o core (sem UIKit/SwiftUI)
swift test 2>&1 | tail -80           # IOS-001 + IOS-002 (fila, repositório, wire, DST, ViewModels, timer)
swift test --filter MutationQueueTests    # idempotência de UUID, ordem, retry/backoff
swift test --filter EventRepositoryTests  # escrita local primeiro, base_version prevista, tombstone/desfazer
swift test --filter TimeSupportTests      # DST 23 h / 25 h e dia sem meia-noite

cd ..   # ios/
xcodegen generate
xcodebuild test -project Nina.xcodeproj -scheme Nina \
  -destination 'platform=iOS Simulator,name=iPhone 15' \
  -only-testing:NinaTests/SwiftDataTrackingStoreTests \
  -only-testing:NinaTests/TrackingLocalizationTests \
  -only-testing:NinaTests/EventFormattingTests
```

Checklist de comportamento (simulador, sem backend: o stub de sync não faz rede, então tudo fica "Aguardando envio"):

- [ ] Hoje: sem registros mostra o estado vazio; "Dormiu" (1 toque) inicia o cronômetro; fechar e reabrir o app continua contando do início persistido (RF-009-A4).
- [ ] "Acordou" para o timer, abre "Sono registrado" e o toast "Desfazer" some em 8 s; "Desfazer" reabre o timer.
- [ ] "Foi antes…" (-5/-10/-15/-30 e "Escolher hora…") no início e no fim; segundo "Dormiu" com timer aberto é recusado com aviso.
- [ ] Modo avião: tudo continua funcionando; faixa "Sem conexão" e contador "N registros aguardando envio"; força-fechar o app e reabrir mantém os registros e a fila.
- [ ] Mamada: Esquerdo/Direito inicia; "Trocar de lado"; "Terminar" cria uma sessão por lado com fim; manual exige lado e fim; sair da tela e voltar mantém o timer.
- [ ] Mamadeira: stepper de 10 ml começa no último volume; tipo de leite aparece **só** em mamadeira. Fralda: 1 toque no tipo dentro da folha. Bomba: volume opcional.
- [ ] Timeline: chips de filtro, dia anterior/seguinte, dia vazio ("Nenhum registro neste dia"), marcador "Aguardando envio", abrir detalhe, editar (hora/duração/tipo/observação),
      excluir com confirmação + "Desfazer", adicionar/excluir despertar em um sono.
- [ ] ReadOnly (convite como "somente leitura"): botões de registro somem e há aviso; não há como criar/editar/excluir.
- [ ] Mudar o fuso do aparelho/bebê e a data para o dia de horário de verão: dias de 23/25 h, "Hoje" e totais corretos; sono que cruza a meia-noite conta no dia do início.
- [ ] Mais > Sincronização mostra contagem; logout com pendências avisa o número e apaga fila e eventos.
- [ ] Acessibilidade: VoiceOver (linha de evento fala tipo, horário, duração, estado de envio; cronômetro fala em minutos, não por segundo; stepper de volume ajustável;
      "Desfazer" alcançável), Dynamic Type até AX5 (botões da base e chips não truncam; `AdaptiveStack` vira coluna), modo escuro/"Noite", "Reduzir movimento".
- [ ] Idiomas pt-BR/en/es: textos novos, plurais de "registro(s) aguardando envio", horários no formato do locale.
- [ ] Inspecionar o SQLite do app (ou `po` no depurador): `CachedMutation` com `sequence` crescente, `CachedEvent.payload` com enums desconhecidos preservados.

## Decisões

### Persistência local: SwiftData (por trás de `BabyCache`)

Escolha: **SwiftData**. Motivos:

- Alvo iOS 17+ (decisão do pedido), sem dependência de terceiros: menos superfície de supply chain num app que guarda dados de bebê (LGPD).
- O que persistimos agora é só um **cache de leitura** (bebês) para uso offline; o servidor é a fonte da verdade. Isso cabe bem no SwiftData.
- Guardamos o **JSON do contrato** (`Baby`) numa coluna `payload`, e não cada campo. Campos novos aditivos da API (regra de congelamento v1) não exigem migração de esquema no aparelho, e enums desconhecidos sobrevivem à ida e volta.
- `@ModelActor` dá contexto próprio fora da main thread, e a interface `BabyCache` (async) esconde o framework: testes do core usam `InMemoryBabyCache`.

Atualização IOS-002: a fila de mutações e os eventos entraram no mesmo SwiftData (`CachedEvent`, `CachedMutation`), com o JSON completo em `payload` e colunas só para consulta.
A lógica de fila/tombstone é código puro (`TrackingState`) testado fora do SwiftData; o SwiftData só carrega linhas, aplica a operação e grava a diferença num único `save()`.
Se o spike de sync da Onda 5 mostrar limites (transações, desempenho de `loadQueue` que hoje lê a fila inteira a cada operação, migração), a troca por GRDB continua restrita a `Persistence/`.

Trade-off honesto (IOS-001): a fila de mutações e o change log do ADR-0003 (`/sync/push`, `/sync/pull`, cursor, tombstones, ordenação) vão pedir consultas ordenadas, transações e migrações mais controladas. Se o spike de sync mostrar que SwiftData limita (predicados, migração, controle de transação, depuração do SQLite), **GRDB** é a alternativa; a troca fica restrita a `Persistence/` porque o resto só conhece `BabyCache`. Não adotei GRDB já agora para não puxar dependência por um cache simples.

Proteção em disco: o armazenamento do app usa a classe de proteção padrão do iOS (`CompleteUntilFirstUserAuthentication`). Considere `NSFileProtectionComplete` para o arquivo do SwiftData quando o sync existir. Ao fazer logout, o cache de bebês é apagado.

### Tokens: Keychain atrás de `TokenStore`

`KeychainTokenStore` guarda a sessão (JSON) em `kSecClassGenericPassword` com `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (não migra para outro aparelho nem para backup). Tokens nunca vão para UserDefaults, log ou URL.

### Cliente HTTP (`APIClient`)

- `Authorization: Bearer`; corpo JSON `snake_case`; instantes RFC 3339 em UTC com ou sem frações; datas civis `YYYY-MM-DD` num tipo próprio (`CivilDate`) para o nascimento nunca "andar" por fuso.
- Erros `application/problem+json`: o app localiza a mensagem pelo `code` estável (RF-050-A5), nunca por `title`/`detail`. Código desconhecido vira mensagem genérica.
- **Refresh single-flight**: o refresh token é de uso único (SEC-011); chamadas concorrentes esperam a mesma renovação. 401 com `TOKEN_EXPIRED` renova e repete **uma vez**.
- `SESSION_REVOKED` / `REFRESH_TOKEN_REUSED` / refresh recusado: apaga só os tokens e avisa o app (volta ao login com aviso). **Não apaga dados locais nem mutações pendentes** (RF-001-A4). Falha de rede durante o refresh **não** desloga.
- `Idempotency-Key` em criar bebê e convidar (chave estável entre tentativas da mesma tela); `If-Match: "<version>"` + `application/merge-patch+json` na edição do bebê; `due_date`/`sex` removidos viram `null` explícito (`Patch.clear`).

### Enums tolerantes (ADR-0009/0010)

Todo enum do contrato usado pelo app (`Role`, `InvitableRole`, `MembershipStatus`, `Sex`, `IdentityProvider`, `UserStatus`, `DevicePlatform`, `PurposeKey`, `AgeDisplayed`, ...) tem `.unknown(String)`: valor novo nunca derruba o decode nem descarta o registro, e é reenviado/guardado com o texto original. Papel desconhecido nunca ganha permissão de escrita.

### Idade (ADR-0009)

O app **não calcula nem persiste** idade corrigida. Exibe `age` (ou, se faltar, converte `age_calculation.chronological_days` em semanas/meses aproximados só para exibição). `corrected_days = null` significa "não se aplica": a UI não mostra idade corrigida (nunca trata como 0).

### Design system v0, acessibilidade, i18n

- Tokens de `ux-spec §6.1` em `DesignTokens.swift` (claro + "Noite"), mapeados para `Color` dinâmico. Teste de contraste no core (texto >= 4,5:1; eventos/foco >= 3:1). Resultado da conta feita à mão: todos os pares de texto/estado passam; `border/subtle` (1,3:1 claro / 1,5:1 escuro) **não** serve como borda de campo, então os campos usam `text/secondary` (>= 3:1).
- Texto em *text styles* (escala com Dynamic Type; `AdaptiveStack` vira coluna nos tamanhos de acessibilidade); botões quebram linha em vez de truncar; alvos >= 48 pt (primário 56).
- Erros sempre com ícone + texto; cor nunca é o único sinal. Cabeçalhos marcados para o rotor. Linhas de cuidadores expõem as ações como *custom actions* do VoiceOver. "Reduzir movimento" respeitado.
- Localização: `Localizable.strings` + `Localizable.stringsdict` (plural de idade) em **pt-BR (idioma de desenvolvimento)**, **en** e **es**; mesmas chaves nos três (hoje 353; teste de paridade em `LocalizationTests`). Textos de es/en são traduções minhas sem revisão nativa.
- Copy segue o ux-spec §11 (voz calma, sem alarmismo, erro = o que houve + o que fazer).

## Premissas e lacunas do contrato (decidir/validar)

1. **Sem endpoint de reenvio do código de e-mail.** O contrato só tem `POST /auth/register` (resposta uniforme 202). "Reenviar código" repete o `register` com o pedido guardado **só em memória** (inclui a senha até a verificação terminar). Se isso incomoda, peça ao backend um `POST /auth/email/resend`.
2. **Formato do código de verificação** não está no contrato; o app só exige >= 4 caracteres sem espaços e deixa o servidor validar.
3. **Política de senha (D-16) em aberto.** O app exige >= 8 caracteres como suposição; o servidor decide e os erros por campo (`errors[]`) são mostrados.
4. **Recuperação de senha:** só o pedido (`/auth/password/forgot`) está na UI. A tela de redefinição com token (`/auth/password/reset`) e o link/código de e-mail **não** foram feitos.
5. **Versões dos documentos legais** vêm de `GET /legal/documents`. Se o catálogo não trouxer documento para uma finalidade opcional (analytics/marketing) ou para `CHILD_DATA_GUARDIAN`, o app usa a versão da Política de Privacidade. **Confirmar com o backend/jurídico.** Sem versões carregadas, o cadastro é bloqueado (não inventa versão).
6. **Declaração de responsável legal:** antes de `POST /babies` o app registra `CHILD_DATA_GUARDIAN` em `POST /me/consents` (o contrato devolve `403 CONSENT_REQUIRED` sem isso).
7. **Login social em conta nova:** o app tenta sem `consents`; se vier `403 CONSENT_REQUIRED`, pede o aceite e reenvia a **mesma** credencial. Se o backend rejeitar reuso do `id_token`/`nonce`, será preciso reautenticar com o provedor.
8. **Google Sign-In não está integrado** (exige o SDK + `GoogleService-Info.plist`). Existe `StubSocialSignInProvider(provider: .google)`, exibido só em DEBUG, que responde "não configurado". Para ligar: adicionar o SDK, criar `GoogleSignInProvider: SocialSignInProvider` e trocar o stub em `AppContainer`.
9. **Botão da Apple** é um botão próprio (logo + "Continuar com Apple"), não o `SignInWithAppleButton` do sistema, para ficar atrás do protocolo e seguir o estilo do app. Valide contra as *Human Interface Guidelines* da Apple antes de publicar; se reprovar, troque pelo botão oficial.
10. **Fuso do bebê:** assume o do aparelho e mostra para confirmar (RF-004-A7). A lista é `TimeZone.knownTimeZoneIdentifiers` (sem busca).
11. **Foto do bebê** fora do MVP (D-09). **Último bebê selecionado** não persiste entre aberturas (UX §9 pede; falta guardar o id).
12. Ao **sair do bebê** (não-Owner), a tela de cuidadores não fecha sozinha; a lista de bebês é atualizada.
13. Proteção de captura no app-switcher e "ocultar nome em notificações" (UX §7) não implementados.

## Pontos com maior risco de ajuste na 1a compilação

Como nada compilou, estes são os pontos onde eu apostaria que o compilador pode reclamar (todos pequenos):

- `Enums.swift`: `init(rawValue:)` **não falível** satisfazendo `RawRepresentable` (`init?`), e `Codable` escrito à mão em enums com valor associado.
- `APIClient`: actor + `Task` de refresh; `try?` sobre `tokenStore.load()` (esperado achatar para `StoredSession?`).
- `URLSessionTransport` no ramo Linux (`FoundationNetworking`) nunca foi exercitado; no Mac usa `session.data(for:)`.
- ViewModels `@Observable @MainActor` com `@ObservationIgnored let` e inicializadores que atribuem propriedades observadas (`BabyFormViewModel.init`).
- `SwiftDataBabyCache`: macro `@ModelActor` + `#Predicate` com variável capturada (`remove(id:)`).
- `AppleSignInProvider`: `nonisolated` + `MainActor.assumeIsolated` nos delegates do `AuthenticationServices` (podem virar avisos/erros conforme a versão do SDK).
- Views: `Color(.token)` (init com membro implícito), `.accessibilityActions`, `Text("chave \(var)")` (a chave do catálogo precisa ser `chave %@`/`%lld`, já está assim nos `.strings`).
- Se o *strict concurrency* reclamar de `Sendable` (ex.: `URLSessionTransport`, closures de `AppContainer`), o projeto está em `SWIFT_STRICT_CONCURRENCY: minimal`.
- `project.yml`: sintaxe de `info`/`entitlements`/`schemes` do XcodeGen não foi validada; rode `xcodegen generate` e leia os avisos.

## O que precisa ser verificado manualmente

**Build e testes**
- [ ] `xcodegen generate` sem erro; projeto abre; esquema `Nina` roda no simulador.
- [ ] `swift test` em `Packages/NinaCore` (todos os testes passam; anote os que falharem).
- [ ] `xcodebuild test` do esquema `Nina` (SwiftData, localização, tema).
- [ ] Teste de Keychain do core roda (no simulador) e não é ignorado.

**Fluxos (com backend de homologação ou proxy/mock)**
- [ ] Onboarding aparece uma vez; "Já tenho conta" vai para login.
- [ ] Cadastro: 2 passos, aceite desmarcado por padrão, opcionais desmarcados, versões dos documentos vindas do servidor, voltar preserva o digitado.
- [ ] Confirmação de e-mail abre sessão; reenvio respeita a contagem; sessão só existe após a verificação.
- [ ] Login com senha errada mostra mensagem genérica; 429 mostra "muitas tentativas".
- [ ] Sign in with Apple em aparelho real (conta nova -> pede aceite; conta existente; cancelar sem erro); nonce confere no backend.
- [ ] Criar bebê (com e sem data prevista), editar (Owner), ver que Caregiver/ReadOnly não edita; 412 `VERSION_CONFLICT` com dois aparelhos.
- [ ] Idade: bebê com `corrected_days = null` não mostra idade corrigida; com valor mostra como secundária.
- [ ] Cuidadores: convidar, reenviar, cancelar, remover, sair, trocar papel; 409 `ALREADY_MEMBER`; revogação (`ACCESS_REVOKED`) limpa o cache do bebê.
- [ ] Convite: link `nina://invite?token=...` (`xcrun simctl openurl booted "nina://invite?token=abc"`), prévia sem nome completo, aceitar e recusar, token inválido = mensagem uniforme.
- [ ] Sessão: expirar o access token (esperar 15 min ou forçar 401) renova uma vez; revogar a sessão no servidor leva ao login com aviso e **não** apaga o cache.
- [ ] Modo avião: lista de bebês aparece do cache com faixa "sem conexão"; criar bebê falha com mensagem "seus dados estão salvos".

**Acessibilidade e aparência**
- [ ] VoiceOver nos fluxos: onboarding, cadastro, verificação, bebê, convidar cuidador (ordem de leitura, rótulos, erros anunciados, ações das linhas de cuidador via rotor "Ações").
- [ ] Dynamic Type até AX5: nenhum texto truncado em ação crítica; layouts viram coluna.
- [ ] Modo escuro e tema "Noite" manual; contraste visual dos estados; foco visível com teclado externo.
- [ ] "Reduzir movimento" ligado: sem animações.
- [ ] Controle por Voz / Controle por Chaves nos botões; alvos >= 44 pt.
- [ ] Rótulos do Menu "..." nas linhas de cuidadores (marcado oculto ao VoiceOver; ações estão como *custom actions*): confirme que Controle por Voz ainda alcança as ações.

**Idiomas**
- [ ] Rodar com pt-BR, en e es; plural de idade ("1 mês/2 meses", "1 semana/..."), datas e formatos corretos.
- [ ] Revisão nativa dos textos en/es.

**Segurança/privacidade**
- [ ] Tokens só no Keychain (inspecione o container do app: nada de token em UserDefaults/SwiftData/logs).
- [ ] Logout apaga tokens e cache de bebês.
- [ ] Nenhum log com e-mail, senha, token de convite ou nome do bebê.
