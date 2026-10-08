# Status — 2026-10-08
- Decisões: iOS Swift, Android Kotlin, .NET + BFF, PostgreSQL, MVP completo.
- Branches: `main` e `develop` criadas; trabalho em `denis/fervent-edison-rk7l9g`.
- Containers: `docker-compose.yml` (postgres 17 + imagem .NET SDK 10 para api/bff/tools). Compose validado com `docker compose config`; build/run não executados (sem daemon/SDK neste ambiente).
- Onda 1 concluída (documentos): REQ-001 (`specs/product-spec.md`, `glossary.md`, `domain-model.md`), UX-001 (`ux-spec.md`), PRIV-001 (`privacy-security-spec.md`), QA-001 (`test-strategy.md`). Em revisão.
- Correção: o MVP tem 37 RFs, não 45 (D-14).
- Bloqueios/decisões pendentes: 41 dúvidas em product-spec §9 (idade corrigida D-01, free×premium D-02, cloud D-03, premium por usuário ou família D-04, tombstones D-06, exclusão D-07/D-34); 13 decisões jurídicas (DJ-01..13) em privacy-security-spec §11; 25 dúvidas de UX em ux-spec §15.
- Próximo: Onda 2 (ARCH, DB, API/OpenAPI, CLOUD, SEC, spike de sync) após resolver as dúvidas que bloqueiam ADRs.
