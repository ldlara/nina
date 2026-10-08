# ADR-0010 — Fechamento do contrato v1 (MVP)
Status: **aceita** (decisão do usuário, 2026-10-08). Complementa ADR-0009.

1. **Nomes de campos em `snake_case`** em todo o contrato.
2. **Todos os enums em MAIÚSCULAS** (inclui `sleep_type`, `sex`, `source` de sono, papéis etc.). Clientes toleram valores desconhecidos.
3. **Exclusão de conta:** apenas **Owner ativo de um bebê** pode solicitá-la (cascata por padrão, flag). Para cumprir a LGPD, quem **não** é Owner (Caregiver/ReadOnly, ou sem vínculo) mantém os direitos do titular por um **caminho separado de requisição de privacidade** (acesso, correção, exportação e eliminação/anonimização dos **seus próprios dados pessoais**: conta, sessões, consentimentos, tokens, atribuição de autoria), **sem apagar os dados do bebê** que pertencem ao Owner. Prazo de atendimento e verificação de identidade conforme privacy-security-spec §5; **validação jurídica pendente (DJ-09)**.
4. **Valores iniciais aceitos, customizáveis no banco:** `age.corrected_window_months = 24`; `sleep.night_awakenings.min_session_minutes = 240`.
5. **Mamada:** `end_at` **obrigatório** (PA-05 resolvido).
6. **Janela de arrependimento da exclusão: 7 dias**, parâmetro no banco (`privacy.deletion_grace_days = 7`); o banco/worker não executa a exclusão antes de `scheduled_for`; cancelável durante a janela.
