# Nina Android (AND-001)

App Android nativo do Nina em **Kotlin + Jetpack Compose (Material 3)**. Esta entrega cobre a fundação do app e os
fluxos de **conta, bebê e cuidadores** (RF-001..007) contra o contrato `contracts/openapi.yaml` **v1.0.0** (congelado).

> Nenhum segredo nem URL real está no repositório. Os testes usam **MockWebServer** (nenhuma chamada de rede real).

## Requisitos
- JDK 17+ (testado com JDK 21), Android SDK com `platforms;android-35` e `build-tools;35.0.0`
  (`export ANDROID_HOME=/opt/android` ou `local.properties` com `sdk.dir=...`, que é ignorado pelo git).
- Gradle: use o wrapper (`./gradlew`, Gradle 8.14.3). Acesso a Google Maven, Maven Central e Gradle Plugin Portal.

## Comandos
```bash
cd android
export ANDROID_HOME=/opt/android
./gradlew assembleDebug testDebugUnitTest lintDebug
```
Se o Maven Central responder `429 Too Many Requests` (limite de taxa de proxy/CDN), repita o comando: o Gradle
guarda em cache o que já baixou. `--no-parallel` reduz o número de requisições simultâneas.

## Configuração (sem valores reais)
Propriedades Gradle (`android/gradle.properties`, `~/.gradle/gradle.properties` ou `-Pnome=valor`):

| Propriedade | Padrão no repositório | Uso |
|---|---|---|
| `nina.apiBaseUrl` | `https://api.nina.invalid/v1/` (TLD reservado `.invalid`, nunca resolve) | URL base do BFF, com `/v1/` e barra final. Release exige `https://`. |
| `nina.googleWebClientId` | vazio | Client ID web do Google, para a implementação real do provedor social. |

Ambas viram `BuildConfig.API_BASE_URL` e `BuildConfig.GOOGLE_WEB_CLIENT_ID`. Para apontar a um ambiente:
`./gradlew assembleDebug -Pnina.apiBaseUrl=https://api.staging.exemplo/v1/`.

## Arquitetura (limpa, modular simples, módulo único `:app`)
```
app/src/main/java/app/nina
  domain/            modelos puros, Outcome/AppError, interfaces de repositório e SocialAuthProvider
    model/             Enums.kt (enums do contrato com serializer tolerante), Models.kt, Outcome.kt
    repository/        AuthRepository, BabyRepository, CaregiverRepository
    auth/              SocialAuthProvider + StubSocialAuthProvider
  data/
    remote/            NinaApi (Retrofit), dto/Dtos.kt (modelos do OpenAPI à mão), NinaJson, interceptors, refresh
    local/             Room: BabyEntity, MembershipEntity, DAOs, NinaDatabase
    session/           TokenStore (EncryptedSharedPreferences), SessionManager, AppPrefs
    repository/        implementações + mappers DTO/entidade/domínio
  di/AppContainer      injeção manual (sem framework)
  ui/                  tema Nina DS v0, componentes, navegação, telas + ViewModels
```
Fluxo de dados: UI (Compose) -> ViewModel (StateFlow) -> interface de repositório -> API (Retrofit/OkHttp +
kotlinx.serialization) e cache Room. Listas de bebês/cuidadores são lidas do Room (offline-first para leitura);
escritas de perfil/cuidadores exigem conexão (AD-09, UX 4.13).

### Contrato e tolerância (ADR-0009/0010)
- Modelos em `snake_case` por `@SerialName`; **todos os enums em MAIÚSCULAS**.
- Cada enum do contrato usa `TolerantEnumSerializer`: valor desconhecido vira `UNRECOGNIZED` (nunca falha nem
  descarta o registro); `UNRECOGNIZED` **nunca é enviado** ao servidor. `Json` ignora campos desconhecidos.
- `corrected_days = null` significa "não se aplica" (nunca 0). A idade (`age_calculation`) **vem da API**; o app não a
  calcula nem a trata como dado do bebê (o Room guarda só o último cálculo recebido para exibição offline).
- Erros RFC 7807: o app localiza a mensagem por `code` (`ui/ErrorMessages.kt`); `title`/`detail` não são exibidos.
- Edição do bebê: `PATCH` `application/merge-patch+json` só com campos alterados (`due_date` removida = `null`
  explícito), `If-Match: "<version>"`; em `412 VERSION_CONFLICT` o bebê é recarregado.
- `Idempotency-Key` (UUID) em `POST /auth/register`, `POST /babies`, `POST /babies/{id}/invitations`.

### Segurança
- Tokens (access/refresh) somente em **EncryptedSharedPreferences** (AES-256, chave no Android Keystore);
  arquivo corrompido é descartado e o usuário entra de novo. Nada de token em log, URL ou Room.
- `allowBackup=false` + regras de extração de dados que excluem tudo; `usesCleartextTraffic=false`.
- Refresh de uso único: `TokenAuthenticator` rotaciona o par em 401 e repete; refresh recusado encerra a sessão local
  **sem apagar** dados locais (RF-001-A4). Logout voluntário limpa tokens e o cache Room.

## Telas
Onboarding (3 passos, sem depender de gesto) -> Conta (cadastro/login, Termos+Política obrigatórios, opcionais
desmarcados) -> Confirmação de e-mail (código, reenvio com contagem) -> Lista de bebês -> Criar/editar bebê
(nascimento e data prevista separados; seção **Idade** exibindo `age_calculation` da API; só o Owner edita) ->
Cuidadores (lista, convidar, reenviar, cancelar, mudar papel, remover, sair) -> "Tenho um convite" (prévia,
aceitar, recusar).

Login com **Google** e **Apple** são botões que chamam `SocialAuthProvider`. A implementação atual
(`StubSocialAuthProvider`) devolve "indisponível" e a UI mostra uma mensagem; não fabrica tokens. A real será outra tarefa.

## Design system, i18n e acessibilidade
- Tema original "crepúsculo e leite morno" (ux-spec §6, tokens v0), claro e **modo escuro "Noite"** pelo tema do sistema.
- Strings em `res/values` (**pt-BR, primário**), `values-en`, `values-es`; plurais para idade; `locales_config.xml`.
- A11y: alvos >= 48 dp (botões primários 56 dp), rótulos persistentes nos campos, erros anunciados (live region) com
  ícone+texto, checkboxes/rádios com a linha inteira tocável e papel semântico, títulos como `heading`, cartões
  agrupados para o TalkBack, layouts roláveis (fonte até 200%), formulários sem timeout, ações sem depender de gesto.

## Testes (`app/src/test`)
107 testes unitários JVM: serialização tolerante (enums e campos desconhecidos, snake_case, nulos), repositórios sobre
MockWebServer (cabeçalhos, corpos, cache, merge-patch, 412, ACCESS_REVOKED), `TokenAuthenticator` (refresh/expirar),
ViewModels (auth, confirmação de e-mail com contagem, bebê, cuidadores, convite) e mapeamento de erros por `code`.
Fora do escopo desta entrega: testes instrumentados/Compose UI e DAOs Room em dispositivo; auditoria de contraste e
teste manual com TalkBack (ux-spec §10.2) ainda pendentes.

## Pendências conhecidas
- Provedores Google/Apple reais; deep link de convite (hoje o convidado cola código/link); recuperação de senha (RF-002)
  e sessões/dispositivos (RF-054) não têm tela; escolha de fuso do bebê (usa o do aparelho); foto do bebê.
- Reenvio do código de e-mail repete `POST /auth/register` (o contrato v1 não tem endpoint de reenvio).
