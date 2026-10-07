# ADR-0003 — Offline e sincronização
Status: proposta (a validar com spike SYNC).
- Cada mutação: UUID, client_created_at, base_version, device_id.
- Push de mutações idempotente por UUID; pull incremental por cursor (versão monotônica por bebê).
- Conflitos: eventos de tracking = merge por entidade; edição concorrente do mesmo evento = last-write-wins por campo com histórico de auditoria; diário (V1) preserva histórico.
- Exclusão: tombstone retido por janela definida até convergência.
- Revogação de cuidador: invalida tokens e cursor.
Pendente: janela de retenção de tombstone, formato do cursor.
