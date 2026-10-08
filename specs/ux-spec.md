# UX Spec do MVP — Nina (UX-001)

Status: rascunho para revisão. Fontes: `specs/discovery-napper-wonder-weeks.md` (seções 6–15), `docs/project/specification.md`, `docs/project/architecture.md`.
Plataformas: iOS (SwiftUI) e Android (Jetpack Compose), **mesma arquitetura de informação (IA)**; diferenças apenas em convenções nativas (seção 12).
Idioma: PT-BR primeiro; strings externalizadas, preparado para EN/ES (RNF-010, RF-050).

> Nota de PI: o discovery cataloga capacidades públicas. Este documento define telas, textos, cores e componentes **originais**. Não reproduz nomes de fases, textos, ilustrações nem a identidade visual de Napper, The Wonder Weeks ou qualquer concorrente.

---

## 1. Escopo e rastreabilidade

Dentro do MVP: conta/consentimento, bebê, cuidadores, sono (timer + manual), alimentação, fralda, pumping, timeline, agenda e previsão, gráficos essenciais, notificações/quiet hours, paywall, exportar/excluir conta, offline/sync, sessões.
Fora do MVP (não desenhar agora, reservar espaço na IA): desenvolvimento/marcos, diário, conteúdo, áudio, assistente IA, comunidade, Watch/widgets.

| Fluxo | RFs / RBs principais |
|---|---|
| Onboarding, conta, consentimento | RF-001..003, RF-040 |
| Bebê | RF-004, RF-005, RB-004 |
| Cuidadores/convites | RF-006, RF-007, RB-006/007/015 |
| Sono (timer, manual, correção) | RF-008..010, RF-020, RB-001..003 |
| Alimentação, fralda, pumping | RF-015..018 |
| Timeline | RF-019, RF-020, RB-008 |
| Agenda e previsão | RF-011..014, RB-001/002, RNF-014 |
| Gráficos | RF-028, RF-029, RB-013 |
| Notificações | RF-037..040, RB-010 |
| Paywall | RF-041..043, RB-009 |
| Exportar/excluir | RF-044, RF-045 |
| Offline/sync | RF-046, RF-047, RNF-007 |
| Sessões/auditoria | RF-054, RF-055 |

---

## 2. Princípios de design

1. **Uso com uma mão, no escuro, com um bebê no colo.** Ações primárias na metade inferior da tela (zona do polegar); alvos mínimos 48 pt/dp (primários 56); nada crítico no canto superior.
2. **Registrar em ≤3 toques** a partir da abertura do app (meta medida, ver 2.1). Valores padrão inteligentes (agora, último lado, último volume); detalhes são opcionais e vêm depois do salvar.
3. **Madrugada em primeiro lugar.** Modo escuro verdadeiro, baixo brilho, sem branco puro em fundo escuro, sem animações bruscas; tema "Noite" automático por horário (opcional) além do tema do sistema.
4. **Real > previsto** (RB-001/002). Eventos reais têm peso visual cheio; previsões usam traço tracejado/transparência e rótulo textual "previsto". Previsão nunca substitui nem edita um registro.
5. **Linguagem probabilística, sem diagnóstico** (RNF-014, RB-005). Ver seção 13.
6. **Offline é o estado normal.** O app grava local imediatamente e nunca bloqueia o registro por rede; o estado de sync é discreto e informativo, não alarmante.
7. **Erros reversíveis.** Toda ação de registro tem "Desfazer" (snackbar/toast por 8 s); excluir pede confirmação só quando irreversível (conta, bebê, cuidador).
8. **Cansaço cognitivo.** Poucas decisões por tela, texto curto, sem jargão, sem culpa ("faz X dias que não registra" é proibido).
9. **Comparar o bebê com ele mesmo** (RB-013): nenhum ranking, percentil de desenvolvimento ou "acima/abaixo da média de outros bebês".
10. **Privacidade visível:** consentimentos opcionais separados, linguagem clara, revogação a 2 toques em Ajustes.

### 2.1 Metas de interação (verificáveis)

| Tarefa | Meta | Contagem de toques (a partir da Home já aberta) |
|---|---|---|
| Iniciar timer de sono | 1 toque | 1 (botão primário "Dormiu") |
| Parar timer de sono | 1 toque + confirmação implícita | 1–2 |
| Registrar fralda (tipo mais comum) | 2 toques | FAB/ação rápida → tipo |
| Registrar mamadeira com último volume | 2–3 toques | ação → Mamadeira → Salvar |
| Registrar mamada (lado) | 3 toques | ação → Peito → lado (inicia timer) |
| Registro retroativo de sono | ≤ 4 toques + seletor de hora | exceção justificada |

Desde a abertura a frio do app: +0 toques (Home é a tela inicial; sem login a cada abertura; sessão persistente).

---

## 3. Mapa de telas (IA)

Navegação principal: **barra inferior com 4 abas** + botão de ação rápida central/flutuante. Mesma IA em iOS (TabView) e Android (NavigationBar).

```
App
├── Fluxo de entrada (não autenticado)
│   ├── E1 Boas-vindas
│   ├── E2 Criar conta / Entrar / Recuperar acesso
│   ├── E3 Termos, Privacidade e consentimentos
│   ├── E4 Criar bebê (nome, nascimento, data prevista, fuso)
│   ├── E5 Rotina inicial (nº de sonecas, bedtime — opcional)
│   └── E6 Primeiro registro guiado
│
├── Aba 1  HOJE (Home)                       ← tela principal nº 1
│   ├── Cartão "Agora" (estado do bebê: acordado há X / dormindo há X)
│   ├── Cartão "Próximo sono previsto" (+ confiança, "Por quê?")
│   ├── Resumo do dia (sonecas, total de sono, mamadas, fraldas)
│   ├── Ação rápida (sheet): Sono, Mamada, Mamadeira, Fralda, Pumping
│   └── Timer ativo (barra persistente / Live Activity-like no nativo, pós-MVP)
│
├── Aba 2  LINHA DO TEMPO                    ← tela principal nº 2
│   ├── Lista cronológica por dia, filtros por tipo
│   ├── Detalhe/Edição do evento (sheet)
│   └── Histórico de alterações do evento (RF-020, mínimo)
│
├── Aba 3  AGENDA                            ← tela principal nº 3
│   ├── Agenda do dia (sonecas e bedtime previstos × reais)
│   ├── Detalhe da previsão: confiança + explicação
│   └── Ajustar rotina: nº de sonecas, faixa de bedtime (RF-014)
│
├── Aba 4  GRÁFICOS                          ← tela principal nº 4
│   ├── Sono (dia/semana/mês): total, sonecas, noturno, despertares, wake windows
│   ├── Alimentação (mamadas, volumes) e fraldas
│   └── Detalhe do ponto (tooltip acessível + tabela alternativa)
│
├── Mais (acessível por avatar/ícone no topo da Home — não é aba)   ← tela principal nº 5/6 ver 3.1
│   ├── Bebê(s): trocar, editar, adicionar
│   ├── Cuidadores e convites (papéis)
│   ├── Notificações e quiet hours
│   ├── Assinatura (status, restaurar compra) → Paywall
│   ├── Privacidade: consentimentos, exportar dados, excluir conta
│   ├── Sessões e dispositivos (RF-054)
│   ├── Preferências (unidades ml/oz, fuso, tema, idioma)
│   └── Ajuda, sobre, avisos legais de saúde
│
└── Modais globais: Paywall, Aviso de saúde (primeira previsão), Conflito de sync, Confirmações
```

### 3.1 As 6 telas principais (wireframes na seção 5)

1. **Hoje (Home)**
2. **Registro rápido de sono (timer ativo)** — sheet/tela cheia
3. **Linha do tempo**
4. **Agenda e previsão**
5. **Gráficos de sono**
6. **Paywall**

Telas de apoio com tabela de conteúdo na seção 4: onboarding, registro de alimentação/fralda/pumping, cuidadores, notificações, privacidade.

---

## 4. Fluxos

Notação: `[Tela]` → ação → `[Tela]`. Todos os fluxos de registro funcionam offline.

### 4.1 Onboarding → bebê → primeiro registro

1. `[E1 Boas-vindas]` valor em uma frase + "Começar". Link "Já tenho conta".
2. `[E2 Conta]` e-mail + senha (ou método definido por ADR de Identity). Mostra força de senha e erros inline. "Esqueci minha senha" disponível (RF-002).
3. `[E3 Consentimentos]` (RF-003):
   - Obrigatório: aceitar Termos e Política de Privacidade (versões exibidas, link para leitura completa). Sem pré-marcação.
   - Opcionais, **desmarcados por padrão** e separados: "Ajudar a melhorar o Nina com estatísticas de uso (sem dados do seu bebê identificáveis)", "Novidades e dicas por e-mail".
   - Texto: o que é coletado sobre o bebê e para quê, em linguagem simples.
4. `[E4 Criar bebê]` (RF-004/005):
   - Obrigatórios: apelido/nome, data de nascimento.
   - Opcionais: **data prevista do parto** (com texto de apoio: "Ajuda a ajustar a agenda se o bebê nasceu antes de 37 semanas"), sexo (opcional, "Prefiro não informar"), foto, fuso (padrão do aparelho).
   - Idade exibida em semanas/meses; se houver data prevista e nascimento prematuro, mostrar "idade corrigida" como informação secundária (política pendente, ver dúvidas).
5. `[E5 Rotina inicial]` opcional ("Pular"): nº de sonecas por dia e faixa de bedtime. Padrão = sugestão por idade, rotulada "ponto de partida".
6. `[E6 Primeiro registro guiado]` uma só pergunta: "O que aconteceu por último?" com atalhos Dormiu agora / Acordou há pouco / Mamou / Troquei fralda. Salvar leva à Home com um *toast* "Registrado. Quanto mais você registrar, melhor a agenda fica."
7. `[Home]` com estado vazio parcial (4.12). A primeira previsão exibe o **aviso de saúde contextual** uma única vez (modal curto, reabrível em Ajuda).

Critério: do "Começar" ao primeiro registro ≤ 6 telas, ≤ 2 min; cada tela tem "Voltar" e preserva o que foi digitado.

### 4.2 Registro rápido de sono com timer (RF-008/009/010)

- **Iniciar:** Home → botão primário "Dormiu" (1 toque). Cria sessão local com `start = agora`, tipo inferido (soneca × noturno) por horário/rotina, editável depois. A Home passa ao estado **Dormindo** (cartão com cronômetro, botão "Acordou").
- **Parar:** "Acordou" (1 toque) → sheet de confirmação leve com duração, tipo e campos opcionais (local/método, observação). Botão "Salvar" já focado; fechar o sheet salva com os padrões.
- **Esqueci de iniciar/parar:** em "Dormiu" há ação secundária "Foi antes…" (hora de início). Em "Acordou" há "Foi antes…" (hora de fim). Seletor de hora com atalhos: -5, -10, -15, -30 min.
- **Registro manual:** Ação rápida → Sono → "Registrar sono passado": início/fim, tipo.
- **Corrigir:** timeline → evento → editar início/fim/tipo; pré-visualiza o efeito ("Duração 1h 20 · wake window anterior 2h 05").
- **Edição de sobreposição:** se o novo intervalo sobrepõe outro, aviso inline e opções "Ajustar o outro registro" / "Cancelar".
- **Timer em outro aparelho (multi-cuidador):** Home mostra "Dormindo desde 14:02 · iniciado por {nome do cuidador}"; qualquer cuidador com papel Caregiver/Owner pode finalizar. Conflito de dois timers → ver 4.13.
- **Timer longo:** após 6 h em andamento (configurável) pergunta discretamente "O bebê ainda está dormindo?" (sem push noturno; só na próxima abertura).
- Após salvar: previsões recalculadas (RF-012) com transição suave do cartão "Próximo sono"; nenhum evento real é alterado (RB-001).

### 4.3 Alimentação, fralda e pumping (RF-015..018)

Ação rápida (sheet de baixo, 5 botões grandes, ordem por uso recente): Sono · Mamada · Mamadeira · Fralda · Pumping.

| Tipo | Fluxo mínimo | Campos |
|---|---|---|
| Mamada (peito) | Peito → escolhe lado (E/D) → timer inicia; "Trocar de lado" 1 toque; "Terminar" salva | lado(s), início/fim, duração por lado |
| Mamadeira | Mamadeira → volume (stepper com último valor, ±10 ml) → Salvar | volume, tipo de leite (opcional: materno, fórmula, misto), horário (padrão agora) |
| Fralda | Fralda → Xixi / Cocô / Ambos → salvo (1 toque no tipo) | tipo, observação opcional (cor/consistência como texto livre; **sem** interpretação clínica) |
| Pumping | Pumping → timer ou manual → volume opcional → Salvar | início/fim, duração, volume opcional, lado |

Regras comuns: horário padrão = agora e editável antes e depois; "Desfazer" por 8 s; mostrar na Home "última mamada há X" (fato, sem julgamento). Unidades ml/oz conforme preferência.

### 4.4 Timeline (RF-019/020)

- Lista unificada por dia, mais recente em cima; cabeçalho do dia com totais compactos (sono total, nº mamadas, nº fraldas).
- Filtros por chips: Tudo, Sono, Comida, Fralda, Pumping. Estado persiste na sessão.
- Cada item: ícone + forma (não só cor), título, hora (ou intervalo), duração, autor (avatar/iniciais de cuidador quando há mais de um).
- Toque abre detalhe/edição (sheet). Excluir = "Excluir registro" com desfazer; sincroniza como tombstone (RB-008).
- Itens pendentes de sync mostram marcador discreto (4.11).
- Itens editados por outro cuidador mostram "editado por {nome}" e histórico mínimo (RF-020).
- Previsões **não** aparecem na timeline de eventos reais, exceto como marcador pontilhado de "agora → próximo sono previsto" apenas no dia corrente, desativável.
- Rolagem infinita com carregamento local imediato (RNF-005).

### 4.5 Agenda e previsão com confiança e explicação (RF-011..014)

- Aba Agenda: linha vertical do dia com blocos reais (cheios) e previstos (tracejados).
- Cartão "Próximo sono previsto": faixa de horário (não hora exata), ex.: "Entre 14:10 e 14:40".
- **Confiança** em 3 níveis textuais + indicador não só cromático: *Em construção* (poucos dados / cold start), *Razoável*, *Boa*. Nunca porcentagem exata na superfície principal.
- **"Por quê?"** abre sheet com explicação simples (RF-013), por exemplo: "Baseado na idade (5 meses) e nos últimos 7 dias de registros. O bebê costuma ficar acordado cerca de 2h 15 entre sonecas." Mostra quantos registros foram usados. Se cold start: "Ainda estamos usando valores típicos para a idade. Com mais registros, a agenda se ajusta ao seu bebê."
- Cada previsão traz o rótulo "previsto" e um link "Isto é uma estimativa" para o aviso de saúde.
- **Ajustar rotina:** nº de sonecas e faixa preferida de bedtime (RF-014); mudanças mostram a prévia da nova agenda antes de aplicar.
- **Recalculo:** ao criar/editar/excluir sono a agenda anima a mudança (reduzida se "reduzir movimento"), e se uma notificação já agendada mudar, ela é reagendada (RF-039).
- **Quando não há previsão:** estado vazio explicativo (4.12). Nunca exibir previsão sem dado mínimo + idade.
- Previsão passada que não ocorreu: apenas some quando o evento real é registrado ou a janela passa; sem rótulo de "erro" ou "atraso" do bebê.

### 4.6 Gráficos (RF-028/029)

- Seletor de período: Dia · Semana · Mês (segmentado). Navegação por setas e gesto horizontal.
- Sono: barras empilhadas diárias (noturno × sonecas), linha de média do próprio bebê, despertares noturnos, wake windows (distribuição).
- Alimentação/fraldas: contagem por dia, volume total de mamadeira; duração média de mamadas.
- Comparações **apenas com o histórico do próprio bebê** ("esta semana vs. semana anterior"), em texto neutro: "2 sonecas a mais por dia do que na semana passada", sem juízo de valor.
- Cada gráfico tem alternativa tabular acessível ("Ver como tabela") e resumo falado (seção 10).
- Estado com pouco dado: mostra o que existe + "Os gráficos ficam mais úteis com 3 dias de registros."

### 4.7 Cuidadores e convites (RF-006/007, RB-006/007/015)

- Mais → Cuidadores: lista com avatar, nome, papel (Owner, Caregiver, ReadOnly), status (Ativo, Convite pendente).
- **Convidar:** botão "Convidar cuidador" → e-mail ou link compartilhável → escolher papel (com descrição de uma linha de cada papel) → enviar. Convite expira (prazo exibido) e pode ser reenviado/cancelado.
- **Aceitar convite (convidado):** abre link → login/criação de conta → tela "Você foi convidado(a) por {nome} para acompanhar {bebê}" mostrando o papel, quais dados serão visíveis, e botões Aceitar/Recusar. Consentimentos próprios do convidado são coletados (E3 simplificado).
- **Papéis:** Owner (tudo, inclusive remover cuidadores, transferir propriedade e excluir bebê); Caregiver (registrar/editar eventos); ReadOnly (ver; botões de registro ocultos/desativados com explicação).
- **Remover cuidador (somente Owner):** confirmação "{nome} perderá o acesso imediatamente. Os registros que fez continuam no histórico do bebê." Revogação efetiva imediata (RB-015).
- **Sair do bebê (não-Owner):** confirmação; dados que criou permanecem.
- Quem tem o acesso revogado vê tela "Você não tem mais acesso a {bebê}" ao abrir; sem dados em cache visíveis.
- **Cuidador que tenta ação sem permissão:** controle não é exibido; se acessado por link antigo, mensagem 403 amigável.
- Auditoria (RF-055): Owner vê "Atividade de acesso" simplificada (convites, aceites, remoções).

### 4.8 Notificações e quiet hours (RF-037..040, RB-010)

- Mais → Notificações. Categorias (cada uma com interruptor): Próximo sono, Hora de dormir (bedtime), Lembrete de rotina (opcional), Alertas de conta/segurança (não desativável: novo login, cuidador adicionado/removido; explicado).
- **Antecedência** configurável por categoria (ex.: 5, 10, 15, 30 min).
- **Quiet hours:** hora de início/fim (padrão sugerido 22:00–06:00 **desativado até o usuário escolher**, para não silenciar bedtime sem querer); explicação: "Durante este período não enviamos avisos, exceto de segurança da conta." Respeita o fuso do bebê/família.
- Conflito: se bedtime cai dentro de quiet hours, o app avisa e oferece ajustar.
- Permissão do SO: pedida **no contexto** (ao ativar a primeira categoria), com tela explicativa antes do prompt do sistema. Se negada, mostrar estado "Desativado no sistema" com atalho para Ajustes do SO.
- Notificações usam linguagem probabilística: "Pode ser um bom momento para a soneca em ~15 min."
- Ação direta na notificação: "Dormiu agora" (inicia timer), onde o SO permitir.
- Notificações por bebê quando há mais de um perfil; texto usa apelido do bebê com opção "ocultar nome na tela de bloqueio" (privacidade).

### 4.9 Paywall (RF-041..043, RB-009)

- Gatilhos (placement): ao tocar em recurso premium, em Mais → Assinatura, e um convite não intrusivo após o 7º dia de uso. **Nunca** bloqueia registro básico de eventos nem exclusão/exportação de dados (LGPD).
- Conteúdo: benefícios em linguagem concreta, preço e período claros, "Cancele quando quiser nas configurações da loja", trial (se houver) com data de cobrança explícita, botão "Restaurar compra", links para Termos/Privacidade.
- Fechar (X) sempre visível, alvo ≥ 48; sem *dark patterns* (sem contagem regressiva falsa, sem botão de recusa disfarçado).
- Estados: carregando preços, preços indisponíveis (erro de loja, "Tentar novamente"), compra em andamento, compra pendente (aprovação familiar da loja), sucesso (confirmação servidor), falha, já assinante.
- Entitlement é confirmado pelo backend; enquanto a validação estiver pendente: "Estamos confirmando sua assinatura…" com acesso provisório definido por política (ver dúvidas).
- Compartilhamento: se o plano é compartilhado com cuidadores (RF-043), mostrar quem é beneficiado e que o benefício depende de o plano do Owner estar ativo.
- Preço/limites do free × premium: **pendente de decisão de produto**; wireframe usa marcadores.

### 4.10 Exportar e excluir conta (RF-044/045), consentimentos (RF-003/040)

**Consentimentos (Mais → Privacidade):** lista de finalidades (Termos/Privacidade — obrigatórios e versionados; Estatísticas de uso; E-mails de novidades) com interruptores, data/versão do aceite, e "Ver histórico". Revogar = 1 interruptor + confirmação curta. Mudança de versão dos Termos exibe tela de reaceite com resumo das mudanças.

**Exportar dados:** Privacidade → "Exportar meus dados" → escolher bebê(s)/tudo → formato estruturado legível (arquivo com timestamps, timezone e versão de esquema, RB-014) → "Solicitar". Processamento assíncrono: estado "Preparando… avisaremos quando estiver pronto" (notificação + e-mail); link de download com expiração; reautenticação antes de baixar. Disponível também para quem não é assinante.

**Excluir conta:** Privacidade → "Excluir minha conta":
1. Tela de consequências em linguagem clara: o que será apagado, o que fica por obrigação legal (se aplicável), prazo, e o que acontece com bebês compartilhados (se Owner: escolher transferir propriedade a outro cuidador ou excluir o bebê para todos; destacar impacto nos outros cuidadores).
2. Oferecer "Exportar antes de excluir".
3. Aviso sobre assinatura da loja: "Excluir a conta não cancela a assinatura na {loja}" com atalho para gerenciar.
4. Reautenticação + digitar "EXCLUIR" ou confirmar por e-mail.
5. Confirmação final com botão destrutivo separado do "Cancelar".
6. Estado pós-pedido: "Exclusão agendada para {data}. Você pode cancelar entrando antes dessa data." (período de arrependimento, se definido pelo jurídico). Sessões encerradas.

### 4.11 Sessões e dispositivos (RF-054)
Mais → Sessões: lista de dispositivos (modelo, último acesso, local aproximado se disponível), marca "Este aparelho", botão "Encerrar sessão" por item e "Encerrar todas as outras".

---

## 4A. Estados globais (vazio, erro, offline, sincronizando)

### 4.12 Estados vazios

| Tela | Estado vazio | Copy (exemplo) | Ação |
|---|---|---|---|
| Home (sem registros) | Cartões em esqueleto com orientação | "Vamos começar? Registre o último sono ou mamada do {bebê}." | "Dormiu agora" / "Registrar algo" |
| Home (poucos dados) | Previsão "Em construção" | "Estamos usando valores típicos para a idade. A agenda se ajusta conforme você registra." | "Por quê?" |
| Timeline (dia sem eventos) | Ilustração leve (original) | "Nenhum registro neste dia." | "Adicionar registro" |
| Agenda | Sem dados mínimos | "Precisamos de pelo menos {N} registros de sono para estimar. Faltam {n}." | "Registrar sono" |
| Gráficos | Sem dados no período | "Ainda não há dados neste período." | Mudar período |
| Cuidadores | Só o Owner | "Convide quem cuida do {bebê} para registrarem juntos." | "Convidar" |
| Convites pendentes vazios | — | "Nenhum convite pendente." | — |

### 4.13 Offline, sincronizando, conflito, erro

Indicador de sync (padrão único, ícone + texto curto, no topo da Home e em Mais; **nunca** modal bloqueante):

| Estado | Visual | Texto | Comportamento |
|---|---|---|---|
| Sincronizado | Ícone neutro/discreto (ou oculto) | "Tudo sincronizado" (em Mais) | — |
| Sincronizando | Ícone girando (respeita "reduzir movimento": estático com texto) | "Sincronizando…" | Registros funcionam normalmente |
| Offline | Ícone nuvem riscada, faixa fina âmbar | "Sem conexão. Seus registros ficam salvos neste aparelho." | Tudo de tracking disponível; recursos online (convidar, assinar, exportar, excluir conta) mostram "Precisa de conexão" |
| Pendências | Marcador em itens da timeline + contador em Mais | "{n} registros aguardando envio" | Reenvio automático com backoff; botão "Tentar agora" |
| Erro de sync persistente | Faixa âmbar com ação | "Não conseguimos sincronizar desde {hora}. Seus dados estão seguros neste aparelho." | "Tentar novamente" · "Ver detalhes" (código curto para suporte) |
| Conflito | Sheet após sync | "{Nome} também editou este registro. Qual versão manter?" | Mostrar as duas versões lado a lado; "Manter a minha / a de {nome} / Manter as duas". Para sono, padrão: preservar ambos e sinalizar sobreposição. Last-write-wins só onde sem perda relevante (docs: seção 13 do discovery) |
| Sessão expirada | Tela leve | "Entre novamente para continuar sincronizando. Seus registros não foram perdidos." | Login sem perder fila local |
| Acesso revogado | Tela cheia | "Você não tem mais acesso a este bebê." | Limpar cache do bebê |

Erros de formulário: inline, junto ao campo, com texto + ícone (não só cor), foco movido ao primeiro erro. Erros de servidor: mensagem humana + ação (Tentar novamente) e código curto. Timeouts não perdem dados digitados. Estado de carregamento: esqueletos, nunca spinner em tela cheia para dados locais (RNF-005).

Relógio do aparelho muito diferente do servidor: aviso discreto ("A hora do seu aparelho parece diferente. Verifique em Ajustes.") pois afeta timestamps.

---

## 5. Wireframes (ASCII) — 6 telas principais

Legenda: `[ ]` botão · `( )` opção · `····` previsto/tracejado · `███` real · `▲▼` rolagem · `<` voltar.
Largura de referência: 360 pt/dp. Zona do polegar na base.

### 5.1 Hoje (Home) — estado "Acordado"

```
┌───────────────────────────────────┐
│ Nina                  ☁✓   (Ma) ⚙ │  topo: sync + avatar (Mais)
│ Lia · 5 meses 2 sem  ▾            │  seletor de bebê
├───────────────────────────────────┤
│ ┌───────────────────────────────┐ │
│ │ AGORA                         │ │
│ │ Acordada há 1 h 35            │ │  cartão estado
│ │ Última soneca: 12:40–13:55    │ │
│ └───────────────────────────────┘ │
│ ┌───────────────────────────────┐ │
│ │ PRÓXIMO SONO · previsto       │ │
│ │ Entre 14:10 e 14:40           │ │
│ │ ◐ Confiança: razoável         │ │
│ │ Por quê?                  ›   │ │
│ │ Isto é uma estimativa.        │ │
│ └───────────────────────────────┘ │
│ HOJE                              │
│ Sono 3 h 20 · 2 sonecas           │
│ Mamadas 4 · Fraldas 5             │
│ Última mamada há 1 h 10           │
│                                   │
│                                   │
│ ┌───────────────────────────────┐ │
│ │        ☾  Dormiu              │ │  botão primário, 56 dp, base
│ └───────────────────────────────┘ │
│ [ + Registrar outro ]             │  abre sheet de ação rápida
├───────────────────────────────────┤
│ Hoje  Linha do tempo  Agenda  Gráf│  abas
└───────────────────────────────────┘
```
Estado "Dormindo": cartão AGORA vira "Dormindo há 0:42" (cronômetro, valores com `tabular-nums`), botão primário muda para "☀ Acordou"; cartão de previsão é substituído por "Acordar previsto"? **Não** — mostra apenas "Sono em andamento" (não prevê fim, evita pressão).

Sheet de ação rápida:
```
┌───────────────────────────────────┐
│ O que você quer registrar?    ✕   │
│ ┌─────────┐ ┌─────────┐           │
│ │ ☾ Sono  │ │ ◖ Mamada│           │
│ └─────────┘ └─────────┘           │
│ ┌─────────┐ ┌─────────┐           │
│ │ ▢ Mama- │ │ ◇ Fralda│           │
│ │   deira │ │         │           │
│ └─────────┘ └─────────┘           │
│ ┌─────────┐                       │
│ │ ◍ Bomba │   ordem por uso       │
│ └─────────┘                       │
└───────────────────────────────────┘
```

### 5.2 Registro de sono — timer ativo e finalização

```
┌───────────────────────────────────┐
│ <                  Sono           │
├───────────────────────────────────┤
│                                   │
│            ☾ Dormindo             │
│                                   │
│            0 : 42 : 17            │  cronômetro grande
│        desde 14:02 · soneca       │
│        iniciado por Ana           │
│                                   │
│  Foi antes?  (-5) (-10) (-15) (…) │  corrige início
│                                   │
│  Tipo:  (•) Soneca  ( ) Noturno   │
│                                   │
│                                   │
│ ┌───────────────────────────────┐ │
│ │          ☀  Acordou           │ │  1 toque, base da tela
│ └───────────────────────────────┘ │
│ [ Cancelar registro ]             │  texto secundário, com confirmação
└───────────────────────────────────┘

Após "Acordou":
┌───────────────────────────────────┐
│ Soneca registrada ✓           ✕   │
│ 14:02 – 14:44 · 42 min            │
│ Ajustar fim:  (-5) (-10) (…)      │
│ ▸ Detalhes (local, observação)    │
│ ┌───────────────────────────────┐ │
│ │            Salvar             │ │
│ └───────────────────────────────┘ │
│   Desfazer                        │
└───────────────────────────────────┘
```

### 5.3 Linha do tempo

```
┌───────────────────────────────────┐
│ Linha do tempo        ☁✓          │
│ (Tudo)(Sono)(Comida)(Fralda)(Pump)│  chips
├───────────────────────────────────┤
│ ▲ Hoje, qui 8 out                 │
│   Sono 3h20 · Mamadas 4 · Fraldas5│
│ ───────────────────────────────── │
│ 14:44  ☀ Acordou  (soneca 42 min) │
│ 14:02  ☾ Dormiu            ⟳ pend.│  pendente de envio
│ 13:20  ◇ Fralda · xixi    (Ana)   │
│ 12:50  ▢ Mamadeira · 120 ml       │
│ 12:15  ◖ Mamada · D 9 min E 7 min │
│ ············ previsto 14:10–14:40 │  opcional, tracejado
│ ───────────────────────────────── │
│ Ontem, qua 7 out                  │
│   Sono 13h05 · Mamadas 7 · …      │
│ 19:30  ☾ Dormiu (noturno)         │
│  ...                              │
│                                   │
│                        ( + )      │  FAB: ação rápida
├───────────────────────────────────┤
│ Hoje  Linha do tempo  Agenda  Gráf│
└───────────────────────────────────┘
Toque em item → sheet "Editar" (hora, duração, tipo, notas, histórico, Excluir)
```

### 5.4 Agenda e previsão

```
┌───────────────────────────────────┐
│ Agenda      < qui 8 out >  ⚙ Rotina│
├───────────────────────────────────┤
│ 06:30 ███ Acordou                 │
│ 08:45 ███ Soneca 1 · 55 min       │
│ 11:10 ███ Soneca 2 · 1h 10        │
│ 13:30 ·································│
│ 14:10 ░░░ Soneca 3 · previsto     │
│       ░░░ 14:10–14:40 (início)    │
│ 17:20 ····· Soneca curta prevista │
│ 19:00 ░░░ Hora de dormir · prev.  │
│       ░░░ 18:45–19:25             │
│                                   │
│ ┌───────────────────────────────┐ │
│ │ Por que esta previsão?        │ │
│ │ ◐ Confiança: razoável         │ │
│ │ Baseada na idade (5 m) e em   │ │
│ │ 6 dias de registros. Costuma  │ │
│ │ ficar acordada ~2h 15 entre   │ │
│ │ sonecas.                      │ │
│ │ Mudou algo? Registre o sono   │ │
│ │ para atualizar.               │ │
│ │ Isto é uma estimativa, não um │ │
│ │ diagnóstico nem uma regra.    │ │
│ └───────────────────────────────┘ │
│ Rotina: 3 sonecas · bedtime 19–20h│
│ [ Ajustar rotina ]                │
├───────────────────────────────────┤
│ Hoje  Linha do tempo  Agenda  Gráf│
└───────────────────────────────────┘
```

### 5.5 Gráficos de sono

```
┌───────────────────────────────────┐
│ Gráficos                          │
│ (Sono)(Comida)(Fralda)            │
│ ( Dia | Semana | Mês )  < 2–8 out >│
├───────────────────────────────────┤
│ Sono total por dia                │
│ 16h│                              │
│ 12h│ ▇  ▇  ▇  ▆  ▇  ▇  ▆   —— média│
│  8h│ ▇  ▇  ▇  ▇  ▇  ▇  ▇   (13h05)│
│  4h│ ▒  ▒  ▒  ▒  ▒  ▒  ▒   ▒=sonec│
│    └───────────────────────  ▇=noit│
│     seg ter qua qui sex sáb dom    │
│ Média desta semana: 13h05          │
│ Semana anterior: 12h40             │
│ [ Ver como tabela ]                │
│                                   │
│ Despertares à noite  (média 1,6)  │
│  ● ○ ● ● ○ ● ●                    │
│                                   │
│ Janelas acordado (wake windows)   │
│  distribuição: 1h30–2h30 mais comum│
│ Comparação só com o próprio bebê. │
├───────────────────────────────────┤
│ Hoje  Linha do tempo  Agenda  Gráf│
└───────────────────────────────────┘
```
Padrões de preenchimento (listrado/sólido) além de cor para distinguir séries (seção 10).

### 5.6 Paywall

```
┌───────────────────────────────────┐
│ ✕                                 │  fechar sempre visível
│                                   │
│        Nina Plus                  │
│  Mais ajuda para a rotina da Lia  │
│                                   │
│  ✓ [benefício 1 — a definir]      │
│  ✓ [benefício 2 — a definir]      │
│  ✓ Compartilhe com cuidadores     │
│    (se aprovado pelo produto)     │
│                                   │
│ ┌───────────────────────────────┐ │
│ │ ( ) Mensal   R$ [x]/mês       │ │
│ │ (•) Anual    R$ [y]/ano       │ │
│ │     equivale a R$ [z]/mês     │ │
│ └───────────────────────────────┘ │
│ Teste grátis de [N] dias.         │
│ Cobrança de R$ [y] em 15 out.     │
│ Cancele quando quiser, nas        │
│ configurações da {loja}.          │
│ ┌───────────────────────────────┐ │
│ │      Começar teste grátis     │ │
│ └───────────────────────────────┘ │
│  Restaurar compra · Termos ·      │
│  Privacidade                      │
│  Registrar eventos e exportar     │
│  seus dados continuam gratuitos.  │
└───────────────────────────────────┘
```

### 5.7 Apoio (wireframes curtos): onboarding de bebê e consentimentos

```
┌───────────────────────────────────┐   ┌───────────────────────────────────┐
│ <   Conte sobre seu bebê   3 de 5 │   │ <   Antes de começar         2 de 5│
│                                   │   │                                   │
│ Como podemos chamá-lo(a)?         │   │ ☐ Li e aceito os Termos de Uso    │
│ [ Lia______________ ]             │   │   e a Política de Privacidade     │
│                                   │   │   (v1.0) — obrigatório            │
│ Data de nascimento                │   │                                   │
│ [ 02/05/2026       ▾ ]            │   │ Opcionais (pode mudar depois):    │
│                                   │   │ ☐ Estatísticas de uso, sem dados  │
│ Data prevista do parto (opcional) │   │   que identifiquem seu bebê       │
│ [ 10/05/2026       ▾ ]            │   │ ☐ Novidades por e-mail            │
│ Ajuda a ajustar a agenda se o     │   │                                   │
│ bebê nasceu antes do previsto.    │   │ [ Continuar ]  (ativo só c/ ☑ 1)  │
│                                   │   │                                   │
│ [ Continuar ]                     │   │                                   │
└───────────────────────────────────┘   └───────────────────────────────────┘
```

---

## 6. Design system — tokens iniciais (v0)

Nome de trabalho: **Nina DS**. Paleta, tipografia e formas **originais**: identidade "crepúsculo e leite morno" (índigo suave, damasco e verde-sálvia), sem reproduzir as paletas, mascotes ou iconografia de concorrentes. Valores a validar com ferramenta de contraste antes da implementação (ver 10.2).

### 6.1 Cores (tokens semânticos)

| Token | Claro | Escuro ("Noite") | Uso |
|---|---|---|---|
| `bg/app` | `#FAF8F5` | `#12141C` | fundo da tela |
| `bg/surface` | `#FFFFFF` | `#1B1F2B` | cartões |
| `bg/surface-raised` | `#F1EEF7` | `#242938` | sheets, destaque |
| `text/primary` | `#1F2430` | `#E8EAF0` | texto principal |
| `text/secondary` | `#555C6E` | `#B3B9C9` | apoio |
| `text/on-accent` | `#FFFFFF` | `#10131A` | texto sobre botão primário |
| `accent/primary` | `#3B5BA9` | `#9DB4F0` | ações primárias, links |
| `accent/primary-pressed` | `#2F4A8E` | `#B7C8F5` | pressionado |
| `event/sleep` | `#5B4B9A` | `#B6A8F0` | sono |
| `event/feeding` | `#A5541A` | `#F0B27E` | alimentação |
| `event/diaper` | `#2B7A66` | `#7FD0B8` | fralda |
| `event/pumping` | `#9A3F6B` | `#EBA0C4` | pumping |
| `prediction/fill` | `accent/primary` a 12% | idem 18% | blocos previstos (+ borda tracejada) |
| `state/success` | `#2E7D4F` | `#7FD39E` | sucesso |
| `state/warning` | `#8A5A00` | `#F2C14E` | offline/atenção |
| `state/error` | `#B3261E` | `#FF9A93` | erro, destrutivo |
| `border/subtle` | `#DDD8E3` | `#2F3547` | divisores |
| `focus/ring` | `#1F6FEB` | `#8CB4FF` | foco de teclado/switch |

Regras: nunca usar cor como único diferencial (sempre ícone/forma/texto); fundo escuro nunca `#000` puro nem branco puro como texto; modo "Noite" reduz saturação e brilho de superfícies; respeita tema do sistema, com override manual em Preferências.

### 6.2 Tipografia

- Fonte: **fonte do sistema** (SF Pro no iOS, Roboto/sistema no Android) no MVP para suportar Dynamic Type/escala de fonte e i18n sem custo de licença. Fonte de marca fica como decisão futura.
- Números do cronômetro e horários usam variante tabular.

| Estilo | Tamanho base | Peso | Uso | Mapeamento nativo |
|---|---|---|---|---|
| `display` | 34 | Semibold | cronômetro | iOS `largeTitle`/Compose `displayMedium` |
| `title-1` | 24 | Semibold | título de tela | `title`/`headlineSmall` |
| `title-2` | 20 | Semibold | título de cartão | `title2`/`titleLarge` |
| `body` | 17 | Regular | texto | `body`/`bodyLarge` |
| `body-strong` | 17 | Semibold | ênfase | — |
| `callout` | 15 | Regular | secundário | `callout`/`bodyMedium` |
| `caption` | 13 | Regular | legendas (mínimo; nunca < 12 pt) | `caption`/`labelMedium` |

Todos os estilos escalam com Dynamic Type (iOS) e `sp`/fontScale (Android) até 200%; layouts testados nos maiores tamanhos (10.1).

### 6.3 Espaçamento, forma e elevação

| Token | Valor |
|---|---|
| `space/1,2,3,4,5,6,8` | 4, 8, 12, 16, 20, 24, 32 pt/dp (grade de 4) |
| Gutter lateral | 16 |
| Alvo mínimo de toque | 48 (primário: 56) |
| `radius/sm, md, lg, pill` | 8, 14, 20, 999 |
| Elevação | 0 / 1 / 2 (apenas sutil; no escuro, diferenciar por superfície, não por sombra) |
| Ícones | 24 (20 em linha, 32 em ação rápida); traço 2 pt; formas distintas por evento |
| Movimento | 150–250 ms ease-out; versão estática com "reduzir movimento" |

### 6.4 Componentes base (v0)
Botão primário/secundário/texto/destrutivo; cartão; chip de filtro; seletor segmentado; linha de evento (timeline); bloco de agenda (real/previsto); indicador de confiança; faixa de sync/offline; sheet inferior; stepper de volume; seletor de hora com atalhos (-5/-10/-15/-30); cronômetro; item de lista com avatar; campo de formulário com erro; snackbar com Desfazer; estado vazio; esqueleto de carregamento; gráfico (barras, pontos, tabela alternativa).

---

## 7. Notificação de segurança, privacidade e conteúdo sensível na UI

- Foto/nome do bebê: opcionais; o nome pode ser ocultado em notificações e no seletor de apps recentes (tela de privacidade).
- Telas com dados do bebê não exibem conteúdo em capturas do app-switcher se o usuário ativar "Ocultar conteúdo ao alternar apps" (opcional).
- Analytics: consentimento separado; nenhuma tela coleta texto livre para analytics (RB-011).
- Convites e links compartilhados não expõem nome completo do bebê antes do aceite.

---

## 8. Preferências do usuário relevantes ao desenho
Unidades (ml/oz), fuso (automático por aparelho com override por bebê), formato de 12/24 h, tema (Sistema/Claro/Noite), idioma, "Ocultar nome em notificações", "Reduzir animações" (adicional ao sistema).

---

## 9. Multi-bebê e multi-fuso
- Seletor de bebê no topo da Home/Timeline/Agenda/Gráficos; o último selecionado persiste. Cores/avatares distinguem bebês.
- Timeline mostra horários no fuso do bebê; se diferente do fuso do aparelho, rótulo "(horário de {cidade/fuso})" (RB-010, RB-014).
- Mudança de fuso/viagem: banner "Você está em outro fuso. Mostrar horários no fuso do aparelho ou do bebê?" Eventos históricos não são alterados.
- Horário de verão: registros em UTC; exibição ajustada sem duplicar/perder eventos.

---

## 10. Requisitos de acessibilidade

Meta: WCAG 2.2 AA como referência (RNF-009) e equivalentes nativos iOS HIG / Material a11y.

### 10.1 Geral
- **VoiceOver (iOS) / TalkBack (Android):** todos os elementos interativos com rótulo, papel e estado; ordem de leitura = ordem visual; agrupamento de cartões (um foco por cartão com ações expostas como *custom actions*); títulos de seção marcados como cabeçalho (rotor/headings).
- Rótulos de eventos falados completos: "Soneca, das 14:02 às 14:44, 42 minutos, registrada por Ana, aguardando sincronização."
- Cronômetro: não anunciar a cada segundo; anuncia a cada minuto sob demanda; o rótulo fala "Dormindo há 42 minutos".
- **Dynamic Type / fontScale:** suportar até 200% (iOS categorias de acessibilidade AX5); layouts fluem para coluna única; nenhum texto truncado em ações críticas; botão primário cresce em altura; testes de snapshot em 100%, 150% e 200%.
- **Alvos de toque:** ≥ 44×44 pt (iOS) / 48×48 dp (Android); espaçamento ≥ 8 entre alvos.
- **Contraste AA:** texto normal ≥ 4,5:1; texto grande (≥ 18 pt ou 14 pt bold) e componentes de UI/ícones/gráficos ≥ 3:1, em claro e escuro. Estados de foco ≥ 3:1.
- **Não depender de cor:** eventos distintos por ícone + forma + rótulo; previsões por tracejado + rótulo "previsto"; confiança por texto + glifo (◔ ◐ ●); erro por ícone + texto.
- **Movimento:** respeitar "Reduzir movimento"/animações do sistema; sem piscar > 3 vezes/s; sem animações de fundo contínuas.
- **Orientação e zoom:** suporta retrato (principal) e paisagem sem perda de função; zoom do sistema/lupa funciona.
- **Alternativas ao gesto:** toda ação por gesto (deslizar para excluir, arrastar horário) tem alternativa por botão/menu; sem gestos de múltiplos dedos obrigatórios.
- **Teclado/switch control/Voice Control/Voice Access:** navegação completa por teclado externo; rótulos visíveis casam com rótulo acessível (Rótulo na Voz); foco visível e lógico; sheets devolvem foco ao disparador ao fechar.
- **Tempo:** snackbar "Desfazer" acessível (anunciado, permanece ≥ 8 s, ampliável nas configurações); sem timeouts curtos em formulários.
- **Formulários:** rótulos persistentes (não só placeholder); erros anunciados; autofill/teclados adequados; seletor de hora acessível com entrada numérica alternativa.
- **Leitores e gráficos:** cada gráfico tem resumo textual ("Sono total médio 13 h 05; maior valor sexta, 14 h") e visão em tabela; pontos navegáveis por foco; sonificação opcional é pós-MVP.
- **Idioma:** localizar rótulos acessíveis e pluralização; marcar idioma dos textos para o sintetizador.
- **Madrugada:** modo escuro sem queda de contraste; opção de "brilho baixo" (tema Noite) e texto grande como padrão sugerido para uso noturno.

### 10.2 Verificação
- Checklist por PR de UI (rótulos, foco, contraste, 200%).
- Auditoria de contraste dos tokens (6.1) antes do congelamento do DS v1; os pares `text/secondary` × `bg/app`, `event/*` × `bg/surface` e `state/warning` × `bg/app` são os de maior risco.
- Testes manuais com VoiceOver e TalkBack nos fluxos: onboarding, timer de sono, registro de fralda/mamadeira, editar evento, ajustar rotina, convidar cuidador, paywall, excluir conta.
- Accessibility Gate (conforme specification.md): zero bloqueadores em fluxos críticos para liberar release.

---

## 11. Copy guidelines — voz e linguagem probabilística (RNF-014)

### 11.1 Voz
Calma, acolhedora, direta, em português do Brasil; trata a pessoa por "você"; frases curtas; sem infantilização do adulto; sem culpa, pressão, comparação ou alarmismo; sem exclamações em excesso; emojis não usados em texto funcional.

### 11.2 Regras para previsões e conteúdo de saúde
1. **Faixas, não horários exatos:** "entre 14:10 e 14:40", "por volta de".
2. **Verbos de possibilidade:** "pode", "costuma", "provavelmente", "tende a", "sugere". Evitar "vai", "deve", "precisa", "está atrasado", "normal/anormal".
3. **Fonte e base:** "Baseado na idade e nos últimos {n} dias de registros."
4. **Humildade explícita:** "É uma estimativa. Cada bebê é diferente."
5. **Nunca diagnóstico ou alegação clínica:** proibido sugerir causa médica ("seu bebê tem refluxo/cólica/problema de sono"), proibido classificar como "saudável/doente/normal/anormal", proibido prometer resultados ("vai dormir a noite toda").
6. **Dados factuais sem julgamento:** "Dormiu 11 h nas últimas 24 h" e não "dormiu pouco".
7. **Comparação só com o próprio histórico** (RB-013); nunca com "outros bebês" ou percentis.
8. **Encaminhamento responsável:** se o usuário registrar algo que ele marque como preocupação ou ao final do aviso de saúde: "Se você está preocupado(a) com a saúde do seu bebê, fale com o pediatra. Em emergência, procure atendimento imediato." Sem inferir sintomas.
9. **Desenvolvimento (V1, reservado):** "pode começar a…", "costuma aparecer entre…"; sem data exata (RB-005).
10. **Notificações:** sem urgência artificial; "Pode ser um bom momento para a soneca em ~15 min." Nunca "Seu bebê está cansado!".

### 11.3 Exemplos (usar / evitar)

| Contexto | Usar | Evitar |
|---|---|---|
| Próxima soneca | "Pode ser hora da soneca entre 14:10 e 14:40." | "A soneca é às 14:10." / "Seu bebê precisa dormir agora." |
| Sem dados | "Estamos usando valores típicos para a idade." | "Dados insuficientes." |
| Soneca curta | "Esta soneca durou 25 min." | "Soneca ruim." |
| Noite com despertares | "3 despertares registrados esta noite." | "Sono agitado, procure ajuda." |
| Confiança | "Confiança: razoável" | "Precisão: 83%" |
| Previsão errada | (nada; o real prevalece) | "Previsão falhou." |
| Registro esquecido | (nada) | "Você não registra há 2 dias." |

### 11.4 Aviso de saúde (texto-base, revisar com jurídico/especialista)
Curto (modal e rodapé): "As estimativas do Nina são orientativas, baseadas na idade e nos registros que você fez. Não substituem a avaliação de um profissional de saúde e não são diagnóstico."
Longo (Ajuda): acrescenta como a estimativa é calculada em alto nível e quando procurar o pediatra.

### 11.5 Outros padrões de texto
- Botões com verbo: "Salvar", "Convidar", "Excluir minha conta" (nunca "OK" ou "Sim" em destrutivo).
- Mensagens de erro: o que houve + o que fazer ("Não foi possível enviar. Seus dados estão salvos. Tentar novamente").
- Datas/horas relativas acessíveis ("há 1 h 35"), com formato absoluto no rótulo acessível.
- Pluralização e gênero: usar apelido do bebê e evitar flexão de gênero quando possível ("Acordou", "Lia dormiu"); se sexo não informado, não flexionar adjetivos.
- Tom em estados de risco (exclusão, remoção de cuidador): claro e factual, sem dramatizar.
- Termos de loja/preço: seguem exigências Apple/Google; preço e período com mesmo destaque do botão.

---

## 12. Diferenças nativas permitidas (mesma IA)

| Aspecto | iOS (SwiftUI) | Android (Compose) |
|---|---|---|
| Navegação base | `TabView` + `NavigationStack` | `NavigationBar` + Navigation Compose |
| Sheets | `.sheet` com detents | `ModalBottomSheet` |
| Voltar | gesto de borda + botão | botão/gesto do sistema (predictive back) |
| Seletores | `DatePicker` nativo | `TimePicker/DatePicker` Material |
| Notificação | APNs, ações em notificação | FCM, canais por categoria (mapear às categorias de 4.8) |
| Fonte/escala | Dynamic Type | fontScale + `sp` |
| Haptics | feedback leve em "Dormiu/Acordou" (opcional, desativável) | idem |
| Compra | StoreKit 2 | Play Billing |
| Widgets/Watch | fora do MVP | fora do MVP |

Nomes de telas, textos, ordem de abas, tokens e fluxos são idênticos; qualquer divergência além da tabela requer decisão registrada.

---

## 13. Medição de UX (alinhada à taxonomia de analytics, sem PII)

Eventos reutilizados: `onboarding_started`, `baby_profile_created`, `sleep_timer_started`, `sleep_logged`, `feeding_logged`, `prediction_viewed`, `prediction_outcome`, `caregiver_invited`, `paywall_viewed`, `trial_started`, `subscription_started`, `notification_opened`.
Métricas de UX: tempo até o primeiro registro; toques por registro (amostragem de instrumentação de interface com consentimento); taxa de uso do "Foi antes…"; taxa de abertura de "Por quê?"; fracasso de sync percebido (faixa de erro exibida); desfazer por tipo. Nenhuma métrica registra texto livre, nome ou fotos.

---

## 14. Critérios de aceite de UX (resumo)

1. Fluxo onboarding → primeiro registro concluído em ≤ 6 telas e ≤ 2 min em teste moderado com 5 usuários.
2. Timer de sono iniciado em 1 toque e finalizado em ≤ 2 toques; fralda em 2; mamadeira em ≤ 3.
3. Todas as telas funcionam offline para tracking; indicador de sync nunca bloqueia.
4. Toda previsão exibe faixa, confiança textual, "Por quê?" e rótulo de estimativa; nenhuma copy viola 11.2.
5. Contraste AA verificado em claro e Noite; Dynamic Type/fontScale 200% sem perda de ação.
6. VoiceOver e TalkBack completam os fluxos listados em 10.2.
7. Revogar cuidador, exportar dados e excluir conta seguem os passos de 4.7 e 4.10, com reautenticação e confirmações.
8. Paywall nunca bloqueia registro básico, exportação nem exclusão; fechar e restaurar sempre acessíveis.

---

## 15. Dúvidas de UX (abertas)

1. **Autenticação:** apenas e-mail+senha ou também login social (Apple/Google)? Impacta E2 e exigência de "Sign in with Apple" nas diretrizes da loja.
2. **Idade corrigida (prematuros):** exibir apenas como info secundária ou usar na previsão por padrão? Limite de semanas e momento de parar de corrigir?
3. **Cold start:** quantos registros/dias mínimos antes de mostrar previsão com confiança "Razoável"? Mostrar previsão só por idade desde o dia 1 ou ocultar?
4. **Confiança:** 3 níveis textuais bastam ou o motor expõe valor numérico? Os limiares são definidos por quem (engenharia + especialista)?
5. **Tipo soneca × noturno:** inferência automática por horário, ou perguntar? Como tratar "sono da tarde longo" e sonecas de colo/carro?
6. **Aviso de saúde:** texto final e exibição (uma vez, sempre no rodapé, ou ambos) dependem de revisão jurídica e de especialista; precisa de gate antes de usuários reais?
7. **Free × premium:** o que fica atrás do paywall (previsão? gráficos de mês? cuidadores extras? histórico > N dias)? Há trial? Isso muda toda a tela 5.6 e os pontos de gatilho.
8. **Plano compartilhado (RF-043):** política familiar — todos os cuidadores herdam o premium do Owner? O que acontece se o Owner cancelar?
9. **Acesso provisório** enquanto o servidor valida um recibo: libera premium temporariamente ou aguarda?
10. **Convites:** por e-mail apenas ou também link/QR? Prazo de expiração? Limite de cuidadores no free?
11. **Transferência de propriedade e exclusão de conta do Owner:** transferência obrigatória antes de excluir? Prazo de arrependimento (quantos dias)? Retenções legais a mencionar?
12. **ReadOnly:** pode receber notificações de sono? Pode ver o histórico completo ou só período recente?
13. **Quiet hours:** padrão desligado (como proposto) ou pré-configurado 22–6? Notificações de bedtime podem atravessar quiet hours por escolha explícita?
14. **Notificações acionáveis:** ação "Dormiu agora" direto da notificação é aceitável do ponto de vista de risco de registro acidental?
15. **Timer em multi-cuidador:** dois cuidadores iniciam timers de sono simultâneos — fundir, perguntar ou manter ambos? (ligado a ADR-0003 de sync)
16. **Conflitos de edição:** a UI de conflito entra no MVP ou usa regra automática com histórico, aceitando conflito explícito só para casos raros?
17. **Unidades:** ml/oz por locale ou escolha explícita no onboarding? Peso/altura ficam fora do MVP?
18. **Observações de fralda:** campo livre apenas, ou opções (cor/consistência) — sendo que opções sugerem interpretação clínica e RNF-014?
19. **Tema Noite automático:** ativar por horário por padrão, ou seguir só o sistema?
20. **Identidade de marca:** nome do produto, logotipo e ilustrações ainda não definidos; os tokens aqui são provisórios. Há restrições de marca (cor, tom) do cliente?
21. **Idiomas:** PT/EN/ES no MVP ou só PT com i18n pronto? Impacta tempo de revisão de copy probabilística em cada idioma.
22. **Timer e Live Activities / notificação persistente:** entram no MVP (como conveniência) ou ficam para pós-MVP junto de widgets/Watch?
23. **Sessões:** exibir localização aproximada de acesso conflita com minimização de dados (RNF-002)? Alternativa: só modelo do aparelho e data.
24. **Edição retroativa:** limite de antiguidade para editar eventos (ex.: 30 dias) ou ilimitado? Impacta previsões recalculadas e experiência multi-cuidador.
25. **Pesquisa com usuários:** há acesso a pais reais para teste de usabilidade e de compreensão da linguagem probabilística antes do MVP?
