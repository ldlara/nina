# Especificação de Produto — Nina (MVP)

Status: rascunho para revisão (REQ-001) · Data: 2026-10-08
Fontes: `specs/discovery-napper-wonder-weeks.md` (seções 6, 10–15), `docs/project/specification.md`, `architecture.md`, `backlog.md`, ADR-0001..0004.
Documentos irmãos: `specs/glossary.md`, `specs/domain-model.md`.

> **Propriedade intelectual.** O discovery cataloga capacidades públicas dos concorrentes. Esta especificação descreve comportamento próprio da Nina; não reproduz textos, nomes de fases, jogos, assets nem estrutura editorial de terceiros.
>
> **Nota de contagem.** O pedido fala em 45 RFs. O escopo listado (RF-001..020, 028, 029, 037..048, 050, 054, 055) e a tabela de `docs/project/specification.md` somam **37 RFs** (20 + 2 + 12 + 3). Esta spec cobre os 37 e não inventa os 8 restantes; ver dúvida D-14.

## 1. Visão e escopo

A Nina é um app nativo (iOS e Android) para cuidadores registrarem a rotina real do bebê (sono, alimentação, fraldas, pumping), verem uma timeline única, receberem previsão de soneca e bedtime como orientação, acompanharem gráficos do próprio histórico e compartilharem tudo com outros cuidadores, funcionando offline.

Princípios (discovery 6, 13, 14): histórico real tem precedência sobre previsão (RB-001/002); previsão é orientação, não diagnóstico (RNF-014); privacidade por desenho para dados de criança (RNF-002/003); offline-first (RNF-007).

Dentro do MVP: conta e sessão; bebê e cuidadores; sono e previsão; alimentação, fralda, pumping e timeline; gráficos essenciais; notificações; assinatura; exportação e exclusão; offline e sync; analytics sem PII; sessões e auditoria; i18n (PT-BR primeiro, EN/ES).

Fora do MVP: desenvolvimento e marcos (RF-021..025), diário (026/027), resumo semanal (030), conteúdo/CMS (031, 032, 049), áudio (033/034), assistente de IA (035/036), comunidade (051/052), backup em nuvem (053), Watch/widgets.

## 2. Personas

Proto-personas baseadas nos papéis e jornadas do discovery. São hipóteses a validar com pesquisa (D-15).

**P1 — Cuidador principal (Owner).** Mãe, pai ou responsável que cria o perfil, registra a maior parte dos eventos, decide quem tem acesso e é o titular da assinatura. Dor: cansaço e falta de memória confiável sobre horários; quer registro em poucos toques, mesmo no escuro e sem rede. Quer saber quando a próxima soneca deve acontecer.

**P2 — Cuidador secundário (Caregiver).** Parceiro(a), avó/avô ou babá que registra eventos durante o seu turno. Dor: não saber o que aconteceu antes de assumir; duplicar ou perder registros. Quer ver a timeline atualizada e registrar sem depender do Owner.

**P3 — Observador (ReadOnly).** Familiar ou profissional que acompanha sem editar. Dor: pedir atualizações constantes. Quer consultar timeline e gráficos com certeza de que não altera nada.

**P4 — Responsável preocupado com privacidade (transversal).** Qualquer cuidador atento ao uso de dados do bebê. Quer exportar, excluir, revogar acessos e ver quais dispositivos estão logados.

## 3. Jornadas do MVP

| ID | Jornada | RFs |
|---|---|---|
| J1 | Onboarding: criar conta, aceitar termos, criar o bebê (DPP e fuso), primeira tela com timeline vazia e agenda baseada na idade | 001, 003, 004, 005, 050 |
| J2 | Registrar sono com timer, inclusive offline; corrigir depois; ver previsão atualizada | 008, 009, 010, 011, 012, 046 |
| J3 | Registrar cuidados: amamentação, mamadeira, pumping e fralda; ver tudo na timeline | 015..019 |
| J4 | Planejar o dia: ver próxima soneca e bedtime, confiança e explicação; ajustar metas de sonecas e faixa de bedtime; receber aviso | 011, 013, 014, 037, 038, 039 |
| J5 | Compartilhar com outro cuidador: convidar, aceitar, ver dados sincronizados; Owner revoga | 006, 007, 047, 055 |
| J6 | Analisar: gráficos de sono, alimentação e cuidados por dia/semana/mês; médias e tendências do próprio bebê | 028, 029 |
| J7 | Corrigir ou excluir um evento (inclusive compartilhado) e ver os totais e a previsão se ajustarem | 020, 012, 047 |
| J8 | Assinar o premium, restaurar compra em novo dispositivo | 041, 042, 043 |
| J9 | Privacidade: preferências, exportar dados, excluir conta, ver e revogar sessões | 040, 044, 045, 054, 002 |
| J10 | Recuperar acesso e encerrar sessões em outros dispositivos | 002, 054 |

## 4. Convenções dos critérios de aceite

- Formato **Dado / Quando / Então**. Cada critério tem ID `RF-nnn-An` e é testável (automatizado ou manual guiado; sugestão de nível entre parênteses: U unitário, I integração, C contrato/API, E2E, M manual).
- "Rastreio" lista RB (regras de negócio), RNF (não funcionais) e, quando houver, ADR/jornada. Os RF fora desta lista não têm critérios aqui.
- Valores numéricos não definidos no discovery (tempo, limite, tamanho) aparecem como `[a definir: D-nn]` e não bloqueiam a escrita, mas bloqueiam o teste correspondente.
- Papéis: Owner, Caregiver, ReadOnly (RF-006). Toda operação de leitura/escrita de bebê exige autorização server-side (discovery 12; INV-08/13 do domain-model).

## 5. Requisitos funcionais do MVP

### 5.1 Conta, sessão e consentimento

**RF-001 — Criar conta com e-mail e autenticar com sessão segura.** Rastreio: RNF-001, RNF-003, RNF-008; J1.
- RF-001-A1 (I/C): Dado um e-mail ainda não cadastrado e senha dentro da política `[a definir: D-16]`, quando o usuário envia o cadastro, então a conta é criada, uma sessão é emitida e a senha não é armazenada nem logada em claro.
- RF-001-A2 (C): Dado um e-mail já cadastrado, quando alguém tenta cadastrar, então a resposta não revela se o e-mail existe (proteção contra enumeração) e nenhuma segunda conta é criada.
- RF-001-A3 (C): Dadas credenciais válidas, quando o usuário faz login, então recebe sessão ligada ao dispositivo; dadas credenciais inválidas, então recebe erro genérico e, após N tentativas `[a definir: D-16]`, o login é limitado por taxa.
- RF-001-A4 (I): Dada uma sessão expirada ou revogada, quando o app chama a API, então recebe 401 e o cliente exige novo login sem apagar os dados locais ainda não sincronizados.
- RF-001-A5 (M/E2E): Dado o tráfego do app, quando inspecionado, então só há TLS 1.2 ou superior.

**RF-002 — Recuperação de acesso e encerramento de sessões.** Rastreio: RNF-001, RB-015 (por analogia de invalidação), RF-054; J9, J10.
- RF-002-A1 (I): Dado um e-mail cadastrado, quando o usuário solicita recuperação, então recebe um link ou código de uso único com expiração `[a definir: D-16]`; para e-mail não cadastrado a resposta externa é idêntica.
- RF-002-A2 (I): Dado um token de recuperação válido, quando o usuário define nova senha, então a senha muda, o token é consumido e todas as outras sessões são encerradas.
- RF-002-A3 (I): Dado um token expirado ou já usado, quando é apresentado, então a troca é recusada.
- RF-002-A4 (E2E): Dado um usuário logado em vários dispositivos, quando ele escolhe "encerrar outras sessões", então os demais dispositivos recebem 401 na próxima chamada e o dispositivo atual permanece ativo.
- RF-002-A5 (I): Quando a senha ou o e-mail é alterado, então um aviso de segurança é enviado (categoria "sistema", discovery 10).

**RF-003 — Registrar aceite versionado de Termos, Política de Privacidade e consentimentos.** Rastreio: RNF-003, RNF-002, RB-011; discovery 12.
- RF-003-A1 (I): Dado o cadastro, quando o usuário aceita Termos e Política, então é criado um ConsentRecord por documento com `document_version`, `granted_at` e canal; sem aceite não há conta.
- RF-003-A2 (I): Dada uma nova versão de documento, quando o usuário abre o app, então um novo aceite é exigido antes de continuar e o histórico anterior é mantido.
- RF-003-A3 (I): Dado um consentimento opcional (ex.: analytics não essencial), quando o usuário o revoga, então `revoked_at` é registrado, o efeito vale a partir de então e o histórico é preservado (ConsentRevoked emitido).
- RF-003-A4 (C): Dado o usuário, quando consulta seus consentimentos, então vê finalidade, versão e datas de cada um.
- RF-003-A5 (I): Consentimentos de finalidades opcionais são apresentados separados do aceite de Termos (discovery 12).

### 5.2 Bebê e cuidadores

**RF-004 — Criar um ou mais perfis de bebê.** Campos: nome/apelido, data de nascimento, data prevista do parto, sexo opcional, fuso, foto opcional. Rastreio: RB-004, RB-006, RB-014; J1.
- RF-004-A1 (I/E2E): Dado o usuário autenticado, quando cria um bebê com apelido, data de nascimento e fuso, então o bebê é criado, o usuário vira Owner ativo e `BabyCreated` é emitido.
- RF-004-A2 (U): Dada a data de nascimento no futuro, quando se tenta salvar, então a validação recusa com mensagem clara (ver D-11 para perfil pré-natal).
- RF-004-A3 (I): Dadas DPP e data de nascimento informadas, quando o perfil é salvo, então ambas persistem em campos distintos e a edição de uma não altera a outra (RB-004).
- RF-004-A4 (I): Dado um usuário com um bebê, quando cria um segundo, então ambos coexistem e cada um tem timeline, vínculos e preferências independentes.
- RF-004-A5 (U): Dados sexo e foto ausentes, quando salva, então o perfil é válido.
- RF-004-A6 (I): Dado outro usuário sem vínculo, quando tenta ler ou editar o bebê por ID, então recebe negação sem revelar a existência do recurso.
- RF-004-A7 (U): Dado fuso omitido, quando salva, então assume o fuso do dispositivo e o exibe para confirmação.

**RF-005 — Calcular idade cronológica e idade corrigida quando aplicável, preservando a DPP.** Rastreio: RB-004, RB-005, RB-013, RNF-014; domain-model seção 6; dúvida D-01.
- RF-005-A1 (U): Dados nascimento 2026-01-10, fuso do bebê e data de hoje 2026-04-10, então a idade cronológica é calculada na data local do bebê (90 dias; 12 semanas e 6 dias; 3 meses completos).
- RF-005-A2 (U): Dado bebê sem DPP, quando a idade é pedida, então só a cronológica é devolvida e a UI não mostra idade corrigida.
- RF-005-A3 (U): Dada DPP igual ou anterior ao nascimento, então idade corrigida = cronológica (sem valores negativos).
- RF-005-A4 (U): Dada DPP posterior ao nascimento, então a idade corrigida é calculada conforme a fórmula do domain-model seção 6 (`[a validar: D-01]`: critério "aplicável" e limite de idade ainda indefinidos). O teste de política fica bloqueado até D-01.
- RF-005-A5 (I): Dada alteração de DPP, nascimento ou fuso, então as idades são recalculadas, os eventos históricos permanecem intactos e as previsões são recalculadas (RF-012).
- RF-005-A6 (M): A UI informa qual idade é exibida e não apresenta comparação com outras crianças nem meta de habilidade (RB-005, RB-013).
- RF-005-A7 (U): Dada troca do fuso do dispositivo, a mudança de faixa de idade usa a data local do bebê.

**RF-006 — Convidar cuidadores e atribuir papéis Owner, Caregiver e ReadOnly.** Rastreio: RB-006, RB-007, RB-015; J5.
- RF-006-A1 (I): Dado um Owner, quando convida um e-mail com papel Caregiver ou ReadOnly, então cria-se vínculo `pending`, o convidado é notificado e nenhum dado do bebê é exposto antes do aceite (RB-006).
- RF-006-A2 (I): Dado um convite pendente, quando o convidado aceita com conta própria, então o vínculo vira `active` com o papel atribuído e `CaregiverAccepted` é emitido.
- RF-006-A3 (I): Dado um Caregiver ou ReadOnly, quando tenta convidar, alterar papéis, remover alguém, transferir propriedade ou excluir o bebê, então recebe 403 (RB-007).
- RF-006-A4 (I): Dado um ReadOnly, quando tenta criar, editar ou excluir qualquer evento, então a API recusa e a UI não oferece a ação.
- RF-006-A5 (E2E): Dado um Owner que remove um cuidador, quando a remoção é confirmada, então os tokens e o cache daquele cuidador para o bebê são invalidados imediatamente e a próxima chamada recebe 403 (RB-015).
- RF-006-A6 (I): Dado um convite recusado, expirado ou cancelado `[a definir: D-17]`, então o vínculo não vira ativo.
- RF-006-A7 (I): O Owner não pode ficar sem Owner: remover ou rebaixar a si mesmo só é aceito após transferir a propriedade.
- RF-006-A8 (I): Dado um usuário já vinculado ao bebê, quando é convidado de novo, então não se cria segundo vínculo.

**RF-007 — Sincronizar dados compartilhados do bebê entre cuidadores autorizados.** Rastreio: RB-006, RB-015, RNF-007, ADR-0003; J5.
- RF-007-A1 (E2E): Dados dois dispositivos de cuidadores ativos do mesmo bebê, quando um registra um evento e há rede, então o outro o vê após o pull, em tempo `[a definir: D-18]`.
- RF-007-A2 (I): Dado um cuidador sem vínculo ativo, quando faz pull ou push do bebê, então nenhum dado é enviado nem aceito.
- RF-007-A3 (I): Dado um cuidador revogado, então o cursor dele para o bebê deixa de ser aceito (ADR-0003).
- RF-007-A4 (I): Dado um cuidador novo, quando aceita o convite, então o pull inicial traz o histórico completo permitido (ver D-19 sobre histórico anterior ao ingresso).
- RF-007-A5 (I): Dados bebês distintos, então o pull de um bebê nunca traz eventos de outro.
- RF-007-A6 (C): Notificações push e URLs de mídia não carregam dados do bebê além do necessário e não são acessíveis sem autorização (discovery 14).

### 5.3 Sono e previsão

**RF-008 — Registrar sono com início, fim, tipo, método/local opcional e observações.** Rastreio: RB-002, RB-003, RB-014, RNF-007; J2.
- RF-008-A1 (I): Dado um Owner ou Caregiver, quando salva sono com início, fim e tipo `nap` ou `night`, então a sessão é criada e aparece na timeline.
- RF-008-A2 (U): Dado fim anterior ao início, então a validação recusa.
- RF-008-A3 (U): Dados método/local e observações vazios, então a sessão é válida.
- RF-008-A4 (I): Dado um ReadOnly, quando tenta registrar, então é recusado (INV-13).
- RF-008-A5 (I): Dado o registro, então início e fim são guardados em UTC com o fuso contextual (RB-014).
- RF-008-A6 (I): Dada sobreposição com outra sessão do mesmo bebê, então aplica-se a política de D-05 (teste bloqueado até a decisão).
- RF-008-A7 (E2E): Dado o app offline, quando o registro é salvo, então persiste no banco local com estado "pendente" e sem perda após reiniciar.

**RF-009 — Iniciar/parar timer de sono e corrigir registros retroativamente.** Rastreio: RB-003, RF-020; J2, J7.
- RF-009-A1 (E2E): Dado nenhum sono em aberto, quando o usuário inicia o timer, então uma sessão é criada com início agora, sem fim, e `SleepStarted` é emitido.
- RF-009-A2 (E2E): Dado um timer em curso, quando para, então o fim é gravado, a duração calculada e `SleepEnded` emitido.
- RF-009-A3 (I): Dado timer em curso, quando se tenta iniciar outro para o mesmo bebê, então é recusado ou o usuário é levado ao existente (INV-02).
- RF-009-A4 (E2E): Dado timer em curso e app fechado ou reiniciado, então o timer continua contando a partir do início persistido.
- RF-009-A5 (I): Dado um dispositivo offline, o timer inicia e para localmente e sincroniza depois.
- RF-009-A6 (E2E): Dada uma sessão passada, quando o usuário edita início, fim ou tipo, então a alteração é salva, `SleepUpdated` é emitido e totais e previsão são recalculados (RB-003).
- RF-009-A7 (I): Dado um registro retroativo manual (sem timer), então `source = manual`.
- RF-009-A8 (I): Dois cuidadores iniciam timers simultâneos offline para o mesmo bebê, então na sincronização o conflito é tratado por RF-047.

**RF-010 — Calcular duração, total diário, número de sonecas, despertares e wake windows.** Rastreio: RB-001, RB-003, RNF-005; domain-model INV-04.
- RF-010-A1 (U): Dada uma sessão fechada, então a duração é fim menos início.
- RF-010-A2 (U): Dadas três sonecas de 45, 60 e 30 minutos num dia, então o total diurno = 135 min e o número de sonecas = 3.
- RF-010-A3 (U): Dadas duas sessões consecutivas, então a wake window é o intervalo entre o fim da primeira e o início da segunda.
- RF-010-A4 (U): Dado sono noturno com despertares, então a contagem de despertares segue o modelo de D-12 `[teste bloqueado]`.
- RF-010-A5 (U): Dada uma sessão que cruza meia-noite, então a atribuição aos dias segue D-13 `[teste bloqueado]`.
- RF-010-A6 (U): Dada uma sessão em aberto, então ela não entra nos totais fechados nem nas wake windows e é exibida como "em andamento".
- RF-010-A7 (I): Os valores derivados são recalculados a partir dos eventos e nunca persistidos como verdade independente.
- RF-010-A8 (I): Dada a troca de fuso do bebê, os dias dos totais usam o fuso vigente na data do evento (RB-014).

**RF-011 — Gerar previsão de próxima soneca e bedtime.** Rastreio: RB-001, RB-002, RNF-014, ADR-0004, S2; J4.
- RF-011-A1 (U): Dado um bebê sem histórico (cold start), então a previsão usa apenas a referência por idade e a confiança é baixa.
- RF-011-A2 (U): Dado histórico recente suficiente `[a definir: volume mínimo: D-20]`, então a previsão usa a mediana ponderada das wake windows recentes ajustada pela referência por idade.
- RF-011-A3 (U): Dada a mesma entrada (histórico + idade + parâmetros), então a saída é determinística (reprodutível).
- RF-011-A4 (U): Dado o bebê com idade corrigida aplicável, então a faixa etária usa a idade efetiva (RIC-02). Teste da política bloqueado por D-01.
- RF-011-A5 (I): Dada uma previsão, então ela é uma entidade separada e nunca altera ou cria SleepSession (RB-001).
- RF-011-A6 (M): O texto da previsão usa linguagem probabilística ("por volta de", "provavelmente") e traz aviso contextual de que não é diagnóstico nem garantia (RNF-014).
- RF-011-A7 (I): A previsão vem com `model_version`, `computed_at` e tipo (`next_nap` | `bedtime`).
- RF-011-A8 (I): Com timer de sono em curso, não se exibe previsão de próxima soneca; exibe-se o sono atual.
- RF-011-A9 (U): As tabelas de referência por idade vêm de configuração versionada e validada por especialista (S2, ADR-0004); sem valores hardcoded no código.
- RF-011-A10 (E2E, offline): Dado o app offline, então o cliente exibe ao menos a previsão baseline por idade (ADR-0004) e sinaliza que não é a do servidor.

**RF-012 — Recalcular previsões quando um evento de sono é criado, alterado ou excluído.** Rastreio: RB-001, RB-003, INV-05/06, RNF-011; J2, J7.
- RF-012-A1 (I): Dado um evento de sono criado, alterado ou excluído, então `SleepPredictionRecalculated` é emitido e a previsão vigente é substituída pela nova.
- RF-012-A2 (E2E): Dado o fim de uma soneca editado, então wake window, totais e próxima previsão mudam coerentemente, sem alterar o registro real (critério de alto nível, discovery 14).
- RF-012-A3 (I): Dada uma alteração de DPP, nascimento ou preferências (RF-014), então as previsões também são recalculadas.
- RF-012-A4 (I): Dadas duas mutações em sequência rápida, então prevalece o cálculo sobre o estado final (sem corrida).
- RF-012-A5 (I): Dada uma exclusão retroativa, então avisos já agendados sobre a previsão antiga são cancelados ou reagendados (INV-24).
- RF-012-A6 (I): O recálculo é idempotente: reexecutá-lo com os mesmos dados dá o mesmo resultado.

**RF-013 — Exibir confiança e explicação simples da previsão quando tecnicamente possível.** Rastreio: RNF-014, ADR-0004.
- RF-013-A1 (U): Dado volume e variância do histórico, então a confiança é calculada de modo determinístico e mapeada a faixas (ex.: baixa, média, alta; rótulos a validar com UX).
- RF-013-A2 (M): Dada a previsão, então a UI mostra a faixa de confiança e uma explicação curta e localizada (ex.: "baseada na idade" no cold start; "baseada nas últimas sonecas" com histórico).
- RF-013-A3 (I): Dado que a confiança não pode ser calculada, então a UI omite o indicador sem erro.
- RF-013-A4 (M): A explicação não faz afirmação diagnóstica nem compara com outras crianças (RB-013).

**RF-014 — Configurar meta/estrutura de número de sonecas e faixa preferida de bedtime.** Rastreio: RB-002, RB-010; J4.
- RF-014-A1 (I): Dado um Owner ou Caregiver, quando define meta de sonecas e faixa de bedtime, então ficam salvas por bebê e sincronizam com os outros cuidadores.
- RF-014-A2 (U): Dada faixa de bedtime com início posterior ao fim `[sem virada de meia-noite]` ou fora de intervalo plausível `[a definir: D-21]`, então a validação recusa.
- RF-014-A3 (I): Dadas preferências salvas, então o bedtime previsto respeita a faixa e a previsão é recalculada (RF-012).
- RF-014-A4 (I): Dadas as preferências removidas, então a previsão volta ao comportamento padrão por idade.
- RF-014-A5 (I): ReadOnly não altera as preferências.
- RF-014-A6 (M): A meta é tratada como alvo do cuidador, nunca como padrão que a criança "deveria" cumprir (RB-005).

### 5.4 Alimentação, fralda, pumping e timeline

**RF-015 — Registrar amamentação por lado, início/fim e duração.** Rastreio: RB-002, RB-003; J3.
- RF-015-A1 (I): Dado um Owner ou Caregiver, quando informa lado (esquerdo, direito ou ambos), início e fim, então o registro é criado com duração calculada.
- RF-015-A2 (E2E): Dado o modo timer, quando o usuário inicia e para a mamada, então início e fim são gravados e `FeedingLogged` emitido.
- RF-015-A3 (U): Dado fim anterior ao início ou lado ausente, então a validação recusa.
- RF-015-A4 (E2E): Dado o app offline, o registro persiste local e sincroniza depois.
- RF-015-A5 (I): Dado o registro, então aparece na timeline e nos gráficos de alimentação.

**RF-016 — Registrar mamadeira com volume, tipo de leite opcional e horário.** Rastreio: RB-002; J3.
- RF-016-A1 (I): Dados horário e volume em ml, quando salva, então o registro é criado.
- RF-016-A2 (U): Dado volume zero, negativo ou acima do limite plausível `[a definir: D-22]`, então a validação recusa.
- RF-016-A3 (U): Dado tipo de leite ausente, então o registro é válido; dado presente, deve estar entre os valores permitidos `[a definir: D-08]`.
- RF-016-A4 (I): Dada unidade de exibição preferida (ml/oz), então a persistência permanece em ml (RNF-010).
- RF-016-A5 (E2E): Offline: persiste local e sincroniza.

**RF-017 — Registrar pumping/extração com duração e volume opcional.** Rastreio: RB-002; J3.
- RF-017-A1 (I): Dados início e fim, quando salva, então a duração é calculada.
- RF-017-A2 (U): Dado volume ausente, então é válido; dado volume informado, deve ser maior que zero (INV-07).
- RF-017-A3 (U): Dado fim anterior ao início, então recusa.
- RF-017-A4 (E2E): Offline: persiste local e sincroniza; `PumpingLogged` emitido.

**RF-018 — Registrar fralda com tipo e observações opcionais.** Rastreio: RB-002; J3.
- RF-018-A1 (I): Dado o horário e o tipo, quando salva, então o registro é criado.
- RF-018-A2 (U): Dado tipo fora do conjunto permitido `[a definir: D-08]`, então recusa; dadas observações ausentes, é válido.
- RF-018-A3 (E2E): O registro em até dois toques após abrir a tela de registro `[meta de UX; confirmar: D-23]`.
- RF-018-A4 (E2E): Offline: persiste local e sincroniza; `DiaperLogged` emitido.

**RF-019 — Exibir timeline cronológica unificada de eventos.** Rastreio: RNF-005, RNF-007, RNF-009; J2, J3, J7.
- RF-019-A1 (E2E): Dados eventos de sono, amamentação, mamadeira, pumping e fralda, então a timeline os lista em ordem cronológica decrescente num único fluxo, com tipo distinguível por ícone e texto (não só por cor).
- RF-019-A2 (E2E): Dado o app offline, então a timeline abre a partir do banco local de forma imediata (RNF-005) `[meta de abertura: D-24]`.
- RF-019-A3 (I): Dado evento removido por tombstone, então ele não aparece.
- RF-019-A4 (I): Dado grande volume, então a paginação ou a janela de carga mantém a rolagem fluida e p95 da API de listagem < 500 ms (RNF-005).
- RF-019-A5 (I): Dados eventos de dois cuidadores, então todos aparecem, com autoria quando houver mais de um cuidador no bebê.
- RF-019-A6 (M): Dado o VoiceOver/TalkBack, cada item é lido com tipo, horário e resumo (RNF-009).
- RF-019-A7 (I): Sessão em aberto aparece no topo como "em andamento".
- RF-019-A8 (U): Horários exibidos no fuso do bebê, não no do dispositivo, com indicação quando diferem.

**RF-020 — Editar/excluir eventos com trilha de auditoria mínima para dados compartilhados.** Rastreio: RB-003, RB-008, RF-055; J7.
- RF-020-A1 (I): Dado um evento, quando um Owner ou Caregiver o edita, então a versão é incrementada, `last_modified_by` e `updated_at` são registrados e um AuditEvent é criado.
- RF-020-A2 (I): Dado um evento excluído, então vira tombstone, some da timeline e dos gráficos e um AuditEvent é criado (RB-008).
- RF-020-A3 (I): ReadOnly não edita nem exclui.
- RF-020-A4 (I): O AuditEvent não contém o conteúdo livre do evento (INV-28).
- RF-020-A5 (E2E): Dada a edição ou exclusão de evento de sono, então totais e previsão são recalculados (RB-003).
- RF-020-A6 (M): Antes de excluir, a UI pede confirmação.
- RF-020-A7 (I): Em bebê com mais de um cuidador, o histórico mínimo mostra quem alterou e quando, para Owner e Caregiver (alcance exato em D-25).

### 5.5 Gráficos essenciais

**RF-028 — Dashboards de sono, alimentação e cuidados por dia/semana/mês.** Rastreio: RB-001, RB-003, RNF-005, RNF-009; J6.
- RF-028-A1 (I): Dados eventos reais, quando o usuário escolhe dia, semana ou mês, então o gráfico mostra: sono (total, número de sonecas, despertares), alimentação (contagens e volume/duração) e fraldas (contagem), nas métricas listadas no discovery N-F10.
- RF-028-A2 (I): Dado um período sem dados, então a UI mostra estado vazio explicativo, não erro nem zero enganoso.
- RF-028-A3 (I): Dada edição ou exclusão de evento, então o gráfico reflete o novo estado após sincronização/recálculo (RB-003).
- RF-028-A4 (U): Os totais respeitam o fuso do bebê e a regra D-13 para sessões que cruzam o dia.
- RF-028-A5 (M): Cada gráfico tem alternativa acessível (resumo textual ou tabela) e não depende só de cor (RNF-009).
- RF-028-A6 (E2E): Offline: os gráficos usam dados locais, ou indicam claramente o que está indisponível `[a decidir: D-26]`.
- RF-028-A7 (I): Agregações servidas pelo BE-009 com p95 < 500 ms (RNF-005).

**RF-029 — Médias, tendências e comparações com o próprio histórico do bebê.** Rastreio: RB-013, RB-005, RNF-014; J6.
- RF-029-A1 (U): Dado histórico suficiente `[a definir: D-27]`, então o app calcula a média do período e a variação em relação ao período anterior do mesmo bebê.
- RF-029-A2 (M): A comparação é sempre com o próprio histórico; nenhuma tela exibe ranking, percentil ou comparação com outros bebês (RB-013).
- RF-029-A3 (M): Mudanças são descritas de modo neutro e informativo, sem rotular o bebê como "atrasado", "adiantado" ou com diagnóstico (RNF-014).
- RF-029-A4 (U): Dado histórico insuficiente, então a UI diz que ainda não há dados suficientes, sem inventar tendência.
- RF-029-A5 (U): Médias ignoram sessões em aberto e eventos excluídos.

### 5.6 Notificações e preferências

**RF-037 — Configurar notificações de próxima soneca, bedtime, rotina e fases de desenvolvimento.** Rastreio: RB-010, RNF-014; discovery 10; J4.
- RF-037-A1 (I): Dado o usuário, quando liga ou desliga cada categoria (soneca, bedtime, rotina) por bebê, então a preferência é salva por usuário e bebê.
- RF-037-A2 (E2E): Dada soneca prevista às T e antecedência X, quando a categoria está ativa, então o aviso é agendado para T − X e entregue no push.
- RF-037-A3 (I): Dada nova previsão, então o job anterior é cancelado e um novo é agendado (INV-24).
- RF-037-A4 (I): Dado um evento real registrado que torna o aviso obsoleto (ex.: o bebê já dormiu), então o aviso pendente é cancelado.
- RF-037-A5 (M): O texto do aviso usa linguagem probabilística e não diagnóstica (RNF-014).
- RF-037-A6 (I): A categoria "fases de desenvolvimento" existe na configuração, mas nenhum envio ocorre no MVP, pois o conteúdo é V1 (D-10).
- RF-037-A7 (E2E): Sem permissão de push do sistema, o app explica como habilitar e as preferências continuam salvas.
- RF-037-A8 (I): Aviso de rotina é um lembrete opcional configurado pelo usuário `[estrutura do lembrete: D-28]`.

**RF-038 — Quiet hours, antecedência e categorias.** Rastreio: RB-010; J4.
- RF-038-A1 (U): Dada antecedência configurável por categoria `[limites: D-29]`, então o aviso é agendado de acordo.
- RF-038-A2 (I): Dado um aviso cujo horário cai dentro de quiet hours, então não é entregue nesse intervalo; comportamento (adiar ou descartar) conforme D-30; registra-se status `skipped_quiet_hours` ou adiamento.
- RF-038-A3 (U): Quiet hours que cruzam a meia-noite (ex.: 22:00 a 06:00) funcionam corretamente.
- RF-038-A4 (I): Quiet hours e horário dos avisos usam o fuso do bebê/família, inclusive com mudança de horário de verão (RB-010).
- RF-038-A5 (I): Avisos de sistema e segurança `[a decidir se ignoram quiet hours: D-30]`.
- RF-038-A6 (I): Preferências por usuário, não afetando outros cuidadores do mesmo bebê.

**RF-039 — Entregar notificações de forma idempotente e registrar status de envio.** Rastreio: RNF-011, RNF-008; INV-22.
- RF-039-A1 (I): Dado um evento de notificação com `event_key`, quando o job é reprocessado após falha ou retry, então só uma entrega chega ao usuário.
- RF-039-A2 (I): Cada NotificationJob registra estado (scheduled, sent, failed, cancelled, skipped) e tentativas.
- RF-039-A3 (I): Dada falha transitória do provedor (APNs/FCM), então há retry com backoff; esgotadas as tentativas, vai para dead-letter e gera alerta operacional.
- RF-039-A4 (I): Token de dispositivo inválido ou expirado é removido e não gera novas tentativas.
- RF-039-A5 (I): Logs de entrega não contêm PII nem texto de conteúdo (RNF-008).
- RF-039-A6 (I): Jobs cancelados por nova previsão não são enviados.

**RF-040 — Visualizar e alterar preferências de privacidade e comunicação.** Rastreio: RNF-002, RNF-003, RF-003; J9.
- RF-040-A1 (E2E): O usuário vê e altera consentimentos opcionais (ex.: analytics não essencial) e preferências de comunicação (push, e-mail), cada um separado.
- RF-040-A2 (I): Cada alteração gera ConsentRecord/AuditEvent quando se trata de consentimento (RF-055).
- RF-040-A3 (I): Revogar analytics interrompe imediatamente o envio de novos eventos (RF-048).
- RF-040-A4 (E2E): A tela dá acesso a exportação (RF-044), exclusão (RF-045) e sessões (RF-054).
- RF-040-A5 (M): Termos e política de privacidade estão acessíveis na versão aceita e na vigente.

### 5.7 Assinatura

**RF-041 — Plano gratuito e um ou mais planos premium com entitlement server-side.** Rastreio: RB-009; INV-25; J8; limites em D-02.
- RF-041-A1 (I): Dado um novo usuário, então está no plano free por padrão e usa todas as funções definidas como gratuitas `[a definir: D-02]`.
- RF-041-A2 (C): Dado um recurso premium, quando o cliente o solicita, então o servidor verifica o entitlement e recusa sem ele (RB-009); alterar flags no cliente não libera recurso.
- RF-041-A3 (I): Dado um premium ativo, quando a assinatura expira, então o entitlement é revogado pelo servidor e os dados já registrados permanecem acessíveis para leitura e exportação (RF-044).
- RF-041-A4 (M): O paywall informa preço, período, renovação e como cancelar, conforme regras de cada loja.
- RF-041-A5 (I): `SubscriptionActivated` e `SubscriptionExpired` são emitidos nas transições.
- RF-041-A6 (I): O app obtém o entitlement vigente do servidor após login, e o armazena em cache, com expiração, para uso offline `[política offline: D-31]`.

**RF-042 — Compras Apple e Google, restauração e validação de recibo.** Rastreio: RB-009, INV-26; J8.
- RF-042-A1 (I): Dada compra concluída na loja (StoreKit/Play Billing), então o cliente envia o recibo ao backend, que valida com a loja e só então ativa o entitlement.
- RF-042-A2 (I): Dado recibo inválido, reutilizado por outra conta ou adulterado, então nenhum entitlement é concedido.
- RF-042-A3 (E2E): Dado novo dispositivo, quando o usuário toca em "restaurar compras", então o entitlement é recuperado sem nova cobrança (discovery 14).
- RF-042-A4 (I): Notificações de loja (renovação, cancelamento, reembolso) atualizam a Subscription no servidor `[mecanismo: ARCH-001]`.
- RF-042-A5 (I): Falha de rede durante a compra não perde a transação: reconciliação ao reabrir.
- RF-042-A6 (I): Uma mesma compra de loja não vincula duas contas simultaneamente.

**RF-043 — Compartilhar entitlement premium conforme política familiar.** Rastreio: RB-009; D-04; discovery N-F18.
- RF-043-A1 (I): Dada a política familiar definida `[bloqueado por D-04]`, quando o Owner premium tem cuidadores vinculados, então o entitlement efetivo dos cuidadores segue exatamente essa política.
- RF-043-A2 (I): Dado um cuidador revogado, então perde o acesso premium herdado imediatamente.
- RF-043-A3 (I): A herança é calculada no servidor, nunca declarada pelo cliente (RB-009).
- RF-043-A4 (M): A UI informa de quem é a assinatura e o que é compartilhado.

### 5.8 LGPD: exportar e excluir

**RF-044 — Exportar dados pessoais em formato estruturado e legível.** Rastreio: RB-014, RNF-003, discovery 12 e 14; J9.
- RF-044-A1 (I): Dado o usuário autenticado, quando solicita exportação, então `DataExportRequested` é emitido e o processamento é assíncrono.
- RF-044-A2 (I): O arquivo contém conta, consentimentos, bebês dos quais é Owner e seus eventos, preferências e vínculos, com timestamps UTC, fuso e `schema_version` (RB-014).
- RF-044-A3 (I): O formato é estruturado (JSON ou CSV) e acompanhado de resumo legível `[formatos: D-32]`.
- RF-044-A4 (I): O download é por URL assinada de curta duração, acessível só ao titular, e expira.
- RF-044-A5 (I): Dados de outros usuários (ex.: e-mail de outro cuidador) não vazam além do permitido `[escopo: D-33]`.
- RF-044-A6 (I): Um AuditEvent registra a solicitação e a entrega (RF-055).
- RF-044-A7 (E2E): O usuário recebe aviso de que a exportação está pronta.

**RF-045 — Solicitar exclusão da conta e dos dados, observando retenções legais.** Rastreio: RNF-003, INV-31, RB-007; J9; D-07.
- RF-045-A1 (E2E): Dado o usuário autenticado, quando solicita exclusão e confirma (reautenticação), então `AccountDeletionRequested` é emitido, as sessões são encerradas e o estado muda conforme D-07.
- RF-045-A2 (I): Concluída a exclusão, os dados pessoais do titular são removidos ou anonimizados, exceto o que a lei exigir, e a retenção é documentada.
- RF-045-A3 (I): Dado um Owner de bebê com outros cuidadores, então a exclusão exige transferir a propriedade ou decidir o destino do perfil (RB-007) `[fluxo: D-34]`.
- RF-045-A4 (I): Dado um cuidador não Owner, então a exclusão remove o vínculo e os dados pessoais dele, mas não apaga os eventos do bebê que ele registrou `[a validar: D-34]`.
- RF-045-A5 (I): Backups e logs seguem a retenção definida (RNF-012) e a exclusão em backups é refletida no prazo declarado `[prazo: D-07]`.
- RF-045-A6 (I): Assinaturas ativas na loja não são canceladas automaticamente; o app avisa o usuário.
- RF-045-A7 (I): Um AuditEvent mínimo e sem PII registra o pedido e a conclusão.

### 5.9 Offline e sincronização

**RF-046 — Operar tracking essencial offline e sincronizar depois.** Rastreio: RNF-007, RNF-005, ADR-0003; critério de alto nível 1 do discovery 14; J2.
- RF-046-A1 (E2E): Dado o dispositivo sem rede, o usuário consegue registrar sono (timer e manual), amamentação, mamadeira, pumping e fralda, ver a timeline e editar/excluir eventos.
- RF-046-A2 (E2E): Dado o registro offline, quando a rede volta, então as mutações são enviadas, confirmadas e o estado passa de "pendente" para "confirmado".
- RF-046-A3 (I): Cada mutação tem UUID, `client_created_at`, `base_version` e `device_id`; o reenvio do mesmo UUID não duplica o evento (RNF-007).
- RF-046-A4 (E2E): Dado o app fechado ou encerrado com mutações pendentes, então elas permanecem na fila e são enviadas ao reabrir.
- RF-046-A5 (E2E): Dada interrupção da rede no meio do envio, então nada é perdido nem duplicado após retomar.
- RF-046-A6 (M): A UI sinaliza o estado de sincronização (pendente, sincronizado, erro) sem bloquear o registro.
- RF-046-A7 (E2E): Dado o registro sono em poucos toques offline e visível em outro dispositivo após reconexão (critério de alto nível, discovery 14), então o fluxo completo é demonstrado com dois dispositivos.
- RF-046-A8 (E2E): Dado o relógio do dispositivo errado, o servidor aceita a mutação mas mantém `client_created_at` e sinaliza anomalia grande de relógio `[tratamento: D-35]`.

**RF-047 — Resolver conflitos de sincronização por versão/timestamp e regras por entidade.** Rastreio: RB-008, RNF-007, ADR-0003; INV-18..21.
- RF-047-A1 (I): Dois cuidadores criam eventos distintos offline, então ambos entram (merge por entidade, sem perda).
- RF-047-A2 (I): Dois cuidadores editam o mesmo evento, então prevalece last-write-wins por campo e a resolução é registrada em auditoria (ADR-0003, INV-21).
- RF-047-A3 (I): Dada edição de um lado e exclusão de outro, então a política definida resolve de forma determinística `[a decidir: D-36]` e fica auditada.
- RF-047-A4 (I): Dado dispositivo atrasado que reenvia update de entidade excluída, então a entidade não ressuscita (tombstone, RB-008).
- RF-047-A5 (I): O servidor atribui a versão canônica monotônica por bebê; o pull por cursor devolve só mudanças posteriores.
- RF-047-A6 (I): Cursor inválido ou expirado `[janela de tombstone: D-06]` faz o cliente refazer o pull completo, sem perda.
- RF-047-A7 (I): Convergência: após trocar todas as mutações, todos os dispositivos terminam no mesmo estado.
- RF-047-A8 (I): Conflitos em sono (RF-009-A8) acionam recálculo de previsão.

### 5.10 Analytics sem PII

**RF-048 — Registrar analytics de produto sem conteúdo sensível desnecessário.** Rastreio: RB-011, RNF-002, RNF-008; discovery 11 e 12.
- RF-048-A1 (C): Os eventos seguem a taxonomia do discovery seção 11 restrita ao MVP (onboarding_started, baby_profile_created, caregiver_invited, sleep_timer_started, sleep_logged, prediction_viewed, prediction_outcome, feeding_logged, notification_opened, paywall_viewed, trial_started, subscription_started), com propriedades apenas das permitidas; eventos de V1/V2 não são enviados.
- RF-048-A2 (C): Nenhum evento carrega nome ou apelido do bebê, e-mail, notas livres, fotos, ou identificadores sensíveis; IDs são pseudonimizados e valores são bucketizados (ex.: `age_bucket`, `duration_bucket`).
- RF-048-A3 (I): Teste automatizado de esquema rejeita eventos com campos fora da lista permitida.
- RF-048-A4 (I): Dado analytics não essencial sem consentimento ou revogado, então nenhum evento não essencial é enviado (RF-040-A3).
- RF-048-A5 (I): Ferramentas de terceiros recebem somente o conjunto minimizado; o fornecedor consta no RIPD `[PRIV-001]`.
- RF-048-A6 (U): `prediction_outcome` mede a diferença entre previsão e evento real em buckets, sem os horários exatos.

### 5.11 Internacionalização

**RF-050 — Suporte a múltiplos idiomas e conteúdo localizado.** Rastreio: RNF-010, S1; J1.
- RF-050-A1 (M/I): Nenhuma string visível está hardcoded; todas vêm de recursos de localização em PT-BR, EN e ES; PT-BR é completo no lançamento e EN/ES conforme o planejamento `[D-37]`.
- RF-050-A2 (U): Pluralização, datas, horas, unidades (ml/oz) e formatos numéricos seguem o locale; verificado com pseudo-locale e testes por idioma.
- RF-050-A3 (E2E): O idioma segue o do sistema e pode ser alterado nos ajustes; na falta de uma chave usa-se fallback para PT-BR sem exibir a chave bruta.
- RF-050-A4 (I): Textos de aviso (notificações, explicações da previsão, disclaimers) são localizados e mantêm a linguagem probabilística em todos os idiomas.
- RF-050-A5 (I): Mensagens do servidor expostas ao usuário usam códigos e o cliente localiza.
- RF-050-A6 (M): Layouts suportam textos até 40% maiores sem truncar ações essenciais (RNF-009, RNF-010).

### 5.12 Sessões e auditoria

**RF-054 — Histórico de dispositivos/sessões e revogação.** Rastreio: RNF-001, RB-015; J9, J10.
- RF-054-A1 (E2E): O usuário vê a lista de sessões ativas com dispositivo/plataforma, data de criação e último uso aproximado, sem expor dados sensíveis desnecessários.
- RF-054-A2 (I): Quando o usuário revoga uma sessão, então tokens daquele dispositivo deixam de funcionar imediatamente e a próxima chamada recebe 401.
- RF-054-A3 (I): O usuário não vê nem revoga sessões de outra conta.
- RF-054-A4 (I): A sessão atual é identificada na lista; revogá-la equivale a sair.
- RF-054-A5 (I): Login em dispositivo novo gera aviso de segurança ao usuário (categoria sistema, discovery 10) `[canal: D-38]`.
- RF-054-A6 (I): Dados locais não sincronizados em dispositivo revogado não são enviados com a sessão revogada; o usuário é avisado antes de sair `[tratamento: D-39]`.

**RF-055 — Auditoria de alterações críticas.** Rastreio: RB-007, RB-015, RNF-003, RNF-008; INV-28.
- RF-055-A1 (I): Geram AuditEvent: criação/exclusão de conta, mudança de e-mail/senha, convite, aceite, remoção e mudança de papel de cuidador, transferência de propriedade, consentimentos (concessão e revogação), solicitações de exportação e exclusão, compartilhamento e exclusão de bebê/evento compartilhado.
- RF-055-A2 (I): Cada AuditEvent registra ator, ação, entidade, `timestamp` e `metadata_safe`, sem PII nem conteúdo livre.
- RF-055-A3 (I): AuditEvent é imutável (somente inserção); tentativa de editar ou apagar falha.
- RF-055-A4 (I): Só os perfis autorizados consultam a auditoria `[quem e como: D-40]`; o usuário vê os eventos relativos à própria conta e bebês de que é Owner.
- RF-055-A5 (I): A retenção segue a política definida (D-07) e é aplicada por job.

## 6. Matriz de rastreabilidade RF para regras

Células: RB / RNF / ADR relevantes (critérios completos na seção 5).

| RF | RB | RNF | Outros |
|---|---|---|---|
| 001 | | 001, 003, 008 | |
| 002 | 015 | 001 | RF-054 |
| 003 | 011 | 002, 003 | |
| 004 | 004, 006, 014 | 002 | |
| 005 | 004, 005, 013 | 014 | domain-model 6 |
| 006 | 006, 007, 015 | | |
| 007 | 006, 015 | 007 | ADR-0003 |
| 008 | 002, 003, 014 | 007 | |
| 009 | 003 | 007 | |
| 010 | 001, 003 | 005 | |
| 011 | 001, 002 | 014 | ADR-0004 |
| 012 | 001, 003 | 011 | |
| 013 | 013 | 014 | ADR-0004 |
| 014 | 002, 010 | | |
| 015..018 | 002, 003 | 007 | |
| 019 | 008 | 005, 007, 009 | |
| 020 | 003, 008 | | RF-055 |
| 028 | 001, 003 | 005, 009 | |
| 029 | 005, 013 | 014 | |
| 037 | 010 | 014 | discovery 10 |
| 038 | 010 | | |
| 039 | | 008, 011 | |
| 040 | 011 | 002, 003 | |
| 041..043 | 009 | | |
| 044 | 014 | 003 | |
| 045 | 007 | 003, 012 | |
| 046 | 008 | 005, 007 | ADR-0003 |
| 047 | 008 | 007 | ADR-0003 |
| 048 | 011 | 002, 008 | discovery 11 |
| 050 | | 010 | |
| 054 | 015 | 001 | |
| 055 | 007, 015 | 003, 008 | |

RNF globais que se aplicam a todos os RF: RNF-001 (segurança), RNF-002/003 (privacidade/LGPD), RNF-005 (performance), RNF-009 (acessibilidade), RNF-014 (conteúdo de saúde), RNF-015 (manutenibilidade).

## 7. Critérios de aceite de alto nível do discovery e cobertura

| Critério (discovery 14) | Coberto por |
|---|---|
| Registrar sono offline em poucos toques e vê-lo sincronizado em outro dispositivo | RF-046-A7, RF-007-A1 |
| Alterar fim de soneca recalcula sem modificar o registro | RF-012-A2 |
| Não autorizado nunca recebe dados por ID, cache, push ou mídia | RF-004-A6, RF-007-A2, A6 |
| Revogar cuidador invalida acesso e sessões | RF-006-A5, RF-007-A3 |
| Notificações não duplicam e recalculam | RF-039-A1, RF-037-A3 |
| Exportação estruturada | RF-044 |
| IA sem diagnóstico | fora do MVP (sem IA) |
| Assinatura restaurada sem duplicar cobrança | RF-042-A3 |
| Troca de timezone não corrompe histórico | RF-010-A8, RF-005-A7, INV-17 |

## 8. Requisitos transversais ao MVP e gates

- Gates (specification.md): API, Accessibility, Privacy, Performance, Operational Readiness, Release. `SECURITY-REVIEW-001` bloqueia o release (auth, multi-tenant, dado de criança, exposição pública).
- Disclaimer contextual de saúde (RNF-014) em previsão, explicações e avisos.
- Contratos OpenAPI (API-001) congelados antes do trabalho paralelo dos clientes.
- Cada RF deve ser rastreável até feature, endpoints/eventos, entidades e testes (discovery 17).

## 9. Dúvidas abertas

Nenhuma bloqueia a escrita da spec; as marcadas "bloqueia teste" impedem a verificação do critério citado.

| ID | Dúvida | Afeta | Dono sugerido |
|---|---|---|---|
| D-01 | **[resolvido em parte pelo ADR-0009: janela de aplicação é parâmetro editável; valor padrão ainda a definir com especialista]** **Política de idade corrigida para prematuros**: critério de "aplicável" (idade gestacional não é campo do discovery), limite de idade, opt-out manual, uso no motor de sono. Bloqueia teste. | RF-005, RF-011 | produto + especialista clínico |
| D-02 | Preço e limites do plano free × premium (o que é gratuito). | RF-041 | product-owner |
| D-03 | Cloud alvo, provedor de push (APNs/FCM diretos) e revisão jurídica/LGPD (Privacy Gate) antes de usuários reais; base legal por finalidade. | RF-039, RF-003, RF-040 | cloud, privacidade |
| D-04 | Entitlement é por usuário ou por família/bebê? Política de herança para cuidadores. Bloqueia teste. | RF-041, RF-043 | produto |
| D-05 | Sobreposição de sessões de sono: rejeitar, mesclar ou sinalizar. Bloqueia teste. | RF-008 | produto + UX |
| D-06 | Janela de retenção de tombstone e formato do cursor (pendências do ADR-0003). | RF-047 | backend-architect |
| D-07 | **[janela de arrependimento confirmada (ADR-0009); prazos de retenção seguem com o jurídico]** Janela de arrependimento da exclusão, prazos de retenção e o que a lei obriga reter. | RF-045, RF-055 | privacidade/jurídico |
| D-08 | **[RESOLVIDO (ADR-0009): DiaperType, FeedingType, MilkType; método/local de sono segue aberto]** Conjuntos de valores: tipos de fralda, tipos de leite, método/local de sono. Bloqueia teste. | RF-008, 016, 018 | produto + UX |
| D-09 | Foto de perfil do bebê: armazenamento e tratamento de mídia no MVP. | RF-004 | arquitetura |
| D-10 | Categoria "fases de desenvolvimento" em RF-037 sem conteúdo no MVP: exibir desabilitada ou ocultar. | RF-037 | produto |
| D-11 | Permitir perfil pré-natal (só DPP, sem nascimento)? | RF-004 | produto |
| D-12 | **[RESOLVIDO (ADR-0009): WakeEvent + night_awakenings derivado (null/0/N)]** Como registrar e contar despertares noturnos. Bloqueia teste. | RF-010 | produto |
| D-13 | Atribuição de sessões que cruzam meia-noite a dias e corte do "dia". Bloqueia teste. | RF-010, RF-028 | produto |
| D-14 | **Contagem**: o pedido cita 45 RFs, mas a lista fornecida e `specification.md` somam 37. Confirmar se faltam RFs no escopo (quais) ou se o número é um erro. | escopo | product-owner |
| D-15 | Personas são hipóteses sem pesquisa; validar com entrevistas. | seção 2 | UX |
| D-16 | Política de senha, limites de tentativa/bloqueio, expiração de token de recuperação, tempo de sessão. | RF-001, 002 | segurança |
| D-17 | Expiração e cancelamento de convite; quem pode cancelar. | RF-006 | produto |
| D-18 | Meta de latência de propagação entre dispositivos (não há RNF explícito). | RF-007 | arquitetura |
| D-19 | Novo cuidador vê o histórico anterior ao ingresso? Padrão proposto: sim; confirmar. | RF-007 | produto + privacidade |
| D-20 | Volume mínimo de histórico para sair do cold start. | RF-011 | sleep engine |
| D-21 | Limites plausíveis de bedtime e de quantidade de sonecas. | RF-014 | especialista |
| D-22 | Limites de volume plausível (mamadeira/pumping). | RF-016 | produto |
| D-23 | Metas de UX (toques para registrar). O discovery diz "poucos toques" sem número. | RF-018, RF-046 | UX |
| D-24 | Meta numérica de abertura da timeline offline ("imediata", RNF-005). | RF-019 | performance |
| D-25 | Alcance da trilha de auditoria visível ao usuário (quem vê o quê). | RF-020 | produto + privacidade |
| D-26 | Gráficos offline: dados locais completos ou parciais. | RF-028 | arquitetura |
| D-27 | Mínimo de dados para médias e tendências. | RF-029 | produto |
| D-28 | Estrutura dos lembretes de "rotina" (horários fixos, recorrência). | RF-037 | produto |
| D-29 | Limites de antecedência dos avisos. | RF-038 | produto |
| D-30 | Quiet hours: adiar ou descartar; avisos de sistema/segurança ignoram quiet hours? | RF-038 | produto |
| D-31 | Política de entitlement em cache offline (validade). | RF-041 | arquitetura |
| D-32 | Formatos finais de exportação e se o resumo legível é PDF. | RF-044 | privacidade |
| D-33 | Escopo da exportação para dados compartilhados com outros cuidadores. | RF-044 | privacidade |
| D-34 | Fluxo de exclusão para Owner com cuidadores e destino dos eventos registrados por quem sai. | RF-045 | produto + jurídico |
| D-35 | Tratamento de relógios de dispositivo errados. | RF-046 | arquitetura |
| D-36 | Política para edição versus exclusão concorrentes. | RF-047 | backend-architect |
| D-37 | Cronograma de EN/ES (RF-050 diz PT/EN/ES, PT primeiro) e responsável pela tradução. | RF-050 | produto |
| D-38 | Canal do aviso de novo login (push, e-mail). | RF-054 | segurança |
| D-39 | O que acontece com dados locais pendentes ao revogar uma sessão. | RF-054 | arquitetura |
| D-40 | Acesso à auditoria por suporte/admin (não há CMS/admin no MVP). | RF-055 | segurança |
| D-41 | Baseline de versões iOS/Android (RNF-016) e meta formal de RPO/RTO (RNF-012). | NFR | arquitetura/SRE |

Suposições vigentes (specification.md): S1 PT-BR primeiro; S2 previsão por regras sem ML; S3 plano free + um premium; S4 conteúdo original (V1); S5 sem IA nem comunidade; S6 monorepo.


## Atualização ADR-0009 (2026-10-08)
Resolvidos: cadastro só ativo após confirmação por e-mail; escrita de eventos via /sync/push; exclusão de conta com janela de arrependimento, só Owner, em cascata por padrão (flag); sono sobreposto aceito e sinalizado (flag); adicional do premium não precisa ser cuidador ativo. Critérios marcados `[teste bloqueado]` por D-08/D-12 podem ser escritos agora; D-01 (valor padrão da janela) e D-13 seguem abertos.
