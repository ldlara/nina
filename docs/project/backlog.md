# Backlog do MVP — Nina

Status: Backlog | Ready | In Progress | Blocked | In Review | Done. Complexidade XS–XL (XL deve ser dividida).
Plataformas: **BE** = .NET (`dotnet-backend-engineer`), **IOS** = `swift-engineer`, **AND** = `kotlin-android-engineer`.

## Épicos
EPIC-001 Descoberta/fundação · 002 Identidade · 003 Família/bebê · 004 Tracking e sync · 005 Sono · 006 Notificações · 007 Gráficos · 008 Assinaturas · 009 LGPD/segurança/observabilidade · (V1) 010 Desenvolvimento/diário · 011 Conteúdo/áudio · (V2) 012 IA/comunidade.

## Tarefas
| ID | Tarefa | Prior. | Compl. | Agente | Dependências | Paralela | Status |
|---|---|---|---|---|---|---|---|
| PRODUCT-001 | MVP, outcome, priorização | P0 | S | product-owner | — | não | Done (escopo em specification.md) |
| REQ-001 | Product spec, glossário, critérios de aceite | P0 | M | requirements-analyst | PRODUCT-001 | não | Ready |
| ARCH-001 | Arquitetura + ADRs (sync, auth, tenancy) | P0 | L | backend-architect | REQ-001 | não | In Progress (ADR-0001..0004 propostos) |
| ARCH-002 | Arquitetura iOS/Android (estado, offline) | P0 | M | frontend-architect | ARCH-001 | com ARCH-003 | Backlog |
| ARCH-003 | Spike de sync (2 dispositivos, conflito) | P0 | M | backend-architect + dotnet | ARCH-001 | com ARCH-002 | Backlog |
| DB-001 | Modelo de dados e migrations | P0 | M | database-engineer | ARCH-001 | não | Backlog |
| API-001 | Contratos OpenAPI (auth, baby, tracking, sync, sleep) | P0 | L | api-contract-engineer | ARCH-001, DB-001 | não | Backlog |
| UX-001 | Fluxos, wireframes, design system | P1 | L | ux-ui-designer | REQ-001 | com ARCH-001 | Backlog |
| CLOUD-001 | Repo, CI/CD, ambientes, IaC | P0 | M | cloud-backend-engineer | ARCH-001 | com DB-001 | Backlog |
| QA-001 | Estratégia de testes e aceite | P0 | M | software-quality-engineer | REQ-001 | com UX-001 | Backlog |
| PRIV-001 | RIPD, consentimentos, retenção, DSAR | P0 | M | privacy-compliance-reviewer | REQ-001 | com ARCH-001 | Backlog |
| BE-001 | Identity: conta, sessão, recuperação, sessões/dispositivos | P0 | L | dotnet | API-001, DB-001 | com BE-002 | Backlog |
| BE-002 | Família: bebê, cuidadores, papéis, convites, revogação | P0 | L | dotnet | API-001, DB-001 | com BE-001 | Backlog |
| BE-003 | Tracking: sono, mamada, mamadeira, fralda, pumping, timeline | P0 | L | dotnet | BE-002 | não | Backlog |
| BE-004a | Sync: push idempotente + pull por cursor | P0 | L | dotnet | BE-003, ARCH-003 | não | Backlog |
| BE-004b | Sync: tombstones, conflitos, auditoria | P0 | L | dotnet | BE-004a | não | Backlog |
| BE-005 | Motor de previsão de sono (regras) | P0 | L | dotnet | BE-003, ADR-0004 | com FE | Backlog |
| BFF-001 | BFF: composição, DTOs, endpoints mobile | P0 | M | dotnet | API-001 | com BE-001 | Backlog |
| MSG-001 | Outbox, jobs, retry, DLQ | P1 | M | messaging-engineer | ARCH-001 | com BE-003 | Backlog |
| BE-006 | Notificações (soneca, bedtime, quiet hours, idempotência) | P1 | M | dotnet + messaging | BE-005, MSG-001 | com BE-007 | Backlog |
| BE-007 | Assinatura: entitlement, validação de recibo Apple/Google | P1 | L | integration-engineer + dotnet | API-001 | com BE-006 | Backlog |
| BE-008 | Exportação e exclusão de conta (LGPD) | P1 | M | dotnet | PRIV-001, BE-003 | não | Backlog |
| BE-009 | Gráficos: agregações diárias/semanais/mensais | P1 | M | dotnet | BE-003 | com BE-006 | Backlog |
| IOS-001 | iOS: fundação, auth, onboarding, bebê | P0 | L | swift-engineer | API-001, UX-001 | com AND-001 | Backlog |
| IOS-002 | iOS: tracking + timeline + persistência local | P0 | L | swift-engineer | IOS-001, BE-003 | com AND-002 | Backlog |
| IOS-003 | iOS: sync engine | P0 | XL→dividir | swift-engineer | IOS-002, BE-004a | com AND-003 | Backlog |
| IOS-004 | iOS: agenda/previsão, gráficos, push | P1 | L | swift-engineer | BE-005, BE-009 | com AND-004 | Backlog |
| IOS-005 | iOS: assinatura (StoreKit), LGPD, i18n | P1 | L | swift-engineer | BE-007, BE-008 | com AND-005 | Backlog |
| AND-001 | Android: fundação, auth, onboarding, bebê | P0 | L | kotlin-android-engineer | API-001, UX-001 | com IOS-001 | Backlog |
| AND-002 | Android: tracking + timeline + Room | P0 | L | kotlin-android-engineer | AND-001, BE-003 | com IOS-002 | Backlog |
| AND-003 | Android: sync engine | P0 | XL→dividir | kotlin-android-engineer | AND-002, BE-004a | com IOS-003 | Backlog |
| AND-004 | Android: agenda/previsão, gráficos, push | P1 | L | kotlin-android-engineer | BE-005, BE-009 | com IOS-004 | Backlog |
| AND-005 | Android: assinatura (Play Billing), LGPD, i18n | P1 | L | kotlin-android-engineer | BE-007, BE-008 | com IOS-005 | Backlog |
| REVIEW-001 | Revisão independente de código (por onda) | P0 | M | code-reviewer | cada entrega | — | Backlog |
| A11Y-001 | Acessibilidade (VoiceOver/TalkBack) | P1 | S | accessibility-reviewer | IOS-004, AND-004 | com PERF-001 | Backlog |
| PERF-001 | Orçamentos e carga (p95 < 500 ms) | P1 | M | performance-engineer | BE-004b | com A11Y-001 | Backlog |
| SEC-001 | Pipeline, scans, secrets, observabilidade | P0 | M | devsecops-engineer | CLOUD-001 | com BE-001 | Backlog |
| SECURITY-REVIEW-001 | Auditoria: auth, tenancy, dado de criança, exposição pública | P0 | L | security-reviewer | BE-001..004b | não | Backlog |
| SRE-001 | SLOs, alertas, runbooks | P1 | M | sre-engineer | CLOUD-001 | com SECURITY-REVIEW-001 | Backlog |
| DOC-001 | ADRs finais, runbooks, docs | P2 | S | technical-writer | contínua | sim | Backlog |
| RELEASE-001 | Gates e readiness | P0 | M | release-manager | todos os gates | não | Backlog |

## Ondas
0 decisões ✔ · 1 REQ/UX/PRIV/QA · 2 ARCH/DB/API/CLOUD/SEC/spike de sync · 3 BE-001/002/BFF + IOS/AND-001 · 4 BE-003/004/005 + IOS/AND-002/003 · 5 BE-006..009 + IOS/AND-004/005 · 6 gates → RELEASE-001.
Caminho crítico: REQ-001 → ARCH-001/003 → API-001 → BE-003 → BE-004a/b → IOS/AND-003 → SECURITY-REVIEW-001 → RELEASE-001.

## Regras
Contrato OpenAPI (API-001) congelado antes de BE/BFF/IOS/AND paralelos. Tarefas que editam o mesmo arquivo não rodam em paralelo. Nenhuma publicação em loja/produção sem autorização explícita.
