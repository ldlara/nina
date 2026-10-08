# ADR-0005 — Entitlements, feature gates e configuração em banco
Status: aceita (decisão do usuário, 2026-10-08; resolve D-02, D-04 e parte de D-01).

- **Bloqueio por gatilhos:** toda funcionalidade que possa ser premium nasce com um *gate* (bloqueada por padrão). O que é free ou premium é decidido liberando flags **no banco de dados**, sem novo deploy.
- **Plano premium por família:** titular (Owner) + **1 membro adicional**. O entitlement pertence à família, não ao usuário. Validação sempre no backend (RB-009).
- **Parâmetros editáveis no banco:** política de idade corrigida (D-01), tabelas de referência do motor de sono e limites de plano. Mudança é auditada.
- Clientes consultam o entitlement/flags pelo BFF e nunca decidem sozinhos.

Pendências: confirmar que "um adicional" significa o titular + 1 pessoa (total 2 membros com premium); o que ocorre se o titular cancelar; preço; schema das tabelas `feature_flag`, `plan`, `family_entitlement`; quem pode editar flags em produção (papel e auditoria).
