# Especificação do MVP — Nina

Fonte: `specs/discovery-napper-wonder-weeks.md` (seções 6–15). O discovery cataloga capacidades públicas; **não** se copia texto, assets, nomes de fases, jogos ou estrutura editorial dos concorrentes.

## Decisões do usuário
- Clientes nativos: iOS (Swift) e Android (Kotlin).
- Backend .NET com BFF; PostgreSQL.
- Escopo desta iniciativa: **MVP completo**.

## Escopo do MVP (37 RFs — corrigido: o título anterior dizia 45, mas a tabela soma 37; ver D-14 em specs/product-spec.md)
| Grupo | RFs |
|---|---|
| Conta, sessão, consentimento | RF-001, 002, 003 |
| Bebê e cuidadores | RF-004, 005, 006, 007 |
| Sono e previsão | RF-008 a RF-014 |
| Alimentação, fralda, pumping, timeline | RF-015 a RF-020 |
| Gráficos essenciais | RF-028, RF-029 |
| Notificações e preferências | RF-037, 038, 039, 040 |
| Assinatura | RF-041, 042, 043 |
| LGPD (exportar, excluir) | RF-044, RF-045 |
| Offline e sync | RF-046, RF-047 |
| Analytics sem PII | RF-048 |
| Sessões e auditoria | RF-054, RF-055 |
| i18n | RF-050 (PT/EN/ES, PT primeiro) |

**Fora do MVP:** desenvolvimento/marcos (RF-021..025), diário (026/027), resumo semanal (030), conteúdo/CMS (031, 032, 049), áudio (033/034), IA (035/036), comunidade (051/052), backup (053), Watch/widgets.

## Regras que moldam o desenho
RB-001/002 (real > previsto), RB-004 (data prevista separada), RB-006/007/015 (papéis e revogação), RB-008 (tombstones), RB-009 (entitlement no backend), RB-010 (timezone/quiet hours), RB-014 (UTC + timezone), RNF-014 (previsão não é diagnóstico).

## Suposições (corrigíveis)
- S1: PT-BR primeiro, i18n desde o início.
- S2: previsão de sono por regras (tabelas de referência por idade, cold start); sem ML.
- S3: assinatura = entitlement server-side, plano free + um premium; integração StoreKit/Play Billing validada no backend.
- S4: conteúdo editorial é original e revisado por especialista (V1).
- S5: sem IA nem comunidade no MVP.
- S6: monorepo.

## Dúvidas abertas (não bloqueantes)
- Política de idade corrigida para prematuros (RF-005).
- Preço/limites do plano free × premium.
- Cloud alvo e provedor de push (APNs/FCM diretos).
- Revisão jurídica/LGPD antes de usuários reais (Privacy Gate).

## Gates aplicáveis
API, Accessibility, **Privacy**, Performance, Operational Readiness, Release. `SECURITY-REVIEW-001` é bloqueante do release (auth, multi-tenant, dado de criança, exposição pública).
