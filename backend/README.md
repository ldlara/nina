# Backend (.NET 10)

Solucao `Nina.sln` — monolito modular + BFF (ADR-0001, ADR-0002). Modulo Identity implementado (BE-001); demais modulos so com estrutura.

```
backend/
  Nina.sln  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
  src/
    Nina.Api/          minimal API (host do monolito); /health (liveness) e /ready (readiness)
    Nina.Bff/          BFF (unico exposto): encaminha os endpoints do contrato para a API interna; /health e /ready
    Nina.SharedKernel/ infraestrutura compartilhada: Npgsql (papel nina_app, SET LOCAL nina.user_id por transacao),
                       runner de migracao, RFC 7807, JWT ES256, rate limit, auditoria, cabecalhos de seguranca
    Modules/
      Nina.Identity      cadastro/verificacao, login (senha, Google, Apple), refresh rotativo, sessoes, reauth,
                         recuperacao de senha, push tokens, consentimentos
      Nina.Family  Nina.Tracking  Nina.SleepIntelligence
      Nina.Notifications  Nina.Subscriptions  Nina.Privacy      (so estrutura: <Modulo>Module.Add<Modulo>Module)
  tests/
    Nina.Api.Tests/       xUnit + WebApplicationFactory
    Nina.Bff.Tests/       xUnit + WebApplicationFactory (API simulada): encaminhamento e superficie publica
    Nina.Identity.Tests/  unidade + integracao contra PostgreSQL 16 real e temporario (initdb, porta alta, removido ao final)
```

Os caminhos `src/Nina.Api` e `src/Nina.Bff` casam com `infra/docker/Dockerfile.service` (`--build-arg PROJECT=Nina.Api|Nina.Bff`) e `docker-compose.yml`.

## Qualidade
- Nullable habilitado, warnings como erro, analyzers .NET (`AnalysisLevel=latest-recommended`), `NuGetAudit` ligado.
- Pacotes com versao central (`Directory.Packages.props`).

## Configuracao (Identity / infraestrutura)
Segredos vem do cofre (ADR-0006); fora de `Development` a ausencia das chaves abaixo impede a inicializacao.

| Chave | Descricao |
|---|---|
| `ConnectionStrings:Default` | login membro de `nina_app` (nunca dono/superusuario: RLS) |
| `ConnectionStrings:Migrations` + `Database:ApplyMigrations=true` | dono do schema; aplica `backend/db/migrations/*.sql` na subida (opcional) |
| `Jwt:SigningKeyPem`, `Jwt:KeyId`, `Jwt:VerificationKeys:<kid>` | chave ECDSA P-256 (ES256) e chaves publicas antigas para rotacao |
| `Security:MasterKey` | Base64 >= 32 bytes; deriva os HMAC de codigos, tokens de recuperacao e hash de IP |
| `Identity:GoogleClientIds`, `Identity:AppleClientIds` | `aud` aceitas nos id_tokens |
| `Identity:*` | politica de senha, Argon2id, TTLs e limites de taxa (ver `IdentityOptions`) |

E-mail e provedores externos estao atras de interfaces (`IIdentityMailer`, `IIdentityTokenVerifier`); o padrao e o fake em memoria
(em `Development` o codigo/token e escrito no log). Nenhum envio ou chamada de rede real e feito pelos testes.

## Comandos
    export PATH=/opt/dotnet:$PATH            # onde instalado por scripts/setup-dev-env.sh
    dotnet build -c Release
    dotnet test -c Release
    dotnet format Nina.sln --verify-no-changes
    dotnet list Nina.sln package --vulnerable --include-transitive

Os testes de integracao do Identity sobem um PostgreSQL local (`/usr/lib/postgresql/*/bin`; como root usam `runuser -u postgres`).
Para usar um servidor existente: `NINA_TEST_PG_ADMIN='Host=...;Username=postgres'`. Binarios em outro lugar: `NINA_PG_BIN`.

Ambiente completo: `cp .env.example .env && docker compose up --build` (API em :5080, BFF em :5081).

## CI e deploy
- `.github/workflows/ci.yml`: restore, format, build, test e scan de vulnerabilidades.
- `infra/openshift/`: Deployments (nao-root, UID arbitrario, FS somente leitura, probes), Services, Route (so BFF), ConfigMap; Secret apenas por referencia (ADR-0006).

## Pendencias
Registry de imagens, host da Route, build/push de imagem no CI; limites de taxa distribuidos (hoje por instancia); provedores reais de e-mail, HIBP e JWKS em producao.
