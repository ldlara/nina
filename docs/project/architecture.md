# Arquitetura inicial — Nina

## Visão
```
iOS (SwiftUI, SQLite/GRDB)   Android (Compose, Room)
          \                    /
           \--- HTTPS/JSON ---/
                  BFF (.NET)         agregação, DTOs por cliente, sync endpoints
                     |
              API / Domínio (.NET, monólito modular)
   Identity | Family | Tracking | SleepIntelligence | Notifications | Subscriptions | Privacy
                     |
     PostgreSQL        Fila/Jobs (outbox + worker)     APNs/FCM     Lojas (validação de recibo)
```

## Princípios
- Monólito modular com módulos isolados; sem microserviços no MVP.
- Clientes **offline-first**: banco local, fila de mutações com UUID, `client_created_at`, versão, estado de sync.
- Servidor é fonte canônica de versão; exclusão por tombstone.
- Motor de sono = biblioteca pura e testável (histórico + idade → previsão + confiança + explicação); previsões nunca sobrescrevem eventos.
- Autorização por bebê (RBAC Owner/Caregiver/ReadOnly) server-side em toda consulta.
- Timestamps UTC + timezone contextual.
- Jobs idempotentes (outbox, retry com backoff, dead-letter).
- Logs/analytics sem PII (RB-011).

## Estrutura de repositório proposta
```
/specs  /docs/project  /backend (Nina.sln: Nina.Bff, Nina.Api, módulos, tests)
/ios    /android       /infra   /contracts (OpenAPI)
```

## Riscos de arquitetura
Sync multi-cuidador (ADR-0003), calibração do motor de sono, validação de recibos de loja.
