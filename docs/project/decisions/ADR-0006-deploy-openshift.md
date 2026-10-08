# ADR-0006 — Implantação: containers em OpenShift
Status: aceita (decisão do usuário, 2026-10-08; resolve D-03 parcialmente).

- Serviços (API, BFF) empacotados em Docker e executados em **OpenShift**.
- Requisitos de compatibilidade: executar sem root e com UID arbitrário, porta 8080, filesystem somente leitura quando possível, probes de liveness/readiness, configuração e segredos por variáveis/Secrets (nunca na imagem).
- O usuário citou "Shadow IT" como contexto de hospedagem. **Risco registrado:** infraestrutura fora da governança corporativa de TI afeta segurança, backup/RPO-RTO, LGPD (local dos dados, DJ-04) e suporte. Antes de dados reais, definir responsável, região/local de hospedagem, backup do PostgreSQL, gestão de segredos e monitoramento. Entra no escopo de SECURITY-REVIEW-001, SRE-001 e PRIV-001.

Pendências: provedor de push (APNs/FCM são diretos por padrão), PostgreSQL gerenciado ou em OpenShift, registry de imagens, pipeline de CI/CD.
