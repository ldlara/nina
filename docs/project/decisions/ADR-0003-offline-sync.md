# ADR-0003 — Offline e sincronização
Status: **aceita** (decisão do usuário em 2026-10-08; resolve D-06).

## Modelo
- Sync incremental baseado em **change log**.
- Toda mutação server-side recebe um `sync_sequence` monotonicamente crescente.
- A API expõe um **cursor opaco**; internamente ele referencia o `sync_sequence`. Clientes nunca interpretam o cursor.
- Exclusões são **tombstones** e participam normalmente do change feed.
- **Retenção de tombstones: 90 dias.**

## Mutações do cliente
Cada mutação: UUID (idempotência), `client_created_at`, `base_version`, `device_id`. Push idempotente por UUID; pull incremental pelo cursor.

## Consequências / pendências
- Dispositivo offline por mais de 90 dias não pode mais convergir por delta: o servidor responde "cursor expirado" e o cliente faz resync completo (a especificar em API-001).
- O `sync_sequence` deve ser atribuído de forma que não haja lacunas visíveis fora de ordem de commit (ex.: sequência por transação com leitura apenas até o menor `sync_sequence` confirmado) — a definir no spike ARCH-003.
- Revogação de cuidador invalida tokens e cursor; conflitos de edição concorrente (last-write-wins por campo + auditoria) seguem como proposta, a validar no spike.
