# Glossário — Nina

Status: rascunho (REQ-001) · Data: 2026-10-08 · Escopo: MVP, com termos de V1/V2 marcados.
Fonte dos conceitos: `specs/discovery-napper-wonder-weeks.md`. Definições redigidas para a Nina; não reproduzem texto de terceiros.
Convenção: termos de domínio em português na documentação; identificadores de código/API em inglês (entre crases).

## A. Pessoas e acesso

| Termo | Definição | Rastreio |
|---|---|---|
| Usuário (`User`) | Adulto com conta na Nina, identificado por e-mail. Pode ter vínculo com um ou mais bebês. | RF-001 |
| Bebê (`Baby`) | Criança cujo perfil e eventos são registrados. Pertence a uma família de cuidadores. | RF-004 |
| Cuidador | Usuário com vínculo ativo a um bebê, qualquer papel. | RF-006 |
| Vínculo (`CaregiverMembership`) | Relação usuário-bebê com papel e status (convidado, ativo, revogado). | RF-006, RB-006 |
| Owner | Papel do criador do perfil do bebê. Único que remove cuidador, transfere propriedade ou exclui o perfil. | RB-007 |
| Caregiver | Papel que lê e registra eventos do bebê. Não gerencia vínculos. | RF-006 |
| ReadOnly | Papel que apenas lê dados do bebê. | RF-006 |
| Convite | Oferta de vínculo enviada a um e-mail com papel pré-definido; só gera acesso após aceite. | RF-006, RB-006 |
| Revogação | Encerramento do vínculo de um cuidador; o acesso cessa de imediato e tokens/cache são invalidados. | RB-015 |
| Sessão | Autenticação ativa de um usuário em um dispositivo. | RF-001, RF-054 |
| Dispositivo | Instalação do app (iOS ou Android) associada a sessões; identificada por `device_id`. | RF-054, ADR-0003 |

## B. Tempo e idade

| Termo | Definição | Rastreio |
|---|---|---|
| Data de nascimento | Data em que o bebê nasceu. | RF-004 |
| Data prevista do parto (DPP) | Data estimada do parto, guardada em campo separado da data de nascimento e nunca sobrescrita por ela. Opcional. | RB-004 |
| Idade cronológica | Tempo decorrido desde a data de nascimento, calculado no fuso do bebê. | RF-005 |
| Idade corrigida | Idade ajustada pela antecipação do nascimento em relação à DPP. Regras e política de prematuros em `domain-model.md` seção 6 (dúvida aberta). | RF-005 |
| Fuso do bebê | Fuso horário de referência da família para exibir horários e agendar notificações. | RF-004, RB-010 |
| UTC + fuso contextual | Forma de persistir instantes: instante em UTC mais o fuso vigente no momento do registro. | RB-014 |
| Evento retroativo | Registro criado depois de o fato ocorrer, com horários informados manualmente. | RF-009 |

## C. Sono

| Termo | Definição | Rastreio |
|---|---|---|
| Sessão de sono (`SleepSession`) | Registro real de um período de sono, com início, fim, tipo e observações. | RF-008 |
| Soneca | Sessão de sono diurno. Tipo `nap`. | RF-008 |
| Sono noturno | Sessão de sono principal da noite. Tipo `night`. | RF-008 |
| Timer de sono | Cronômetro que cria a sessão ao iniciar e fecha o fim ao parar; sessão aberta é a que não tem fim. | RF-009 |
| Sessão em aberto | Sessão de sono com início e sem fim (timer em curso). | RF-009 |
| Despertar | Interrupção do sono noturno dentro de uma noite. Forma exata de registro é dúvida aberta. | RF-010 |
| Janela de vigília (wake window) | Intervalo entre o fim de uma sessão de sono e o início efetivo da seguinte. | RF-010 |
| Total diário de sono | Soma das durações de sono atribuídas a um dia no fuso do bebê. Regra de atribuição de sessões que cruzam meia-noite é dúvida aberta. | RF-010 |
| Previsão (`SleepPrediction`) | Estimativa calculada de início da próxima soneca ou do bedtime. Orientação, não fato; sempre recalculável. | RF-011, RB-001 |
| Bedtime | Horário previsto ou preferido para iniciar o sono noturno. | RF-011, RF-014 |
| Faixa preferida de bedtime | Intervalo de horários definido pelo cuidador dentro do qual o bedtime deve cair. | RF-014 |
| Meta de sonecas | Número (ou estrutura) de sonecas diárias que o cuidador deseja como alvo. | RF-014 |
| Confiança | Indicador simples de quão sustentada é uma previsão, ligado à quantidade e à variância dos dados. | RF-013 |
| Cold start | Situação com pouco ou nenhum histórico; a previsão usa apenas a referência por idade. | ADR-0004 |
| Referência por idade | Tabela de valores típicos (janela de vigília e número de sonecas por faixa etária), validada por especialista, usada como base da previsão. | ADR-0004 |
| Motor de sono | Biblioteca pura que, dado histórico e idade, devolve previsão, confiança e explicação. | ADR-0004 |
| Recálculo | Nova execução do motor após criação, alteração ou exclusão de evento de sono. | RF-012 |

## D. Cuidados e linha do tempo

| Termo | Definição | Rastreio |
|---|---|---|
| Amamentação (`FeedingSession`, tipo `breast`) | Mamada ao peito, com lado, início e fim. | RF-015 |
| Mamadeira (`FeedingSession`, tipo `bottle`) | Oferta por mamadeira, com volume, tipo de leite opcional e horário. | RF-016 |
| Pumping (`PumpingSession`) | Extração de leite, com duração e volume opcional. | RF-017 |
| Fralda (`DiaperEvent`) | Troca de fralda com tipo e observações opcionais. | RF-018 |
| Evento | Qualquer registro factual do bebê: sono, amamentação, mamadeira, pumping ou fralda. | RF-019 |
| Timeline | Lista cronológica unificada de todos os eventos de um bebê. | RF-019 |
| Trilha de auditoria mínima | Registro de quem criou, alterou ou excluiu um evento compartilhado, quando e o quê (sem conteúdo sensível). | RF-020, RF-055 |

## E. Sincronização e offline

| Termo | Definição | Rastreio |
|---|---|---|
| Offline-first | O cliente grava primeiro no banco local e sincroniza depois; funções de tracking não dependem de rede. | RF-046, RNF-007 |
| Mutação | Alteração local pendente de envio (criar, editar, excluir), com UUID, `client_created_at`, `base_version` e `device_id`. | ADR-0003 |
| Push idempotente | Envio de mutações em que reenviar o mesmo UUID não duplica efeito. | RNF-007 |
| Pull por cursor | Leitura incremental de mudanças a partir de um marcador de versão. | ADR-0003 |
| Versão canônica | Número monotônico por bebê atribuído pelo servidor a cada alteração aceita. | ADR-0003 |
| Conflito | Edições concorrentes do mesmo evento em dispositivos diferentes. | RF-047 |
| Tombstone | Marca de exclusão retida por uma janela até todos os dispositivos convergirem, evitando ressurreição do dado. | RB-008 |
| Estado de sincronização | Situação de um registro local: pendente, enviado, confirmado ou em conflito. | RF-046 |

## F. Notificações

| Termo | Definição | Rastreio |
|---|---|---|
| Categoria de notificação | Grupo configurável: soneca, bedtime, rotina, fases de desenvolvimento (sem conteúdo no MVP), além de avisos de sistema/segurança. | RF-037 |
| Antecedência | Minutos antes do evento previsto em que o aviso é entregue. | RF-038 |
| Quiet hours | Janela diária em que avisos não devem ser entregues. | RF-038, RB-010 |
| Job de notificação (`NotificationJob`) | Entrega agendada com chave de evento, estado e tentativas. | RF-039 |
| Idempotência de entrega | Garantia de que retries não geram aviso duplicado. | RF-039, RNF-011 |

## G. Assinatura

| Termo | Definição | Rastreio |
|---|---|---|
| Plano free | Plano sem custo; limites definidos pelo produto (dúvida aberta). | RF-041 |
| Plano premium | Plano pago que concede entitlement. Um no MVP. | RF-041 |
| Entitlement | Direito de uso de recursos premium, decidido e guardado no servidor. | RB-009 |
| Recibo | Comprovante de compra emitido pela loja (Apple ou Google), validado no backend. | RF-042 |
| Restauração de compra | Recuperação do entitlement em novo dispositivo sem nova cobrança. | RF-042 |
| Compartilhamento de entitlement | Extensão do direito premium a outros membros da família, conforme política (dúvida aberta). | RF-043 |

## H. Privacidade e conformidade

| Termo | Definição | Rastreio |
|---|---|---|
| LGPD | Lei Geral de Proteção de Dados (Lei 13.709/2018). | RNF-003 |
| Titular | Pessoa a quem os dados se referem (o usuário; no caso do bebê, representado pelo responsável). | RNF-003 |
| Consentimento (`ConsentRecord`) | Registro versionado de aceite ou revogação, por finalidade. | RF-003 |
| Finalidade | Motivo específico e declarado do tratamento de dados. | RNF-002 |
| Exportação de dados | Entrega ao titular de seus dados em formato estruturado e legível, com timestamps, fuso e versão de esquema. | RF-044, RB-014 |
| Exclusão de conta | Remoção da conta e dos dados, respeitando retenções legais. | RF-045 |
| DSAR | Solicitação de direitos do titular (acesso, correção, portabilidade, exclusão). | discovery seção 12 |
| RIPD | Relatório de Impacto à Proteção de Dados Pessoais. | PRIV-001 |
| PII | Informação pessoal identificável. Proibida em analytics e logs. | RF-048, RNF-008 |
| Evento de auditoria (`AuditEvent`) | Registro imutável de ação crítica (conta, cuidadores, consentimentos, exclusões, compartilhamentos). | RF-055 |
| Analytics sem PII | Telemetria de produto com IDs pseudonimizados e valores em faixas (buckets), sem conteúdo livre, nome do bebê ou fotos. | RF-048 |

## I. Produto e qualidade

| Termo | Definição |
|---|---|
| RF / RNF / RB | Requisito funcional / não funcional / regra de negócio, conforme discovery seção 6. |
| MVP | Primeira entrega: conta, bebê e cuidadores, sono, cuidados, timeline, previsão básica, notificações, gráficos essenciais, assinatura, offline/sync e LGPD. |
| BFF | Back-end for Frontend: camada .NET que compõe respostas e DTOs para os apps; sem regra de negócio (ADR-0002). |
| Critério de aceite | Condição verificável em formato Dado/Quando/Então que define quando um RF está atendido. |
| Orientação, não diagnóstico | Princípio de comunicação: previsões e conteúdo nunca são apresentados como diagnóstico nem garantia. |

## J. Termos fora do MVP (reservados)

| Termo | Definição | Fase |
|---|---|---|
| Fase de desenvolvimento | Janela de idade calculada para o bebê, com conteúdo orientativo original da Nina. Nomes e estrutura serão próprios, sem reutilizar os de terceiros. | V1 |
| Sinal observável / Habilidade (marco) | Itens editoriais que o cuidador marca como observados. | V1 |
| Atividade | Sugestão de estimulação por idade, fase ou habilidade. | V1 |
| Diário | Entradas com texto, fotos, data e tags. | V1 |
| Assistente inteligente | Recurso de perguntas contextuais com guardrails e consentimento próprio. | V2 |
