# Backend (.NET 10)

Solucao `Nina.sln` — monolito modular + BFF (ADR-0001, ADR-0002). Sem regras de negocio nesta etapa (scaffold CLOUD-001/SEC-001).

```
backend/
  Nina.sln  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
  src/
    Nina.Api/          minimal API (host do monolito); /health (liveness) e /ready (readiness)
    Nina.Bff/          BFF; consome Nina.Api via HttpClient tipado; /health e /ready (depende da API)
    Modules/
      Nina.Identity  Nina.Family  Nina.Tracking  Nina.SleepIntelligence
      Nina.Notifications  Nina.Subscriptions  Nina.Privacy      (so estrutura: <Modulo>Module.Add<Modulo>Module)
  tests/
    Nina.Api.Tests/    xUnit + WebApplicationFactory
    Nina.Bff.Tests/    xUnit + WebApplicationFactory (API simulada)
```

Os caminhos `src/Nina.Api` e `src/Nina.Bff` casam com `infra/docker/Dockerfile.service` (`--build-arg PROJECT=Nina.Api|Nina.Bff`) e `docker-compose.yml`.

## Qualidade
- Nullable habilitado, warnings como erro, analyzers .NET (`AnalysisLevel=latest-recommended`), `NuGetAudit` ligado.
- Pacotes com versao central (`Directory.Packages.props`).

## Comandos
    export PATH=/opt/dotnet:$PATH            # onde instalado por scripts/setup-dev-env.sh
    dotnet build -c Release
    dotnet test -c Release
    dotnet format Nina.sln --verify-no-changes
    dotnet list Nina.sln package --vulnerable --include-transitive

Ambiente completo: `cp .env.example .env && docker compose up --build` (API em :5080, BFF em :5081).

## CI e deploy
- `.github/workflows/ci.yml`: restore, format, build, test e scan de vulnerabilidades.
- `infra/openshift/`: Deployments (nao-root, UID arbitrario, FS somente leitura, probes), Services, Route (so BFF), ConfigMap; Secret apenas por referencia (ADR-0006).

## Pendencias
Persistencia/PostgreSQL e checagem no `/ready`, autenticacao (ADR-0007), registry de imagens, host da Route, build/push de imagem no CI.
