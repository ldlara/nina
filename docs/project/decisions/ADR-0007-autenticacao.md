# ADR-0007 — Autenticação
Status: aceita (decisão do usuário, 2026-10-08; resolve a pergunta de login).

- Cadastro e login por **usuário/e-mail e senha**, **Google** e **Apple** (Sign in with Apple).
- Tokens de identidade de Google/Apple são validados no backend (assinatura, `aud`, `iss`, expiração, nonce); a sessão é do Nina.
- Vinculação de identidades: mesmo e-mail verificado não deve fundir contas automaticamente sem confirmação (evitar account takeover).
- Senhas: hash adaptativo (Argon2id/bcrypt), rate limit, proteção contra enumeração de contas.
- Exclusão de conta disponível no app (exigência das lojas), com revogação do vínculo Apple/Google.

Pendências: política de senha, verificação de e-mail, recuperação de acesso, MFA (fora do MVP), idade mínima (DJ-11).
