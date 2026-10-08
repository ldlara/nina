# ADR-0009 — Convenções de contrato e decisões de domínio
Status: **aceita** (decisão do usuário, 2026-10-08). Resolve D-01, D-07, D-08, D-12, D-13 e PA-01/02/04/05/07; atualiza ADR-0005 e ADR-0008.

## Decisões de produto
1. **Cadastro:** conta ativa somente após confirmação por e-mail (sessão nasce em `POST /auth/email/verify`).
2. **Escrita de eventos:** somente por `POST /sync/push`; online é um lote de 1 mutação.
3. **Mamada:** mantém-se `end_at` obrigatório no MVP (a resposta "Correto" sobre permitir `end_at` nulo foi ambígua; reabrir se o timer de mamada for necessário).
4. **Exclusão de conta:** existe janela de arrependimento (`scheduled_for`, cancelável).
5. **Sono sobreposto:** aceito e sinalizado por padrão, **customizável por flag** no banco (`reject` possível).
6. **Somente o Owner** pode excluir a conta e editar o perfil do bebê.
7. **Adicional do premium** (titular + 1) **não precisa ser cuidador ativo** do bebê.
8. **Exclusão de conta é em cascata por padrão, customizável por flag** (políticas: `cascade` = padrão; `block` e `transfer_ownership` disponíveis por flag). Substitui o padrão `block` da migração inicial. Nota: a cascata apaga os dados do bebê também para os demais cuidadores; a UI deve avisar e exigir confirmação explícita (reautenticação). Validação jurídica pendente (DJ-09).

## Convenção de nulos (regra geral do contrato)
- `null` = **não se aplica** (NOT_APPLICABLE) ou **indisponível/desconhecido** quando o contexto permitir distinguir (documentar em cada campo).
- `0` = valor conhecido igual a zero. Nunca usar 0 para "não informado".
- `UNSPECIFIED` (enums) = usuário deliberadamente não especificou. `UNKNOWN` = deveria existir mas o sistema não sabe (ex.: migração de dados antigos).

## Idade
- Persistir `birth_date` e `due_date`; **nunca persistir idade corrigida**.
- `corrected_age = chronological_age − (due_date − birth_date)`, somente se `birth_date < due_date` e dentro da janela de aplicação definida pelo produto (parâmetro editável no banco, ADR-0005).
- Contrato: `ageCalculation { chronologicalDays, correctedDays (nullable), correctionApplied (bool) }`. `correctedDays = null` significa "não se aplica".

## Enums
- `DiaperType`: `WET`, `DIRTY`, `MIXED`, `DRY`, `UNSPECIFIED` (WET = urina; DIRTY = fezes; MIXED = ambos; DRY = verificada sem urina/fezes).
- `FeedingType`: `BREASTFEEDING`, `BOTTLE`, `SOLID`, `OTHER`.
- `MilkType`: `BREAST_MILK`, `FORMULA`, `MIXED`, `OTHER`, `UNSPECIFIED` (+ `UNKNOWN` reservado para migração). **Só se aplica quando `feedingType = BOTTLE`**; nos demais, `milkType = null`.
- Enums extensíveis: clientes devem tolerar valores desconhecidos.
- Amamentação: `side` (LEFT/RIGHT/BOTH) e duração; mamadeira: `volumeMl` e `milkType`.

## Despertares noturnos
- Fonte da verdade: entidade **`WakeEvent`** (`id`, `sleepSessionId`, `startedAt`, `endedAt`, `durationSeconds`, `source` ∈ {`MANUAL`, `INFERRED`, `IMPORT`}), sincronizável (change log/tombstone) e com correção manual.
- `nightAwakenings` é **derivado**: `null` = dados insuficientes; `0` = acompanhamento suficiente e nenhum despertar; `N>0` = quantidade. Critério de "suficiente" = parâmetro editável.
- Idade corrigida e despertares são **valores derivados**; tipos de fralda e leite são **classificações de domínio** — modelar separadamente.

## Impacto
Atualizar: `specs/database-spec.md` + `0001_init.sql` (WakeEvent, enums, flags de exclusão e sobreposição), `contracts/openapi.yaml` + `specs/api-spec.md`, `specs/domain-model.md`, `specs/product-spec.md`.
