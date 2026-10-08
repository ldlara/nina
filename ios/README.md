# Nina iOS (IOS-001)

App SwiftUI (iOS 17+) + Swift Package local `NinaCore` (modelos do contrato, cliente HTTP, repositórios, ViewModels).

> **ATENÇÃO: NADA DESTE CÓDIGO FOI COMPILADO NEM EXECUTADO.**
> Foi escrito em um container Linux sem Xcode e sem `swift` (`which swift` retornou vazio). Nenhum `swift build`,
> `swift test`, `xcodegen` ou `xcodebuild` rodou. Espere erros de compilação pequenos na primeira vez (ver
> "Pontos com maior risco de ajuste"). Os testes foram escritos, mas **nunca foram executados**: "passam" é uma hipótese até você rodar.

## Escopo entregue

Fonte: `specs/ux-spec.md`, `specs/product-spec.md` (RF-001..007), `contracts/openapi.yaml` v1.0.0 (congelado), `specs/api-spec.md`, ADR-0007/0009/0010.

| Tela / fluxo | Onde | RF |
|---|---|---|
| Onboarding (boas-vindas, "Começar", "Já tenho conta") | `App/Nina/Features/Onboarding` | UX 4.1 E1 |
| Cadastro e login e-mail+senha; consentimentos separados e desmarcados (E3); Sign in with Apple; Google (stub); "Esqueci minha senha" (pedido) | `Features/Auth` | RF-001/002/003 |
| Confirmação de e-mail (código, reenvio com contagem) | `Features/Auth/EmailVerificationView.swift` | ADR-0009 |
| Criar/editar bebê (nome, nascimento, data prevista opcional, sexo opcional, fuso, declaração de responsável legal); idade exibida vem de `age`/`age_calculation` da API | `Features/Babies` | RF-004/005 |
| Lista de cuidadores, convidar, reenviar, cancelar, remover, sair, trocar papel | `Features/Caregivers` | RF-006 |
| Aceitar/recusar convite (prévia sem nome completo do bebê; link `nina://invite?token=`) | `Features/Caregivers/InvitationAcceptView.swift` | RF-007 |
| Mais: cuidadores, aceitar convite, tema (Sistema/Claro/Noite), sair da conta | `Features/More` | UX §8 |

Fora do escopo desta tarefa (não implementado): E5 rotina inicial, E6 primeiro registro, timer de sono, timeline, agenda, gráficos, paywall,
sync (`/sync/push`, `/sync/pull`), push, exportar/excluir conta, sessões e dispositivos.

## Estrutura

```
ios/
  project.yml                         # XcodeGen (o .xcodeproj NÃO é versionado)
  Packages/NinaCore/                  # Swift Package puro (sem UIKit/SwiftUI)
    Sources/NinaCore/
      Models/                         # Codable do OpenAPI: enums tolerantes, CivilDate, Baby, Membership, ...
      Networking/                     # HTTPTransport (URLSession), Endpoint, APIClient (refresh single-flight)
      Storage/                        # TokenStore (Keychain / memória)
      Repositories/                   # Auth, Baby (+BabyCache), Caregiver, Consent: protocolo + implementação
      ViewModels/                     # @Observable @MainActor, testáveis sem UI
      Design/DesignTokens.swift       # tokens do Nina DS v0 + contraste WCAG
    Tests/NinaCoreTests/              # XCTest com URLProtocol stub (zero rede real)
  App/Nina/                           # app SwiftUI
    Theme/ Features/ Persistence/ Support/ Resources/ (pt-BR, en, es)
  App/NinaTests/                      # testes do app (SwiftData em memória, paridade de localização, tema)
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

## Decisões

### Persistência local: SwiftData (por trás de `BabyCache`)

Escolha: **SwiftData**. Motivos:

- Alvo iOS 17+ (decisão do pedido), sem dependência de terceiros: menos superfície de supply chain num app que guarda dados de bebê (LGPD).
- O que persistimos agora é só um **cache de leitura** (bebês) para uso offline; o servidor é a fonte da verdade. Isso cabe bem no SwiftData.
- Guardamos o **JSON do contrato** (`Baby`) numa coluna `payload`, e não cada campo. Campos novos aditivos da API (regra de congelamento v1) não exigem migração de esquema no aparelho, e enums desconhecidos sobrevivem à ida e volta.
- `@ModelActor` dá contexto próprio fora da main thread, e a interface `BabyCache` (async) esconde o framework: testes do core usam `InMemoryBabyCache`.

Trade-off honesto: a fila de mutações e o change log do ADR-0003 (`/sync/push`, `/sync/pull`, cursor, tombstones, ordenação) vão pedir consultas ordenadas, transações e migrações mais controladas. Se o spike de sync mostrar que SwiftData limita (predicados, migração, controle de transação, depuração do SQLite), **GRDB** é a alternativa; a troca fica restrita a `Persistence/` porque o resto só conhece `BabyCache`. Não adotei GRDB já agora para não puxar dependência por um cache simples.

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
- Localização: `Localizable.strings` + `Localizable.stringsdict` (plural de idade) em **pt-BR (idioma de desenvolvimento)**, **en** e **es**; mesmas 174 chaves nos três (teste de paridade em `LocalizationTests`). Textos de es/en são traduções minhas sem revisão nativa.
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
