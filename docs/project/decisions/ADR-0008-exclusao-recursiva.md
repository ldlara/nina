# ADR-0008 — Exclusão recursiva de conta
Status: aceita em princípio (decisão do usuário, 2026-10-08, resolve D-34), **com ponto aberto**.

Exclusão de conta é **recursiva (cascata)**: remove os dados dependentes (bebês, eventos, cuidadores, preferências, consentimentos conforme retenção legal, sessões, tombstones e backups conforme política).

**Ponto aberto crítico:** se o Owner exclui a conta e o bebê tem outros cuidadores, a cascata apaga os dados deles também? Opções: (a) excluir tudo; (b) bloquear até transferir a propriedade; (c) excluir só o vínculo do Owner. Isso conflita com DJ-09 (jurídico) e RB-007. Até confirmação, a implementação não pode apagar dados de outros cuidadores sem aviso explícito e confirmação.
Pendências: janela de arrependimento, retenções legais (DJ-06), auditoria do pedido, propagação por tombstone aos dispositivos.
