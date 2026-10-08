# Nina

App parental com linha do tempo única do bebê: sono, alimentação, fraldas, agenda de sono adaptativa, notificações e (V1+) desenvolvimento, diário e conteúdo.

## Stack (ADR-0001)
- iOS: Swift / SwiftUI (nativo)
- Android: Kotlin / Jetpack Compose (nativo)
- BFF + API: .NET (ASP.NET Core)
- Banco: PostgreSQL

## Documentação
- `specs/discovery-napper-wonder-weeks.md` — discovery/escopo de produto (fonte)
- `docs/project/specification.md` — escopo do MVP, requisitos e suposições
- `docs/project/architecture.md` — arquitetura inicial
- `docs/project/backlog.md` — backlog, dependências e ondas
- `docs/project/status.md` — status atual
- `docs/project/decisions/` — ADRs

## Ambiente de desenvolvimento
`scripts/setup-dev-env.sh` instala .NET SDK 10 e Android SDK (Linux). iOS requer macOS + Xcode (teste local).
