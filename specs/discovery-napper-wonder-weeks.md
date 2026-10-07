# Levantamento Funcional — Napper × As Semanas Mágicas (The Wonder Weeks)

**Data do levantamento:** 07/10/2026  
**Finalidade:** base de produto para Spec-Driven Development (SDD).  

> **Nota de propriedade intelectual:** este documento cataloga capacidades e padrões funcionais publicamente observáveis. Ele não reproduz textos, assets, algoritmos proprietários nem recomenda clonar a expressão criativa, marca, conteúdo editorial ou implementação dos produtos analisados.

## 1. Objetivo e método

O levantamento combina páginas oficiais, listagens públicas de App Store e documentação/FAQ. Itens marcados **Confirmado** possuem evidência pública explícita; **Confirmado parcial** significa que a capacidade existe, mas detalhes de UX, campos ou regras não são totalmente públicos. A seção de produto consolidado é uma **especificação proposta**, não uma alegação sobre a implementação interna dos concorrentes.

## 2. Visão executiva

**Napper** é predominantemente um produto de sono e tracking operacional: registra a rotina real, aprende padrões, prevê sonecas/bedtime, apresenta analytics, conteúdo e ferramentas de relaxamento. Em 2026 sua política também descreve um assistente baseado em IA com contexto de dados do bebê.

**As Semanas Mágicas / The Wonder Weeks** é predominantemente um produto de desenvolvimento: organiza uma narrativa temporal de 10 saltos, sinais, habilidades, atividades, diário, compartilhamento e comunidade. Em 2026 está migrando para um novo ambiente online e preparando um redesign progressivo para o fim de 2026/2027.

A oportunidade de produto está em uma **linha do tempo única do bebê** que combine tracking factual (sono/cuidados), orientação de desenvolvimento, conteúdo contextual, colaboração familiar e insights — mantendo previsões como orientação, não diagnóstico.

## 3. Escopo funcional — Napper

| ID | Funcionalidade | Evidência | Descrição |
| --- | --- | --- | --- |
| N-F01 | Agenda de sono personalizada | Confirmado | Gera programação individual de sonecas e horário de dormir a partir do ritmo observado do bebê e referências de sono infantil. |
| N-F02 | Predição da próxima soneca | Confirmado | Prevê quando o bebê provavelmente estará pronto para dormir; a agenda se adapta aos dados registrados. |
| N-F03 | Wake windows | Confirmado | Usa janelas de vigília observadas no histórico; o intervalo vai do despertar até o início efetivo do próximo sono. |
| N-F04 | Registro de sono | Confirmado | Registra início/fim e duração de sono, sonecas, sono noturno e despertares. |
| N-F05 | Registro de amamentação | Confirmado | Permite acompanhar mamadas/amamentação. |
| N-F06 | Registro de mamadeira | Confirmado | Permite registrar mamadeiras/alimentação por mamadeira. |
| N-F07 | Troca de fraldas | Confirmado | Permite registrar trocas de fralda. |
| N-F08 | Bombeamento/extração | Confirmado | A política de privacidade cita tracking de pumping. |
| N-F09 | Outros eventos do bebê | Confirmado parcial | A descrição pública indica tracker de bebê com sono, alimentação, fraldas e 'mais'; eventos adicionais podem variar por versão. |
| N-F10 | Estatísticas e tendências | Confirmado | Exibe gráficos e estatísticas como tempo médio de sono, despertares noturnos e duração das mamadas. |
| N-F11 | Gráficos de wake window | Confirmado | O material oficial mostra visualizações específicas de janelas de vigília. |
| N-F12 | Sons para dormir | Confirmado | Biblioteca com mais de 30 sons, incluindo ruído branco/rosa, natureza, canções de ninar e soundscapes. |
| N-F13 | Reprodução em segundo plano | Confirmado | Sons podem continuar tocando durante a rotina de sono. |
| N-F14 | Curso/conteúdo sobre sono | Confirmado | Conteúdo estruturado e baseado em ciência sobre biologia do sono, ambiente, despertares e estratégias de sono. |
| N-F15 | Orientação por idade | Confirmado | Assinatura inclui rotinas e orientação ajustadas à idade. |
| N-F16 | Assistente baseado em IA | Confirmado | Política 2026 informa uso de dados do bebê e biblioteca interna para gerar insights e recomendações personalizadas via assistente de IA. |
| N-F17 | Compartilhamento com cuidadores | Confirmado | O bebê pode ser compartilhado com múltiplos cuidadores; dados são acompanhados de uma perspectiva comum. |
| N-F18 | Compartilhamento da assinatura | Confirmado | A assinatura é compartilhada com pessoas com quem o bebê foi compartilhado. |
| N-F19 | Conta/autenticação | Confirmado | Coleta e utiliza e-mail, nome de usuário e senha para conta/autenticação. |
| N-F20 | Notificações de sono | Confirmado parcial | Materiais públicos e depoimentos descrevem aviso antecipado de soneca; detalhes de configuração devem ser validados na implementação. |
| N-F21 | Configuração do número de sonecas | Confirmado | Descrição pública menciona seleção do número de sonecas. |
| N-F22 | Configuração de horário de dormir | Confirmado | Descrição pública menciona ajuste do bedtime para rotina personalizada. |
| N-F23 | Apple Watch | Confirmado | Compatível com Apple Watch; escopo exato de ações no relógio requer validação de versão. |
| N-F24 | Spotlight/iOS | Confirmado | Versões 2026 mostram próxima soneca e bedtime no Spotlight do iOS. |
| N-F25 | Internacionalização | Confirmado | Português + 14 idiomas na listagem iOS consultada. |
| N-F26 | Assinaturas/In-App Purchase | Confirmado | Planos Napper Sleep/Complete/Unlimited e ofertas de especialistas/recursos premium aparecem nas lojas. |
| N-F27 | Direitos de dados | Confirmado | Política prevê acesso, retificação, exclusão, restrição, portabilidade e oposição conforme GDPR/aplicável. |
| N-F28 | Geolocalização aproximada por IP | Confirmado | Usa IP para inferir país/região/cidade para localização de conteúdo, analytics e segurança; não GPS preciso. |
| N-F29 | Analytics/diagnóstico | Confirmado | Coleta dados de dispositivo, uso e diagnóstico para operação e melhoria. |
| N-F30 | Conteúdo localizado | Confirmado | Localização aproximada e idioma são usados para adequar experiência/conteúdo. |


### 3.1 Jornadas principais do Napper

1. **Onboarding → bebê → primeira agenda:** criar/entrar na conta, informar dados do bebê, começar a registrar sono e receber uma agenda/previsão personalizada.
2. **Tracking diário:** iniciar ou registrar sono; adicionar mamada, mamadeira, fralda e outros eventos; corrigir histórico.
3. **Planejamento do dia:** consultar próxima soneca/bedtime, wake window e avisos; ajustar número de sonecas/bedtime conforme a rotina.
4. **Análise:** consultar gráficos, médias, despertares, duração de sono e alimentação; identificar padrões.
5. **Sono assistido:** reproduzir ruído branco/rosa, natureza e canções de ninar.
6. **Educação:** consumir curso/artigos sobre sono e desafios comuns.
7. **Família:** compartilhar o bebê e assinatura com cuidadores.
8. **Assistente inteligente:** obter insights/recomendações personalizadas usando dados autorizados do bebê e conteúdo interno.

### 3.2 Dados explicitamente identificados no ecossistema Napper

- Conta: e-mail, username e credencial/autenticação.
- Bebê/sono: idade, duração, horários, sonecas, despertares e padrões.
- Cuidados: amamentação, mamadeira/alimentação, fraldas, pumping e outros eventos opcionais.
- Personalização de IA: política cita idade, sono, alimentação, fraldas, temperatura e primeiro nome do bebê e do responsável.
- Dispositivo/uso/diagnóstico e localização aproximada inferida por IP.

### 3.3 Pontos que exigem validação hands-on

- Campos exatos de cada tracker, regras de edição e granularidade dos gráficos.
- Fórmula/modelo exato de predição, período de histórico, confidence score e tratamento de outliers.
- Recursos completos do Apple Watch e ações disponíveis em widgets/Spotlight.
- Matriz exata free × premium por país/oferta e limites do assistente de IA.

## 4. Escopo funcional — As Semanas Mágicas / The Wonder Weeks

| ID | Funcionalidade | Evidência | Descrição |
| --- | --- | --- | --- |
| W-F01 | Perfil do bebê | Confirmado | Mantém dados do bebê necessários para personalizar cronograma e diário. |
| W-F02 | Cálculo por data prevista do parto | Confirmado | O cronograma de saltos é calculado principalmente a partir da due date/data prevista. |
| W-F03 | Cronograma personalizado de saltos | Confirmado | Mostra os 10 saltos mentais ao longo dos primeiros ~20 meses. |
| W-F04 | Início e fim do salto | Confirmado | O gráfico indica início e fim previstos de cada salto. |
| W-F05 | Fase difícil/fussy phase | Confirmado | Mostra períodos em que sinais/comportamento difícil são esperados. |
| W-F06 | Fase de habilidades | Confirmado | Mostra fase associada ao aparecimento/exploração de novas habilidades. |
| W-F07 | Easy spell | Confirmado | Cronograma identifica períodos mais tranquilos. |
| W-F08 | Notificação pré-salto | Confirmado | Envia notificação automática quando um salto está prestes a começar. |
| W-F09 | Sinais do salto | Confirmado | Permite reconhecer e marcar sinais associados ao salto. |
| W-F10 | Habilidades | Confirmado | Apresenta habilidades potenciais por salto e permite acompanhar/marcar progresso. |
| W-F11 | 77 atividades/jogos | Confirmado | Oferece 77 atividades para estimular habilidades emergentes. |
| W-F12 | Diário pessoal | Confirmado | Permite registrar desenvolvimento do bebê em diário. |
| W-F13 | Texto no diário | Confirmado | FAQ documenta limite aproximado de 700/750 caracteres por entrada em versões existentes. |
| W-F14 | Fotos no diário | Confirmado | FAQ menciona múltiplas fotos salvas no diário e exportação em PDF. |
| W-F15 | Exportação/PDF do diário | Confirmado | FAQ cita PDF gerado a partir do diário; detalhes do formato devem ser validados. |
| W-F16 | Compartilhamento com parceiro | Confirmado | Assinantes elegíveis podem convidar 1 pessoa por e-mail. |
| W-F17 | Sincronização com parceiro | Confirmado | Sincroniza diário, sinais, habilidades e jogos marcados. |
| W-F18 | Fórum/comunidade | Confirmado | Usuários podem compartilhar experiências e fazer perguntas. |
| W-F19 | Vídeos de parentalidade | Confirmado | Inclui vídeos sobre parentalidade. |
| W-F20 | Enquetes/polls | Confirmado | Permite responder enquetes e visualizar opinião de outros pais. |
| W-F21 | Conta | Confirmado | Conta permite armazenamento online e transição entre dispositivos/plataformas. |
| W-F22 | Backup | Confirmado | Versões existentes suportam backup/restauração; iCloud/Google Drive e nova infraestrutura online aparecem na documentação. |
| W-F23 | Restauração | Confirmado | Permite restaurar backup mais recente e, em fluxos avançados, selecionar backup. |
| W-F24 | Transferência entre plataformas | Confirmado | Com conta, documentação informa login em outro dispositivo Apple/Android; há documentação histórica divergente, portanto validar na versão atual. |
| W-F25 | Sessão por dispositivo | Confirmado | FAQ 2024 informa que conta pode ficar logada em um dispositivo por vez. |
| W-F26 | Assinaturas | Confirmado | Há planos de 1, 3 e 24 meses e pacotes/Extras, além de compras internas. |
| W-F27 | Compartilhamento condicionado ao plano | Confirmado | Compartilhamento com parceiro depende de assinatura elegível. |
| W-F28 | Extras | Confirmado | O ecossistema inclui recursos/produtos extras acessíveis pelo app. |
| W-F29 | Baby Monitor | Confirmado | Baby Monitor pode ser obtido separadamente e/ou via Extras; pareia dois dispositivos por QR code. |
| W-F30 | Conteúdo editorial | Confirmado | Explica cada salto, sinais, habilidades e orientação aos pais. |
| W-F31 | Internacionalização | Confirmado | Português + 15 idiomas na App Store brasileira consultada. |
| W-F32 | Novo backend/armazenamento online | Confirmado | Em 2026 o produto está migrando para novo ambiente online e conta passa a ser central para backup e futuras funcionalidades. |
| W-F33 | Exportação de dados na migração | Confirmado | FAQ 2026 informa disponibilização de arquivo para usuários que não migrarem/criarem conta. |
| W-F34 | Manutenção/migração comunicada in-app | Confirmado | Migração prevê notificação in-app sobre janela de manutenção. |
| W-F35 | Redesign progressivo 2026–2027 | Confirmado | Site informa introdução gradual do redesign e novos recursos a partir do fim de 2026. |


### 4.1 Jornadas principais do Wonder Weeks

1. **Onboarding → bebê → cronograma:** cadastrar dados do bebê/data prevista e receber cronograma personalizado dos saltos.
2. **Acompanhar salto atual:** ver fase, sinais esperados, habilidades e contexto editorial.
3. **Registrar desenvolvimento:** marcar sinais/habilidades/jogos e criar entradas no diário, inclusive com fotos.
4. **Estimular:** escolher atividades apropriadas à fase e acompanhar o que foi realizado.
5. **Antecipar mudanças:** receber notificação antes do início previsto de um salto.
6. **Compartilhar:** convidar parceiro elegível e sincronizar diário, sinais, habilidades e jogos.
7. **Comunidade:** fórum, experiências, perguntas, vídeos e enquetes.
8. **Continuidade de dados:** conta, backup/restore, migração para ambiente online e exportação em cenários previstos.
9. **Extras:** acesso a produtos/recursos relacionados, incluindo Baby Monitor.

### 4.2 Regras públicas relevantes

- O cronograma usa a **data prevista do parto** como referência central para os saltos.
- O produto apresenta **10 saltos mentais** nos primeiros ~20 meses.
- Compartilhamento com parceiro depende de assinatura elegível e a documentação consultada cita convite de **1 pessoa**.
- Dados sincronizados com parceiro incluem diário, sinais, habilidades e jogos.
- O ecossistema está em transição em 2026 para armazenamento online ligado à conta; documentação antiga de backup/plataforma pode coexistir com a nova arquitetura.

### 4.3 Pontos que exigem validação hands-on

- UX e regras exatas do redesign que começa a aparecer no fim de 2026.
- Campos atuais do diário e formato final da exportação.
- Permissões, moderação e identidade no fórum.
- Escopo comercial atual de Extras/Pro Pack/eBooks por região.
- Comportamento atual de multi-device após a migração de backend.

## 5. Matriz comparativa

| Capacidade | Napper | Wonder Weeks | Destaque |
| --- | --- | --- | --- |
| Predição de sono adaptativa | Forte | Não é foco | Napper |
| Registro detalhado de sono | Forte | Secundário/não central | Napper |
| Alimentação/fraldas/pumping | Sim | Não identificado como core | Napper |
| Saltos de desenvolvimento | Não identificado como core | Forte: 10 saltos | Wonder Weeks |
| Sinais/habilidades por salto | Não identificado | Forte | Wonder Weeks |
| Atividades de estimulação | Conteúdo/orientação | 77 jogos/atividades | Wonder Weeks |
| Diário de desenvolvimento | Tracker/estatísticas | Diário explícito com fotos | Wonder Weeks |
| Gráficos/analytics | Forte | Cronograma/gráfico de saltos | Ambos, objetivos distintos |
| Conteúdo educacional | Sono/parentalidade | Desenvolvimento/parentalidade | Ambos |
| Sons/ruído branco/lullabies | Forte | Não identificado no app principal | Napper |
| Compartilhamento cuidador/parceiro | Múltiplos cuidadores | 1 parceiro em planos elegíveis | Ambos |
| Comunidade/fórum | Não identificado | Sim | Wonder Weeks |
| Enquetes | Não identificado | Sim | Wonder Weeks |
| Assistente IA personalizado | Confirmado em 2026 | Não identificado | Napper |
| Baby monitor | Não identificado | Produto/extra relacionado | Wonder Weeks |
| Backup/cloud | Conta/serviço | Explícito + migração 2026 | Ambos |
| Notificações | Sono/soneca | Início de salto | Ambos |


## 6. Escopo consolidado de um produto de referência

O produto consolidado deve ser concebido como uma plataforma própria, com domínio e conteúdo originais. A combinação recomendada é: **tracking factual + agenda de sono adaptativa + desenvolvimento/marcos + diário + atividades + conteúdo + colaboração familiar + analytics + IA com guardrails**.

### 6.1 Épicos

| ID | Épico | Escopo |
| --- | --- | --- |
| EP-01 | Identidade, conta e consentimento | Cadastro/login, recuperação, consentimentos, termos, privacidade, exclusão e portabilidade. |
| EP-02 | Família, bebê e cuidadores | Perfis de bebê, responsáveis, convites, papéis e compartilhamento. |
| EP-03 | Linha do tempo do bebê | Timeline única de sono, alimentação, fraldas, pumping, desenvolvimento, notas e fotos. |
| EP-04 | Sono e agenda inteligente | Tracking de sono, wake windows, agenda adaptativa, previsão de sonecas/bedtime. |
| EP-05 | Alimentação e cuidados | Mamadas, mamadeira, pumping, fraldas e eventos configuráveis. |
| EP-06 | Desenvolvimento e marcos | Fases de desenvolvimento, sinais, habilidades, marcos e calendário. |
| EP-07 | Atividades e estimulação | Catálogo de atividades por idade/fase/habilidade, favoritos e conclusão. |
| EP-08 | Diário e memórias | Entradas textuais, fotos, tags, exportação e retrospectivas. |
| EP-09 | Insights e analytics | Gráficos, tendências, médias, correlações e resumos semanais. |
| EP-10 | Conteúdo e educação | Artigos, cursos, vídeos, áudio, trilhas e conteúdo contextual. |
| EP-11 | Áudio para sono | Ruído branco/rosa, natureza, lullabies, timer e background playback. |
| EP-12 | Assistente inteligente | Perguntas e recomendações personalizadas com guardrails e transparência. |
| EP-13 | Notificações e lembretes | Eventos previstos, rotinas, desenvolvimento, conteúdo e controles de quiet hours. |
| EP-14 | Comunidade opcional | Fórum, tópicos, moderação, denúncias, enquetes e controles de privacidade. |
| EP-15 | Assinaturas e monetização | Free/premium, trial, entitlement, restore purchase, family sharing e paywall. |
| EP-16 | Offline, sync e backup | Operação offline-first, sincronização, conflitos, backup e exportação. |
| EP-17 | Admin/CMS | Conteúdo, atividades, fases, traduções, feature flags e campanhas. |
| EP-18 | Segurança, LGPD e observabilidade | Auditoria, criptografia, minimização, retenção, monitoramento e incidentes. |


### 6.2 Requisitos funcionais

- **RF-001** — Permitir criar conta com e-mail e autenticar com sessão segura.
- **RF-002** — Permitir uso de recuperação de acesso e encerramento de sessões.
- **RF-003** — Registrar aceite versionado de Termos, Política de Privacidade e consentimentos aplicáveis.
- **RF-004** — Permitir criar um ou mais perfis de bebê com nome/apelido, data de nascimento, data prevista do parto, sexo opcional, fuso e foto opcional.
- **RF-005** — Calcular idade cronológica e idade corrigida quando aplicável, preservando a data prevista para regras de desenvolvimento configuráveis.
- **RF-006** — Permitir convidar cuidadores e atribuir papéis Owner, Caregiver e ReadOnly.
- **RF-007** — Sincronizar dados compartilhados do bebê entre cuidadores autorizados.
- **RF-008** — Registrar sono com início, fim, tipo (soneca/noturno), método/local opcional e observações.
- **RF-009** — Permitir iniciar/parar timer de sono e corrigir registros retroativamente.
- **RF-010** — Calcular duração, total diário, número de sonecas, despertares e wake windows.
- **RF-011** — Gerar previsão de próxima soneca e bedtime a partir de idade, histórico recente e parâmetros do bebê.
- **RF-012** — Recalcular previsões quando um evento de sono for criado, alterado ou excluído.
- **RF-013** — Exibir nível de confiança/explicação simples para previsões quando tecnicamente possível.
- **RF-014** — Permitir configurar meta/estrutura de número de sonecas e faixa preferida de bedtime.
- **RF-015** — Registrar amamentação por lado, início/fim e duração.
- **RF-016** — Registrar mamadeira com volume, tipo de leite opcional e horário.
- **RF-017** — Registrar pumping/extração com duração e volume opcional.
- **RF-018** — Registrar fralda com tipo e observações opcionais.
- **RF-019** — Exibir timeline cronológica unificada de eventos do bebê.
- **RF-020** — Permitir editar/excluir eventos com trilha de auditoria mínima para dados compartilhados.
- **RF-021** — Exibir calendário/fases de desenvolvimento calculadas por regra configurável.
- **RF-022** — Exibir início/fim de fases, períodos de maior sensibilidade e janelas de aquisição de habilidades como conteúdo orientativo, sem alegação diagnóstica.
- **RF-023** — Listar sinais observáveis por fase e permitir ao cuidador marcá-los como observados.
- **RF-024** — Listar habilidades/marcos e permitir marcar observado, data e nota.
- **RF-025** — Oferecer atividades apropriadas à idade/fase/habilidade e registrar conclusão/favorito.
- **RF-026** — Permitir criar entradas de diário com texto, fotos, data e tags.
- **RF-027** — Permitir exportar diário/memórias em PDF ou arquivo estruturado.
- **RF-028** — Exibir dashboards de sono, alimentação e cuidados por dia/semana/mês.
- **RF-029** — Exibir médias, tendências e comparações com o próprio histórico do bebê.
- **RF-030** — Gerar resumo semanal do bebê com eventos relevantes e mudanças observadas.
- **RF-031** — Disponibilizar biblioteca editorial pesquisável por tema e faixa etária.
- **RF-032** — Suportar cursos/trilhas com capítulos e progresso.
- **RF-033** — Disponibilizar biblioteca de áudio para sono com categorias, favoritos e reprodução em background.
- **RF-034** — Permitir timer de áudio e encerramento automático.
- **RF-035** — Disponibilizar assistente inteligente contextual ao bebê somente mediante consentimento adequado.
- **RF-036** — O assistente deve utilizar apenas dados autorizados e conteúdo curado, deixando claro que não fornece diagnóstico médico.
- **RF-037** — Permitir configurar notificações de próxima soneca, bedtime, rotina e fases de desenvolvimento.
- **RF-038** — Permitir quiet hours, antecedência e categorias de notificação.
- **RF-039** — Entregar notificações de forma idempotente e registrar status de envio.
- **RF-040** — Permitir ao usuário visualizar e alterar preferências de privacidade e comunicação.
- **RF-041** — Implementar plano gratuito e um ou mais planos premium com entitlement server-side.
- **RF-042** — Integrar compras/assinaturas Apple e Google, incluindo restauração e validação de recibo.
- **RF-043** — Compartilhar entitlement premium conforme política familiar definida pelo produto.
- **RF-044** — Permitir exportar dados pessoais em formato estruturado e legível.
- **RF-045** — Permitir solicitar exclusão da conta e dados, observando retenções legais.
- **RF-046** — Operar funções essenciais de tracking offline e sincronizar posteriormente.
- **RF-047** — Resolver conflitos de sincronização por versão/timestamp e regras por tipo de entidade.
- **RF-048** — Registrar analytics de produto sem incluir conteúdo sensível desnecessário.
- **RF-049** — Disponibilizar CMS/admin para artigos, atividades, áudios, fases, traduções e feature flags.
- **RF-050** — Permitir suporte a múltiplos idiomas e conteúdo localizado.
- **RF-051** — Permitir moderação e denúncia caso comunidade seja habilitada.
- **RF-052** — Permitir enquetes anônimas/agregadas caso o módulo de comunidade seja habilitado.
- **RF-053** — Permitir backup seguro em nuvem e restauração vinculada à conta.
- **RF-054** — Exibir histórico de dispositivos/sessões e permitir revogação.
- **RF-055** — Registrar auditoria de alterações críticas: conta, cuidadores, consentimentos, exclusões e compartilhamentos.


### 6.3 Requisitos não funcionais

| ID | Categoria | Requisito |
| --- | --- | --- |
| RNF-001 | Segurança | TLS 1.2+ em trânsito; criptografia forte em repouso; segredos em cofre; rotação de chaves. |
| RNF-002 | Privacidade | Privacy by design, minimização e finalidade explícita; dados da criança tratados como de alta sensibilidade operacional. |
| RNF-003 | LGPD | Base legal, transparência, direitos do titular, registro de consentimento quando aplicável, DPA com operadores e processo de incidente. |
| RNF-004 | Disponibilidade | Meta inicial de 99,9% para APIs críticas, excluindo manutenção planejada. |
| RNF-005 | Performance | p95 de APIs interativas < 500 ms em condições normais; abertura da timeline local deve ser imediata/offline. |
| RNF-006 | Escalabilidade | Serviços stateless quando possível; filas para processamento assíncrono; particionamento lógico por usuário/bebê. |
| RNF-007 | Offline | Tracking crítico deve funcionar sem conectividade; sync deve ser resiliente e idempotente. |
| RNF-008 | Observabilidade | Logs estruturados, métricas, tracing, crash reporting e alertas com remoção/mascaramento de PII. |
| RNF-009 | Acessibilidade | WCAG 2.2 AA como meta para web/admin e boas práticas equivalentes iOS/Android. |
| RNF-010 | Internacionalização | Strings externas ao código, pluralização, locale, timezone, unidades e formatos regionais. |
| RNF-011 | Confiabilidade | Jobs e notificações idempotentes; retries com backoff; dead-letter queue. |
| RNF-012 | Dados | Backups automatizados, testes de restauração e RPO/RTO definidos por ambiente. |
| RNF-013 | IA responsável | Prompt/data isolation, filtros, logging seguro, avaliação de respostas e escalonamento para orientação profissional quando necessário. |
| RNF-014 | Conteúdo de saúde | Não apresentar previsões de sono/desenvolvimento como diagnóstico ou garantia; incluir disclaimers contextuais. |
| RNF-015 | Manutenibilidade | Arquitetura modular, contratos versionados, testes automatizados e documentação de domínio. |
| RNF-016 | Compatibilidade | Definir baseline de versões iOS/Android; graceful degradation para integrações específicas de plataforma. |


### 6.4 Regras de negócio

- **RB-001** — Toda previsão deve ser recalculável; nunca deve sobrescrever o evento real registrado.
- **RB-002** — O histórico real do bebê tem precedência sobre agenda prevista.
- **RB-003** — Alterar um evento passado pode alterar métricas e previsões futuras.
- **RB-004** — A data prevista do parto deve ser armazenada separadamente da data de nascimento.
- **RB-005** — Conteúdo de desenvolvimento é orientativo e não deve afirmar que toda criança adquirirá habilidade em data exata.
- **RB-006** — Um cuidador só acessa um bebê após convite/aceite ou criação própria.
- **RB-007** — Somente Owner pode remover outro cuidador, transferir propriedade ou excluir o perfil do bebê.
- **RB-008** — Exclusão de evento compartilhado deve ser sincronizada como tombstone até convergência dos dispositivos.
- **RB-009** — Entitlements premium devem ser validados no backend; o cliente não é fonte de verdade.
- **RB-010** — Notificações devem respeitar timezone do bebê/família e quiet hours.
- **RB-011** — Nenhum conteúdo de diário/foto deve ser usado para analytics ou IA além da finalidade consentida.
- **RB-012** — Recomendações de IA devem registrar versão do modelo/prompt e fontes internas usadas quando aplicável.
- **RB-013** — Métricas comparativas devem priorizar o histórico individual, evitando ranking de desenvolvimento entre crianças.
- **RB-014** — Dados exportados devem incluir timestamps, timezone e versão de esquema.
- **RB-015** — Ao revogar compartilhamento, novos acessos devem cessar imediatamente e tokens/cache devem ser invalidados.


## 7. Modelo conceitual de dados

| Entidade | Responsabilidade | Campos principais (indicativos) |
| --- | --- | --- |
| User | Conta do adulto responsável | id, email, locale, timezone, status, created_at |
| Baby | Perfil da criança | id, owner_id, display_name, birth_date, due_date, timezone, photo_ref |
| CaregiverMembership | Vínculo usuário↔bebê | baby_id, user_id, role, status, invited_at, accepted_at |
| SleepSession | Sessão real de sono | baby_id, start_at, end_at, sleep_type, source, notes |
| SleepPrediction | Previsão calculada | baby_id, predicted_start, predicted_end, kind, confidence, model_version |
| FeedingSession | Evento de alimentação | baby_id, type, start_at, end_at, side, volume_ml |
| PumpingSession | Extração | baby_id, start_at, end_at, volume_ml |
| DiaperEvent | Troca de fralda | baby_id, occurred_at, type, notes |
| DevelopmentPhase | Definição editorial de fase | phase_key, age_rule, title, content_version |
| BabyPhaseWindow | Janela calculada para bebê | baby_id, phase_key, start_at, end_at, status |
| SignalDefinition | Sinal editorial | phase_key, signal_key, localized_content |
| ObservedSignal | Sinal marcado | baby_id, signal_key, observed_at, caregiver_id |
| SkillDefinition | Habilidade/marco editorial | skill_key, phase/age tags, localized_content |
| ObservedSkill | Habilidade observada | baby_id, skill_key, observed_at, notes |
| ActivityDefinition | Atividade editorial | activity_key, age/skill tags, instructions, media |
| ActivityProgress | Uso de atividade | baby_id, activity_key, status, completed_at, favorite |
| DiaryEntry | Diário | baby_id, author_id, occurred_at, text, tags |
| MediaAsset | Foto/áudio/vídeo | owner/context, storage_key, mime, metadata |
| ContentItem | Artigo/curso/capítulo | type, locale, age_tags, body_ref, version |
| AudioTrack | Som para sono | category, duration, asset_ref, locale |
| NotificationPreference | Preferências | user_id, baby_id, category, enabled, lead_time, quiet_hours |
| NotificationJob | Entrega | event_key, scheduled_at, status, attempts |
| Subscription | Entitlement | user_id/family_id, plan, store, status, valid_until |
| ConsentRecord | Consentimento versionado | user_id, purpose, policy_version, granted_at, revoked_at |
| AIConversation | Contexto do assistente | user_id, baby_id, created_at, retention_policy |
| AIMessage | Mensagem IA | conversation_id, role, content_ref/redacted, model_version |
| AuditEvent | Auditoria | actor, action, entity, entity_id, timestamp, metadata_safe |


### 7.1 Relacionamentos centrais

- `User N:N Baby` por `CaregiverMembership`.
- `Baby 1:N` SleepSession, SleepPrediction, FeedingSession, PumpingSession, DiaperEvent, DiaryEntry, ObservedSignal e ObservedSkill.
- `DevelopmentPhase 1:N SignalDefinition/SkillDefinition/ActivityDefinition` por tags/regras.
- `Baby + DevelopmentPhase → BabyPhaseWindow` materializa/calcula a janela individual.
- `User/Family 1:N Subscription/ConsentRecord/NotificationPreference`.
- Conteúdo e mídia devem ser versionados e referenciados, evitando duplicação em registros históricos.

## 8. Eventos de domínio e processamento assíncrono

- `BabyCreated`
- `CaregiverInvited`
- `CaregiverAccepted`
- `CaregiverRemoved`
- `SleepStarted`
- `SleepEnded`
- `SleepUpdated`
- `SleepDeleted`
- `SleepPredictionRecalculated`
- `FeedingLogged`
- `PumpingLogged`
- `DiaperLogged`
- `SignalObserved`
- `SkillObserved`
- `ActivityCompleted`
- `DiaryEntryCreated`
- `WeeklySummaryReady`
- `DevelopmentPhaseApproaching`
- `NapApproaching`
- `BedtimeApproaching`
- `SubscriptionActivated`
- `SubscriptionExpired`
- `ConsentGranted`
- `ConsentRevoked`
- `DataExportRequested`
- `AccountDeletionRequested`

### 8.1 Jobs sugeridos

- Recalcular previsões após mudanças em sono.
- Materializar resumo diário/semanal.
- Agendar/cancelar notificações de soneca, bedtime e desenvolvimento.
- Processar mídia (resize, thumbnail, antivírus e metadados).
- Validar/renovar entitlements de lojas.
- Executar exportações LGPD/diário de forma assíncrona.
- Limpar dados conforme retenção e consentimento.
- Gerar embeddings/índices apenas para conteúdo autorizado do assistente.

## 9. API/domínios sugeridos

- **Identity API:** auth, sessions, consent, export/delete.
- **Family API:** babies, caregivers, invitations, roles.
- **Tracking API:** sleep, feeding, pumping, diaper, timeline.
- **Sleep Intelligence API:** wake windows, predictions, schedule, recalculation.
- **Development API:** phases, signals, skills, observations, activities.
- **Diary API:** entries, tags, media, export.
- **Content API:** articles, courses, videos, audio and localization.
- **Notification API:** preferences, schedules, delivery callbacks.
- **Subscription API:** products, offers, receipts, entitlements.
- **AI Gateway:** contextual retrieval, redaction, policy enforcement, provider abstraction.
- **Analytics ingestion:** first-party event endpoint with privacy filtering.

## 10. Notificações

| Categoria | Trigger | Exemplo de regra |
| --- | --- | --- |
| Sono | Previsão próxima | X minutos antes da próxima soneca prevista; cancelar/reagendar após novo evento. |
| Bedtime | Agenda diária | Antecedência configurável e respeito a quiet hours. |
| Desenvolvimento | Fase se aproximando | Avisar quando uma fase configurada estiver próxima; linguagem probabilística. |
| Rotina | Preferência do usuário | Lembretes opcionais de registro/rotina. |
| Conteúdo | Contexto | Sugerir conteúdo relevante sem excesso de frequência. |
| Assinatura | Billing | Trial terminando, renovação/falha conforme regras da loja. |
| Sistema | Segurança | Novo login, mudança de e-mail, cuidador adicionado/removido. |


## 11. Analytics — taxonomia inicial

| Evento | Propriedades permitidas (exemplos) |
| --- | --- |
| onboarding_started | locale, platform, acquisition_channel |
| baby_profile_created | age_bucket, has_due_date |
| caregiver_invited | role |
| sleep_timer_started | sleep_type |
| sleep_logged | sleep_type, duration_bucket, source |
| prediction_viewed | prediction_type, confidence_bucket |
| prediction_outcome | prediction_type, delta_minutes_bucket |
| feeding_logged | feeding_type |
| development_phase_viewed | phase_key |
| signal_marked | phase_key, signal_key |
| skill_marked | phase_key, skill_key |
| activity_opened | activity_key, source |
| activity_completed | activity_key |
| diary_entry_created | has_photo, tag_count |
| content_opened | content_id, content_type, source |
| audio_started | track_id, category |
| notification_opened | category |
| paywall_viewed | placement, offer |
| trial_started | plan, store |
| subscription_started | plan, store |
| ai_question_sent | topic_taxonomy, context_scope |


**Princípio:** não enviar texto do diário, nome do bebê, fotos, conteúdo de conversa ou identificadores sensíveis a ferramentas de analytics de terceiros. Preferir IDs pseudonimizados e buckets.

## 12. Segurança e LGPD

- Classificar dados do bebê/família como dados de alto impacto e aplicar minimização.
- Mapear controlador, operadores/suboperadores, transferências internacionais e contratos.
- Consentimentos separados quando a finalidade for opcional (IA, marketing, pesquisa, comunidade, analytics não essencial).
- Criptografia em trânsito e repouso; URLs de mídia assinadas e de curta duração.
- RBAC por bebê/família e autorização server-side em todas as consultas.
- Auditoria de compartilhamento, exportação, exclusão, consentimentos e ações administrativas.
- Processo DSAR: acesso, correção, portabilidade e exclusão.
- Retenção explícita para logs, mídia, backups, conversas de IA e contas encerradas.
- Proteção contra enumeração de contas, credential stuffing, abuso de convite e upload malicioso.
- Conteúdo sobre saúde deve ser informativo; fluxos de risco devem recomendar avaliação profissional/emergência quando apropriado.

## 13. Offline e sincronização

O tracking deve ser **offline-first**. Cada mutação local recebe UUID, `client_created_at`, versão e estado de sincronização. O backend confirma com versão canônica. Conflitos simples podem usar last-write-wins somente onde não houver perda clínica/afetiva relevante; para diário e eventos editados por dois cuidadores, preservar histórico e permitir reconciliação. Exclusões devem usar tombstones temporários para evitar ressurreição de dados em dispositivos atrasados.

## 14. Critérios de aceite de alto nível

- Um cuidador consegue registrar sono offline em poucos toques e vê o evento sincronizado em outro dispositivo após reconexão.
- Ao alterar o fim de uma soneca, wake window, totais e próxima previsão são recalculados sem modificar o registro real.
- Um usuário não autorizado nunca recebe dados de bebê por ID previsível, cache, push ou mídia.
- Revogar um cuidador remove acesso ativo e invalida sessões/recursos compartilhados.
- Notificações não duplicam após retries e são recalculadas quando a previsão muda.
- Exportação contém dados do titular em formato estruturado e o diário em formato humano quando solicitado.
- IA não apresenta diagnóstico; respostas usam contexto autorizado e passam por política de segurança.
- Assinatura restaurada em novo dispositivo recupera entitlement sem duplicar cobrança.
- Troca de timezone não corrompe eventos históricos; timestamps persistem em UTC + timezone contextual.

## 15. Roadmap sugerido

| Fase | Objetivo | Inclui |
| --- | --- | --- |
| MVP | Valor diário e retenção | Conta, bebê/cuidadores, sono, alimentação/fralda, timeline, wake windows, previsão básica, notificações, gráficos essenciais, assinatura, offline/sync, LGPD. |
| V1 | Plataforma parental completa | Desenvolvimento/marcos, atividades, diário/fotos, conteúdo/CMS, áudio para sono, resumos semanais, exportação e localização PT/EN/ES. |
| V2 | Personalização avançada | Assistente IA com RAG, correlações, previsão mais sofisticada, Apple Watch/widgets, comunidade moderada/enquetes e integrações adicionais. |


## 16. Riscos e questões em aberto

1. **Validade científica e comunicação:** previsões de sono e fases de desenvolvimento precisam de revisão especializada e linguagem não determinística.
2. **IP/conteúdo:** não reutilizar nomes proprietários de fases, textos, ilustrações, jogos ou estrutura editorial protegida dos concorrentes.
3. **IA + dados infantis:** definir base legal/finalidade, minimização, retenção e fornecedores antes de enviar contexto a LLM.
4. **Multi-caregiver:** definir propriedade do bebê, transferência de ownership, separação familiar e disputas de acesso.
5. **Predição:** definir dataset, cold start, explicabilidade, métricas de precisão e fallback baseado em regras.
6. **Monetização:** decidir o que permanece gratuito sem comprometer utilidade básica.
7. **Comunidade:** aumenta substancialmente escopo de Trust & Safety, moderação e responsabilidade.
8. **Dados médicos:** evitar transformar tracking parental em prontuário/diagnóstico sem estratégia regulatória específica.

## 17. Como usar este documento em Spec-Driven Development

Use este arquivo como **discovery/product scope**, não como única especificação executável. A próxima etapa deve decompor o material em specs versionadas:

```text
/specs
  product-spec.md
  glossary.md
  domain-model.md
  architecture.md
  database-spec.md
  api-spec.md
  mobile-spec.md
  sleep-engine-spec.md
  development-content-spec.md
  notification-spec.md
  subscription-spec.md
  ai-assistant-spec.md
  analytics-spec.md
  privacy-security-spec.md
  test-strategy.md
  implementation-plan.md
```

Fluxo recomendado: **Product Spec → Domain Model → ADRs/Architecture → API/DB → Mobile UX contracts → feature specs → acceptance tests → implementation plan → código**. Cada RF deve ser rastreável até uma feature, endpoints/eventos, entidades e testes.

## 18. Fontes públicas consultadas

Data de acesso/levantamento: **07/10/2026**.

- **Napper — site oficial (PT)** — https://napper.app/pt/ — Agenda de sono, sons, conteúdo, tracking e gráficos.
- **Napper — App Store Brasil** — https://apps.apple.com/br/app/napper-hor%C3%A1rio-do-sono/id1491340863 — Descrição funcional, plataformas, idiomas, IAP e versão.
- **Napper — Privacy Policy** — https://napper.app/privacy/ — Dados coletados, IA, geolocalização aproximada, direitos e compartilhamento.
- **Napper — Terms of Use** — https://napper.app/terms-of-service/ — Conta, restrições, saúde, segurança e proteção de dados.
- **Napper — Wake windows** — https://napper.app/en/blog/baby-sleep/wake-windows-by-age/ — Como wake windows são entendidas e uso dos dados registrados.
- **Napper — Partnership/FAQ público** — https://napper.app/partnerships/sami/ — Recursos de assinatura e compartilhamento.
- **The Wonder Weeks — app oficial** — https://thewonderweeks.com/the-wonder-weeks-app/ — Cronograma, fases, notificações, habilidades, 77 atividades, diário, parceiro e fórum.
- **The Wonder Weeks — App Store Brasil** — https://apps.apple.com/br/app/as-semanas-magicas/id529815782 — Plataformas, idiomas, IAP e classificação.
- **The Wonder Weeks — App Store US** — https://apps.apple.com/us/app/the-wonder-weeks-baby-leaps/id529815782 — Descrição detalhada de recursos, vídeos e polls.
- **The Wonder Weeks — FAQ** — https://thewonderweeks.com/faq/ — Conta, migração 2026, armazenamento online, exportação e roadmap do redesign.
- **The Wonder Weeks — compartilhar com parceiro** — https://thewonderweeks.com/efaq/can-i-share-the-app-with-my-partner/ — Convite, elegibilidade e sincronização de diário/sinais/habilidades/jogos.
- **The Wonder Weeks — backup/restore** — https://thewonderweeks.com/efaq/how-do-i-back-up-and-or-restore-my-data-in-the-app/ — Backup automático/manual e restauração.
- **The Wonder Weeks — FAQ existente** — https://thewonderweeks.com/faqs-existing-users/the-wonder-weeks/ — Índice de capacidades: diário, PDF, notificações, promo code e transferência.
- **The Wonder Weeks — Baby Monitor** — https://thewonderweeks.com/efaq/how-do-i-install-the-babymonitor/ — Pareamento de dois dispositivos por QR code e acesso via Extras.

## 19. Limitações do levantamento

Este é um levantamento de **black-box/public product research**. Sem acesso autenticado e instrumentado a todas as versões/planos dos apps, não é possível afirmar a existência de cada tela, campo, endpoint, algoritmo ou regra interna. A implementação proposta deve passar por validação de produto, jurídico/LGPD, conteúdo clínico/desenvolvimento e testes com usuários.
