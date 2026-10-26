# Spike de sincronização — ARCH-003

Status: concluído (spike descartável; o desenho recomendado alimenta o BE-004) · Data: 2026-10-08
Fontes: ADR-0003, ADR-0009, `specs/database-spec.md` (seções 5, 6, 9, 10 e [Q2]), `backend/db/migrations/0001_init.sql`, `contracts/openapi.yaml` (`/sync/push`, `/sync/pull`; **versão 1.0.1 vigente no repositório**, ver 4.2), `specs/api-spec.md` (seção 3), `specs/security-review-001.md` (SR-014, SR-021).
Código: `backend/spikes/Nina.SyncSpike` (servidor + harness + bench) e `backend/spikes/Nina.SyncSpike.Tests` (46 testes xUnit). **Isolado**: não está em `Nina.sln`, não altera `Nina.Api`/`Nina.Bff`, `0001_init.sql` nem o contrato (as divergências estão listadas como recomendações na seção 7).

## 1. Resumo executivo

| Pergunta do ARCH-003 | Resposta (com evidência) |
|---|---|
| Sequência sem lacuna visível fora de ordem de commit? | **Sim**, com o contador serializado por bebê de `baby_sync_head` (database-spec 6.2). 5 escritores concorrentes × 30 lotes com rollbacks por savepoint + leitor ao vivo: 0 violações; `change_log` = `1..N` = `last_sequence`; `changed_at` monotônico. Mesma checagem em 10 configurações de throughput (até 16 workers): sempre contíguo. O pull usa `sync_sequence > cursor` **sem horizonte de segurança**. |
| Formato do cursor | Opaco, **mapa `baby_id -> [sequência, época-do-vínculo]`** + instante de emissão + posição de snapshot opcional, JSON compacto assinado com HMAC-SHA256 truncado a 128 bits (base64url). 135 caracteres para 1 bebê (o contrato é por bebê); cabem até 5 bebês em 512 caracteres. |
| Política de conflito | **LWW por campo + auditoria** funciona, mas exige informação que o banco **não tem** (qual campo mudou depois de `base_version`). Spike implementa as duas ordens: `ServerArrival` (padrão, = contrato 1.0.1) e `ClientClockClamped` (proposta original do ADR-0003, sem o furo de relógio no futuro). |
| Idempotência | Por `mutation_id`, com lock consultivo: 12 pushes simultâneos idênticos → 1 `APPLIED` + 11 `DUPLICATE`, 1 linha no `change_log`. |
| Cursor expirado | `410 SYNC_CURSOR_EXPIRED` por **idade** (> 90 dias) e por **purga** (`cursor < purged_through`); `reason=INVALID` para adulterado, de outro bebê/usuário, "do futuro" ou vínculo refeito. Resync completo preserva a fila. |
| Custo | Push de 1 mutação ≈ 5 ms p50 (fsync ligado); lote de 100 ≈ 330 ms (3,3 ms/mutação); teto de ~300 mutações/s **por bebê** (serialização do contador), ~1.150 mutações/s com 16 bebês em paralelo em 4 CPUs compartilhadas com o PostgreSQL. |
| **Achado mais importante** | As políticas RLS do `0001_init.sql` chamam `can_read_baby(baby_id)` **por linha**: snapshot de 20 mil eventos leva **29,4 s**; com política por conjunto + índice `(baby_id, id)` leva **0,41 s** (72×); delta de 500 mudanças 76 → 8,5 ms. A semântica é idêntica (a suíte inteira passa nos dois modos). Ver R-03. |

## 2. Como reproduzir

```bash
export PATH=/opt/dotnet:$PATH
cd /home/user/nina/backend/spikes
dotnet build Nina.SyncSpike.Tests/Nina.SyncSpike.Tests.csproj -c Release
dotnet test  Nina.SyncSpike.Tests/Nina.SyncSpike.Tests.csproj -c Release            # 46 testes
NINA_SPIKE_TUNED=1 dotnet test Nina.SyncSpike.Tests/Nina.SyncSpike.Tests.csproj -c Release   # idem, com spike_tuning.sql
dotnet run -c Release --project Nina.SyncSpike -- bench [arquivo-de-saída]          # ~4 min
```

- Cada execução faz `initdb` em `/tmp/nina-syncspike-pg-*` (porta alta livre, `trust`, **fsync ligado**), aplica `0001_init.sql` com `psql -v ON_ERROR_STOP=1` mais `Sql/spike_extras.sql`, e **remove processo e diretório** ao final (verificado: nenhum resíduo).
- Binários: `/usr/lib/postgresql/16/bin` (`NINA_PG_BIN`). O PostgreSQL recusa rodar como root; como root o harness usa `runuser -u claude` (`NINA_PG_USER`).
- O servidor do spike conecta como `spike_app` (membro de `nina_app`, **não superuser**, sujeito a RLS) e define `nina.user_id`/`nina.device_id` por transação (`set_config(..., true)`). O superuser é usado só pelo harness (montar cenário, envelhecer o `change_log`, inspecionar).

## 3. O que foi construído

| Arquivo | Papel |
|---|---|
| `Server/SyncService.Push.cs` | Push idempotente: grupos por bebê em ordem de `baby_id`, 1 transação por grupo, `SAVEPOINT` por mutação, lock consultivo por `mutation_id`, LWW por campo, auditoria, sono sobreposto/timer aberto, WakeEvent. |
| `Server/SyncService.Pull.cs` | Pull em `REPEATABLE READ`: delta por `sync_sequence`, snapshot paginado por keyset (ponto de consistência `H` no cursor), tombstones sem conteúdo. |
| `Server/CursorCodec.cs` | Cursor assinado (mapa bebê -> sequência). |
| `Server/EntityCatalog.cs` | Campos de cada entidade (tipos, imutáveis, obrigatórios na criação) e ordem do snapshot. |
| `Sql/spike_extras.sql` | **Não faz parte da migração.** `nina_spike.field_clock` (relógio por campo), `nina_spike.sync_head()` (leitura de `baby_sync_head`), logins `spike_*`. |
| `Sql/spike_tuning.sql` | **Não faz parte da migração.** Políticas RLS por conjunto + índices `(baby_id, id)`; aplicado só na fase 2 do bench e na suíte com `NINA_SPIKE_TUNED=1`. |
| `Harness/FakeDevice.cs` | Dispositivo simulado: banco local, fila offline, relógio próprio, cursor, perda de resposta, resync do contrato (preserva a fila). |
| `Harness/SpikeEnv.cs`, `TempPostgres.cs` | PostgreSQL temporário e operações administrativas (revogar, envelhecer + purgar, flag de sobreposição via `nina_config_admin`). |
| `Bench.cs` / `Program.cs` | Medições (seção 6). |

Fora do escopo do spike (não implementado): HTTP/autenticação, quotas diárias (`QUOTA_EXCEEDED`/`ENTITY_QUOTA_EXCEEDED`), `BABY.my_role`, `SLEEP_PREDICTION` no feed, formato final das entidades do contrato (o spike devolve `to_jsonb` da linha sem `created_by`/`last_modified_by`).

## 4. Resultados dos cenários

Saída real: `Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46, Duration: 15 s` (Release, `Build succeeded. 0 Warning(s) 0 Error(s)`); com `NINA_SPIKE_TUNED=1`: 46/46.

### 4.1 Cenários pedidos

| Cenário | Resultado observado | Teste |
|---|---|---|
| Dois dispositivos/cuidadores editando offline | Offline não toca o servidor; ao reconectar, 6 eventos + `BABY` convergem nos dois aparelhos e no servidor; `version == sync_sequence`; `device_id`/`actor_user_id` gravados no `change_log` a partir do contexto. | `Two_caregivers_edit_offline_and_converge` |
| Conflito: LWW por campo + auditoria | Campos disjuntos combinam (`MERGED`, sem `conflicts`); mesmo campo: `conflicts[{notes, CLIENT}]` + `LWW_CLIENT_WON`/`entity` canônica; `audit_event sync.conflict_resolved` com **só nomes de campos** (valor de `notes` nunca aparece). Escrita anterior do **mesmo dispositivo** com `base_version` velho não é conflito. Detalhes da ordem na seção 5.2. | `Conflict_*`, `Disjoint_fields_*`, `Same_device_*` |
| Idempotência com retry | Resposta perdida: reenvio devolve `DUPLICATE` com a `version` original, nenhuma sequência nova. 12 pushes simultâneos idênticos: 1 `APPLIED` + 11 `DUPLICATE`. Rejeições determinísticas são gravadas (`outcome=REJECTED`, `reject_code`) e repetem a mesma resposta. `mutation_id` reutilizado para outra mutação: `MUTATION_ID_REUSE`. | `Retry_after_lost_response_*`, `Concurrent_identical_pushes_*`, `Rejected_mutations_*`, `Mutation_id_reused_*` |
| Tombstone no feed | Delta de outro aparelho traz `TOMBSTONE` com `entity=null`, `version`, `deleted_at`; o JSON serializado não contém o texto da nota; no banco `notes IS NULL` e `tombstone.expires_at = deleted_at + 90 dias`. | `Delete_becomes_tombstone_in_feed_without_content` |
| Ressurreição bloqueada | Update/delete atrasado → `REJECTED ENTITY_DELETED` (`retryable=false`, `sync_mutation.outcome=IGNORED_TOMBSTONE`); recriar com o mesmo id também; `UPDATE ... SET deleted_at = NULL` direto como `nina_app` → `NN002`. | `Resurrection_is_blocked_*` |
| Cursor expirado (> 90 dias) | Cursor de 91 dias → `410 SYNC_CURSOR_EXPIRED reason=EXPIRED resync_required=true`; 89 dias ainda é delta. Após purga (`cursor < purged_through`) idem; o aparelho **em dia** (cursor = `purged_through`) segue em delta sem resync. No resync a fila é preservada: criação pendente é aplicada e o update de entidade já purgada sai `REJECTED ENTITY_NOT_FOUND`. | `Cursor_older_than_90_days_*`, `Cursor_below_purged_through_*` |
| Cursor inválido | Adulterado, lixo, de outro bebê, de outro usuário, época errada, sequência > `last_sequence` → `410 reason=INVALID`. | `Invalid_cursors_return_410_invalid`, `CursorCodecTests` |
| Revogação de cuidador | Pull → `403 ACCESS_REVOKED`; push → `REJECTED ACCESS_REVOKED` (`retryable=false`, nada gravado, nem em `sync_mutation`); o aparelho apaga cache e fila. Reconvite cria vínculo novo: o cursor antigo vira `INVALID` e o aparelho refaz o snapshot (inclui o que foi criado durante a revogação). `READ_ONLY` lê e recebe `FORBIDDEN_ROLE`; quem não é membro recebe `404 BABY_NOT_FOUND`. | `Revoked_caregiver_*`, `Read_only_member_*` |
| Sono sobreposto aceito + warning | Padrão `ACCEPT_AND_WARN`: `APPLIED`, `resolution=KEPT_BOTH`, `warnings[SLEEP_OVERLAP]` com `related_entity_ids`, `entity` canônica e `audit_event`; adjacente não é sobreposição; update que passa a sobrepor também avisa. | `Overlapping_sleep_is_accepted_*`, `Update_that_creates_overlap_warns` |
| Sono sobreposto rejeitado por flag | `sleep.overlap_policy=REJECT` (alterada via `nina_config_admin`, com `config_change`): `REJECTED SLEEP_OVERLAP`, `retryable=false`, nada gravado e **`last_sequence` inalterado** (o rollback do savepoint desfaz o contador). | `Overlapping_sleep_is_rejected_when_flag_is_reject_*` |
| Segundo timer aberto (INV-02) | Ver R-05: o índice `sleep_one_open_uq` impede "manter as duas abertas". Spike fecha a mais antiga no início da mais nova, `KEPT_BOTH` + `OPEN_SLEEP_EXISTS`; resultado igual em qualquer ordem de chegada; 6 criações concorrentes terminam com **exatamente 1** aberta. A parada do timer pelo usuário vence a heurística do servidor. | `Two_devices_start_timers_*`, `Concurrent_open_timers_*` |
| WakeEvent com tombstone em cascata | Excluir a `SLEEP_SESSION` gera 3 tombstones no feed (sessão + 2 `WAKE_EVENT`), sem conteúdo, 3 versões distintas, 3 linhas em `tombstone`, nenhum despertar vivo; `night_awakenings` passa de 2 para `NULL`. Despertar criado/editado offline contra a sessão excluída → `ENTITY_DELETED`. Correção manual de `INFERRED` preserva `original_*` e marca `MANUAL`. Fora dos limites da sessão → `VALIDATION_FAILED`. | `WakeEvent_syncs_derives_*` |

### 4.2 Outros resultados

- **Contrato 1.0.1 vigente**: o repositório já está em `openapi.yaml` 1.0.1 (SR-013..021), que **muda** a regra do conflito (ordem de chegada ao servidor, `client_created_at` só informativo, `server_received_at` por resultado, `CLIENT_CLOCK_SKEW` com tolerância de 86.400 s e `tolerance_seconds`, `ENTITY_ID_UNAVAILABLE`, `mutation_id` escopado, cotas). O spike segue o 1.0.1 por padrão; o comportamento do ADR-0003 original está em `ConflictOrder.ClientClockClamped`.
- **Isolamento entre bebês/tenants**: `UPDATE`/`DELETE` com `entity_id` de outro bebê, `WAKE_EVENT` com sessão de outro bebê e id inexistente dão o mesmo `ENTITY_NOT_FOUND`; `CREATE` com id de outro bebê/tenant → `ENTITY_ID_UNAVAILABLE`; `mutation_id` de outro tenant é detectado e recusado (R-04). Teste: `Cross_baby_and_cross_tenant_identifiers_*`.
- **Limites de envelope**: 100 mutações OK, 101 e 0 → 400; 256 KiB + 1 → 413 `PAYLOAD_TOO_LARGE`; `device_id` diferente da sessão → 400; `limit` 0 e 501 → 400; pull de 500 OK. Teste: `Envelope_limits_and_validation`.
- **Snapshot paginado com escritas no meio** (450 entidades, páginas de 100, 20 exclusões + 30 criações + 10 updates entre as páginas 2 e 3): snapshot (todas as páginas `SNAPSHOT`) + delta seguinte = estado do servidor, entidade a entidade. O último `next_cursor` do snapshot continua em `DELTA`.
- **Delta colapsa repetições**: 50 updates da mesma entidade = 1 mudança emitida (`limit=10` percorre 5 páginas de linhas do log, 1 mudança útil).
- **Sem deadlock**: 8 workers, 25 lotes cada, mutações para 2 bebês em ordens opostas no pedido → 400 `APPLIED`, 0 `TRANSIENT`, 0 deadlocks no `pg_stat_database`.
- **Modos de transação** (`PerBabyTransaction` × `PerMutationTransaction`): mesmos resultados.
- **Limitação confirmada**: merge por campo pode violar invariante entre campos (A move `end_at` para 30, B move `start_at` para 45 → `[45,30]`); a 2ª edição sai `REJECTED VALIDATION_FAILED` e é perdida (R-12). Teste: `Field_level_merge_can_violate_*`.

## 5. Decisões

### 5.1 Sequência e cursor

1. **Sequência por bebê** (`baby_sync_head`), confirmando database-spec 6.2/[Q2]: o `UPDATE ... last_sequence + 1` segura o lock de linha até o commit; numeração = ordem de commit; rollback (inclusive de savepoint) desfaz o incremento. Consequência medida: o teto por bebê é ~300 mutações/s (3,3 ms de lock por mutação em lote). Uma criança gera dezenas de eventos por dia: folga > 100×.
2. **Cursor = mapa `baby_id -> sequência`** (ADR-0003 pendência "como não ter lacunas": resolvida **sem** horizonte de segurança, porque a sequência é contígua e em ordem de commit).

```
cursor  = base64url(json) "." base64url(HMAC-SHA256(chave, json)[0..16])
json    = {"v":1,"t":<emissão, epoch s>,"b":{"<baby_id hex>":[<seq>,"<época>"]},"s":[<rank>,"<id>"]?}
época   = base64url(SHA-256(caregiver_membership.id)[0..6])
```

   - O contrato é **por bebê** (`GET /sync/pull?baby_id=`): o mapa tem 1 entrada (135 caracteres; 193 com posição de snapshot). O mapa é a forma de crescer para pull multi-bebê sem mudar o contrato (opaco): 2 bebês 210, 3 → 285, 5 → 434 (491 com snapshot), 6 → 509, 8 → 658 (> 512). Acima de 5 bebês usar codificação binária (16 B uuid + varint, ~12 bebês) — não necessário hoje.
   - **`época` invalida cursor na revogação**: enquanto revogado, 403; depois de reconvite o vínculo tem outro `id`, a época muda e o cursor antigo vira `INVALID` (INV-14 sem estado extra no servidor).
   - **Validade**: `INVALID` se MAC/base64 não canônico/versão ≠ 1/bebê ausente do mapa/época diferente/`seq > last_sequence` (backup restaurado, cursor forjado); `EXPIRED` se `now - t > 90 dias` **ou** `seq < purged_through`. A regra por idade dá o comportamento do contrato independentemente da cadência da purga; a regra por sequência é a que garante correção (a purga só remove a linha física quando o `DELETE` já saiu do log). Cada resposta reemite o cursor com `t` novo (renova a janela, inclusive em delta vazio).
   - Chave HMAC: segredo da aplicação (cofre), com `kid` no JSON ao rotacionar (não implementado no spike; `v` permite evoluir o formato — o contrato diz que ele pode mudar sem aviso).
   - Decodificar + validar: ~5 µs.
3. **Snapshot**: ponto de consistência `H = last_sequence` lido no mesmo snapshot `REPEATABLE READ` da 1ª página e carregado no cursor; páginas por keyset por tipo (`(rank, id)`); o fim do snapshot devolve cursor de delta em `H`. Entidades alteradas depois de `H` podem aparecer no snapshot **e** no delta: o cliente aplica por `version` (idempotente).
4. **Delta**: lê até `limit` **linhas do change_log** (`seq > cursor ORDER BY seq`), busca o estado atual das entidades em 1 consulta por tipo e **colapsa** linhas superadas (emite só quando `entity.version == seq`; as demais serão emitidas pela linha mais nova). Consequência: uma página pode trazer menos de `limit` mudanças mesmo com `has_more=true`; clientes não devem inferir fim por tamanho.

### 5.2 Política de conflito

- **Unidade = campo** (nome de coluna do contrato). Conflito só existe quando **outro dispositivo** gravou o campo **depois de `base_version`**; escritas do mesmo dispositivo (fila offline com `base_version` velho) são sucessão causal, `delete` é `DELETE_WINS` só se outro dispositivo mexeu depois de `base_version`.
- Para saber isso o spike usa `nina_spike.field_clock(entity_id, field, version, device_id, ts)`. **O banco atual não permite** (R-01).
- Duas ordens implementadas (`SyncOptions.ConflictOrder`):

| | `ServerArrival` (padrão; **contrato 1.0.1, SR-014**) | `ClientClockClamped` (ADR-0003 original) |
|---|---|---|
| Quem vence no mesmo campo | O que **chega por último** | O de `min(client_created_at, recebimento)` mais novo |
| Depende de relógio do aparelho | Não | Sim, mas limitado: nunca no futuro (fecha o furo do SR-014) |
| Resultado independe da ordem de chegada | Não | **Sim** (testado nas duas ordens) |
| Edição offline antiga vs. edição online mais nova | **A antiga vence** se chegar depois (consequência de produto já aceita no api-spec 3.1) | A mais nova vence |
| Risco | Perda de edições recentes | Relógio atrasado do aparelho perde indevidamente (no pior caso equivale à ordem de chegada) |

  Resolução devolvida: sem conflito `NONE` (ou `MERGED` se outro dispositivo alterou campos disjuntos); campos em conflito todos do cliente `LWW_CLIENT_WON`, todos do servidor `LWW_SERVER_WON` (só `ClientClockClamped`), misto `MERGED` + `conflicts[]`. `entity` canônica volta quando `resolution != NONE`; `audit_event sync.conflict_resolved` registra campos, versões, `resolution`, sem valores.
- **Recomendação para o BE-004**: manter `ServerArrival` por decisão já tomada (contrato 1.0.1), com o campo `conflict_order` isolado em uma classe de estratégia. Registrar para o produto que `ClientClockClamped` **não reabre o SR-014** (o tempo efetivo nunca passa do recebimento, então relógio no futuro não ajuda o atacante) e preserva a intenção de edições offline; exigiria ajustar o texto do contrato (a descrição atual diz "nunca o relógio do cliente").

### 5.3 Lote, transações e limites

| Parâmetro | Valor | Base |
|---|---|---|
| Mutações por push | máx. 100 (contrato); **cliente envia lotes de até 50** | 50 → ~160 ms p50; 100 → ~330 ms; custo por mutação é plano (3,2–3,3 ms) a partir de 10, então lote maior só alonga o lock do bebê |
| Corpo do push | máx. 256 KiB (413) | contrato |
| Transação | **uma por bebê no pedido**, `SAVEPOINT` por mutação; ordem por `baby_id` | ~30% mais rápido que 1 transação por mutação (3,3 × 4,6–4,9 ms/mut. em lotes ≥ 10) e sem deadlock entre bebês |
| Retentativas | `40P01`/`40001`: até 5 tentativas com backoff + jitter; esgotado → `TRANSIENT` (`retryable=true`) | nenhum `TRANSIENT` observado nos testes e no bench |
| Pull | `limit` padrão 200, máx. 500 (contrato) | delta de 500 mudanças: 8,5 ms (ajustado); snapshot: 42 páginas de 500 em 0,41 s (ajustado) |
| Cursor | ≤ 512 caracteres | 135 caracteres usados |
| Retenção | 90 dias (tombstone, `change_log`, `sync_mutation`) | ADR-0003 |
| Ainda a definir | `lock_timeout`/`statement_timeout` (sugestão: 5 s/15 s → `TRANSIENT`), quotas diárias (SR-014) | não medido |

### 5.4 Idempotência

`pg_advisory_xact_lock(hashtextextended(mutation_id))` + `SELECT` em `sync_mutation` em 1 round trip; reenvio concorrente espera o commit do primeiro e vê a linha. Aplicadas e rejeições determinísticas são gravadas; `TRANSIENT`, `FORBIDDEN_ROLE` e `ACCESS_REVOKED` **não** (a RLS de `sync_mutation` não permite e o resultado é determinístico pelo estado atual). Replay de `DUPLICATE`: `version` = a gravada (`result_version`), `entity` = estado atual; `resolution` aproximada a partir de `outcome` (R-09).

### 5.5 Sono e despertares

- Sobreposição: política lida de `app_parameter sleep.overlap_policy` a cada transação (1 consulta no round trip de contexto); `ACCEPT_AND_WARN` usa `nina.sleep_overlaps()` após gravar; `REJECT` deixa o trigger `NN006` recusar.
- Timer aberto duplicado: ver R-05. Criação serializada por bebê com lock consultivo (sem ele, 6 criações concorrentes davam 4 `23505` — achado do spike).
- `WAKE_EVENT`: valida sessão viva do **mesmo bebê** e o intervalo dentro da sessão (na API, como diz o database-spec 5.1); `tz` herdado da sessão (R-06).

## 6. Medições reais

Ambiente: PostgreSQL 16.15 (Ubuntu), 4 CPUs compartilhadas entre cliente .NET 10 e PostgreSQL (loopback), **fsync ligado**, pool de 120 conexões, `spike_app` com RLS. Números da execução final de `bench`; a variação entre execuções chegou a ±40% (ex.: p50 do push de 1 mutação: 4,9 / 5,3 / 7,5 ms em três execuções) — tratar como ordem de grandeza.

### Push (esquema `0001_init.sql` + extras)

| Operação | n | média | p50 | p95 | p99 |
|---|---:|---:|---:|---:|---:|
| `CREATE` de 1 mutação | 400 | 5,09 | 4,86 | 6,47 | 8,66 ms |
| Reenvio idempotente (`DUPLICATE`) | 300 | 1,64 | 1,56 | 2,04 | 2,95 ms |
| `UPDATE` sem conflito | 300 | 5,13 | 4,83 | 6,90 | 7,57 ms |
| `UPDATE` em conflito de campo (LWW + `audit_event`) | 300 | 6,84 | 6,44 | 9,19 | 10,08 ms |

| Modo | Lote | p50 | p95 | ms/mutação | mutações/s |
|---|---:|---:|---:|---:|---:|
| Transação por bebê | 1 | 4,5 ms | 6,6 ms | 4,92 | 202 |
| Transação por bebê | 10 | 32,1 ms | 36,0 ms | 3,26 | 306 |
| Transação por bebê | 50 | 158,6 ms | 170,0 ms | 3,17 | 315 |
| Transação por bebê | 100 | 328,4 ms | 347,2 ms | 3,31 | 302 |
| Transação por mutação | 10 | 44,3 ms | 54,5 ms | 4,57 | 218 |
| Transação por mutação | 100 | 470,4 ms | 573,1 ms | 4,90 | 204 |

Custo no banco de um `INSERT` de evento (triggers de sync, RLS, sem commit): 0,51 ms como `spike_app` × 0,36 ms como superuser. Ou seja, ~85% dos 3,3 ms/mutação do lote estão fora do `INSERT` em si: instruções auxiliares e idas e vindas do servidor (`SAVEPOINT`, lock+busca, carga da linha, INSERT, relógios, `sync_mutation`, `RELEASE`: ~7 round trips). **Estimativa, não medida**: agrupar com `NpgsqlBatch` deve reduzir isso à metade.

### Pull (bebê com ~20 mil eventos)

| Operação | Esquema `0001` | Com `spike_tuning.sql` |
|---|---:|---:|
| Snapshot completo, `limit=500` | 29,37 s (41 páginas, p50 719 ms/pág.) | **0,41 s** (42 páginas, p50 9,0 ms/pág.) |
| Delta vazio | 1,39 ms p50 | 1,72 ms p50 |
| Delta, 10 mudanças | 3,11 ms | 2,72 ms |
| Delta, 200 mudanças | 31,5 ms | 4,74 ms |
| Delta, 500 mudanças | 76,3 ms | 8,52 ms |
| Leitura de 500 linhas (timeline) com RLS | 41,1 ms | 4,82 ms |
| Mesma leitura como superuser (sem RLS) | 4,19 ms | 4,27 ms |
| Push de 1 mutação / lote de 100 (p50) | 4,86 ms / 328 ms | 5,05 ms / 310 ms |

### Throughput sob concorrência (lotes de 10, 6 s por configuração; contíguo em **todas**)

| Cenário | Workers | Mutações/s | Latência/req p50 | p95 | p99 | `TRANSIENT` |
|---|---:|---:|---:|---:|---:|---:|
| Mesmo bebê | 1 | 285 | 33 ms | 46 ms | 49 ms | 0 |
| Mesmo bebê | 4 | 280 | 103 ms | 311 ms | 674 ms | 0 |
| Mesmo bebê | 16 | 255 | 435 ms | 1.779 ms | 2.327 ms | 0 |
| Bebê por worker | 1 | 271 | 36 ms | 46 ms | 51 ms | 0 |
| Bebê por worker | 4 | 749 | 52 ms | 73 ms | 81 ms | 0 |
| Bebê por worker | 16 | 1.157 | 136 ms | 173 ms | 213 ms | 0 |

(Fase 2, ajustada: bebê por worker 16 → 1.278 mutações/s; mesmo bebê 16 → 280.) O teto por bebê é a serialização do contador: mais dispositivos do **mesmo** bebê só aumentam a espera. Com 2–3 cuidadores empurrando de vez em quando, a contenção é irrelevante.

### Armazenamento por linha (fim da fase 1)

| Tabela | Bytes/linha (tabela+índices) |
|---|---:|
| `change_log` | 301 |
| `sync_mutation` | 254 |
| `field_clock` (spike) | 207 por campo → ~0,8 KB por entidade (4 campos) |
| `diaper_event` | 218 |

Cada mutação aceita custa ~1,4 KB de metadados com `field_clock` em linhas (301 + 254 + 4 × 207 B); R-01 propõe coluna `jsonb` esparsa (~0,6 KB, só `change_log` + `sync_mutation`). `change_log` + `sync_mutation` são podados em 90 dias.

## 7. Falhas e limitações encontradas

Corrigidas dentro do spike (valem como requisitos do BE-004):

1. **Corrida de timers abertos**: a pré-checagem "existe sessão aberta?" não trava nada quando não existe; 6 criações concorrentes → 4 violações de `sleep_one_open_uq` mesmo com 1 retentativa. Corrigido com lock consultivo por bebê (+ até 2 retentativas).
2. **Relógio por campo monotônico**: usar `GREATEST(ts)` evita regredir; mas o fechamento automático do servidor precisa **sobrescrever** o `ts` com o mínimo (epoch), senão a heurística vence a parada real do timer.
3. **Cursor com base64 não canônico**: o último caractere de 16 bytes tem 2 bits sem significado; trocá-lo não mudava os bytes e passava no MAC. Agora exige re-encode idêntico.
4. **`mutation_id` colide com linha invisível pela RLS**: `INSERT ... ON CONFLICT DO NOTHING` retorna 0 linhas sem erro e o efeito seria aplicado **sem registro** (reenvio duplicaria). O spike checa `ExecuteNonQuery() == 0` e recusa (`MUTATION_ID_REUSE`).
5. **`baby_sync_head` ilegível para `nina_app`** (sem `GRANT`): sem `last_sequence` e `purged_through` não há snapshot nem expiração. Spike usa função definer.
6. Fila offline do **mesmo dispositivo** com `base_version` velho virava "conflito consigo mesmo"; tratado como sucessão causal.

Limitações que permanecem:

- **Merge por campo viola invariantes entre campos** (`end_at >= start_at`): a 2ª edição é perdida (`VALIDATION_FAILED`). Mitigação sugerida no BE-004: unidade LWW = grupo (`{start_at, end_at}`), e em caso de violação devolver `LWW_SERVER_WON` com a `entity` canônica em vez de `REJECTED`.
- `ServerArrival` não é determinístico em relação à ordem de chegada; `LWW_SERVER_WON` não ocorre nessa ordem (o contrato descreve um caso que não existe).
- Mutação com relógio muito atrasado fica só com aviso `CLIENT_CLOCK_SKEW`; sem efeito na ordem em `ServerArrival`.
- Idempotência vale por 90 dias (`sync_mutation` é podada junto): um reenvio tardio de mutação antiga seria reaplicado como nova (para `CREATE` vira `ENTITY_ID_UNAVAILABLE`; `UPDATE` repete efeito já visto). Dispositivo > 90 dias offline passa por resync de qualquer modo.
- Um bebê parado > 90 dias perde todo o `change_log`; quem estava em dia continua válido pela regra de sequência, mas é forçado ao resync pela regra de idade (comportamento do contrato).
- Updates com valores idênticos ainda geram versão e linha de log (não otimizado).
- Pull reflete o estado **atual** (`change_log` não tem conteúdo): uma entidade alterada várias vezes chega uma vez, com a versão final — suficiente para o cliente, mas não há histórico de campos.
- Não coberto: HTTP/auth, cotas, `PgBouncer` em modo transação (`set_config(...,true)` e locks `xact` são compatíveis; não testado), réplicas de leitura, PostgreSQL 15, carga prolongada/autovacuum, restore de backup.

## 8. Divergências em relação ao contrato/banco (recomendações; **nada foi alterado**)

Prioridade: A = bloqueia o BE-004 ou o desempenho; M = deve ser decidido antes do BE-004 terminar; B = esclarecimento/higiene.

| ID | P | Divergência / achado | Recomendação | Dono sugerido |
|---|---|---|---|---|
| R-01 | A | O banco só tem `version` por entidade; não dá para saber **qual campo** mudou depois de `base_version`. LWW por campo é inviável sem isso. | Coluna `field_versions jsonb NOT NULL DEFAULT '{}'` nas 6 entidades mutáveis, preenchida pela API só com campos alterados **depois da criação** (`{campo: [version, device_id]}`; chave ausente = escrito na criação = sem conflito). Substitui a tabela do spike (~0,8 KB/entidade) por ~0 em entidades não editadas; some junto com a purga. | database-engineer |
| R-02 | A | `nina_app` não lê `baby_sync_head` (seção 11 da migração). | Função `SECURITY DEFINER` `sync_head(baby)` (como em `spike_extras.sql`, checa `can_read_baby`) ou `GRANT SELECT` + RLS por membro. | database-engineer |
| R-03 | A | Políticas RLS usam `can_read_baby(baby_id)`/`can_write_baby(baby_id)` **por linha** (função com argumento de coluna não é içada). Snapshot de 20 mil eventos 29,4 s; timeline de 500 linhas 41 ms contra 4,2 ms sem RLS; delta de 500 mudanças 76 ms. Também afeta escrita (política de INSERT/UPDATE). | Reescrever como `baby_id IN (SELECT readable_babies())` / `writable_babies()` (função definer que devolve os bebês do `nina.user_id`, avaliada 1× por comando) + índices parciais `(baby_id, id) WHERE deleted_at IS NULL` nas entidades sincronizáveis. Resultados em 6; semântica idêntica (46/46 testes nos dois modos). Vale também para `change_log`, `tombstone`, `sync_mutation`. | database-engineer |
| R-04 | A | PKs globais: `sleep_session.id` etc. e `sync_mutation.mutation_id`. Contrato 1.0.1 exige `mutation_id` escopado por usuário/dispositivo e `ENTITY_ID_UNAVAILABLE`; com PK global um tenant "queima" o `mutation_id` de outro e o id de uma entidade denuncia a existência em outro tenant (SR-014). O spike só consegue **detectar** (limitação 4). | `sync_mutation` com PK `(baby_id, mutation_id)` (ou `(user_id, device_id, mutation_id)`); entidades com PK `(baby_id, id)` (a FK composta de `wake_event` já usa `(id, baby_id)`). Sem isso o escopo do contrato não é implementável. | database-engineer |
| R-05 | M | `sleep_one_open_uq` (INV-02) **contradiz** `ACCEPT_AND_WARN`/`KEPT_BOTH` para "segundo timer aberto" (contrato e api-spec 3.1). Spike: fecha a mais antiga no início da mais nova (heurística; lossy se os dois aparelhos observavam o mesmo cochilo). | Decisão de produto + banco: (a) manter índice + heurística (implementada); (b) **remover o índice** e permitir várias abertas com `OPEN_SLEEP_EXISTS` (fiel ao contrato; o guard de sobreposição já trata aberta como infinito); (c) rejeitar. Preferência: (b). | product + database |
| R-06 | M | `wake_event` aceita `INSERT` apontando para sessão **com tombstone** (a FK só exige a linha). Teste `Direct_database_insert_of_wake_event_*` prova. A API bloqueia (`ENTITY_DELETED`). | Trigger `BEFORE INSERT/UPDATE` em `wake_event` exigindo sessão com `deleted_at IS NULL` (última barreira). | database-engineer |
| R-07 | M | `wake_event.tz NOT NULL` × `WakeEventData` sem `tz`. | Contrato: `tz` opcional (servidor herda da sessão — implementado) **ou** default por trigger no banco. | api-contract + database |
| R-08 | B | `sleep_overlap_guard` monta `tstzrange(start, end)` e lança `22000` para intervalo invertido **antes** do CHECK `sleep_interval_ck` (`23514`), mesmo em `ACCEPT_AND_WARN`. O erro perde o nome do campo/constraint. | No guard, `RETURN NEW` quando `end_at < start_at` e deixar o CHECK falhar. | database-engineer |
| R-09 | M | `sync_mutation` não guarda `resolution`/`conflicts`/`warnings`; o `DUPLICATE` não consegue reproduzir a resposta original (aproxima por `outcome`). Rejeição repetida volta `REJECTED`, não `DUPLICATE`, embora o contrato defina `DUPLICATE` como "já aplicada". | Coluna `resolution text` (e opcional `result jsonb` ≤ 1 KB) em `sync_mutation`; esclarecer no contrato o status do replay de rejeição. | database + api-contract |
| R-10 | M | Contrato × DB (SR-021): `method_or_place` 80 × 60 (70 caracteres passam no contrato e dão `VALIDATION_FAILED`); `notes` 500 × 2000 (o servidor aceitou 900); `volume_ml` inteiro × `numeric(6,1)`; `BottleFeedingData` exige `volume_ml`, o banco não; `PumpingData` sem `notes` e a coluna existe. As entidades do contrato (`Event`, `WakeEvent`: `event_type`, `created_by {id, display_name}`, `duration_seconds`…) **não são** a linha da tabela. | Alinhar limites (decidir qual lado vale) e validar o contrato na API antes do banco; BE-004 precisa de mapeadores linha → DTO (o spike devolve `to_jsonb(linha)` sem autoria). | api-contract + database |
| R-11 | M | Feed: `SLEEP_PREFERENCES` (contrato) × `SLEEP_SCHEDULE_PREFERENCE` (banco/`change_log`); `SLEEP_PREDICTION` está no feed do contrato, mas é derivada e **não** existe no `change_log`; `BABY.my_role` é por usuário. | Mapear nomes na API; decidir se a previsão entra no pull como entidade sintética (versão = `last_sequence`) ou sai do feed. | api-contract |
| R-12 | M | LWW por campo pode produzir combinação inválida (limitação 1) e a edição é perdida. | Grupos atômicos de campos (`{start_at, end_at}`, `{started_at, ended_at}`) como unidade LWW; em violação, devolver `LWW_SERVER_WON` + `entity` em vez de `REJECTED`. | dotnet-backend + api-contract |
| R-13 | B | `Resolution`: mistura de campos ganhos/perdidos não tem valor; em `ServerArrival` `LWW_SERVER_WON` nunca ocorre; `KEPT_BOTH` também serve ao auto-fechamento de timer. | Esclarecer a semântica (spike: misto = `MERGED` + `conflicts[]`). | api-contract |
| R-14 | B | `CREATE` repetido (mesmo `entity_id`, outro `mutation_id`, mesmo usuário e bebê) hoje dá `ENTITY_ID_UNAVAILABLE`; o contrato só fala em "outro bebê/usuário". | Definir se é idempotente (devolve o estado quando o payload é igual). | api-contract |
| R-15 | M | Contrato 1.0.1 fala em cotas (`QUOTA_EXCEEDED`, `ENTITY_QUOTA_EXCEEDED`) sem mecanismo no banco; contar com `count(*)` por push é caro em bebê grande. | Contador `baby_quota(baby_id, day, mutations, live_entities)` atualizado na mesma transação (UPSERT) — não medido. | database + dotnet-backend |
| R-16 | B | `limit` do pull conta linhas do `change_log`, não mudanças emitidas (colapso). `database-spec` 6.2 "Teste de validação" pode citar este spike e fechar [Q2] (cursor por bebê, sequência por bebê). | Documentar no api-spec 3.2; fechar [Q2] como "sim". | architect |
| R-17 | B | `tombstone` é redundante para o pull (a linha da entidade guarda `deleted_at` até a purga); serve à purga/expiração. | Manter; documentar que o feed lê `deleted_at` da entidade. | — |

## 9. Desenho recomendado para o BE-004

### 9.1 Estrutura (monolito modular, módulo `Nina.Tracking`)

```
Nina.Tracking/Sync/
  SyncEndpoints          POST /sync/push, GET /sync/pull (authN + papel + rate limit; ProblemDetails)
  PushHandler            envelope (≤100, ≤256 KiB, device_id da sessão) -> grupos por bebê (ordem de baby_id)
  PullHandler            REPEATABLE READ; delta / snapshot
  CursorService          codec assinado (mapa + época + kid), erros EXPIRED/INVALID
  ConflictResolver       IConflictOrder { ServerArrival (padrão), ClientClockClamped } + grupos atômicos de campos
  SyncRepository         Npgsql, papel nina_app; set_config('nina.user_id'/'nina.device_id', true)
  EntityMappers          linha <-> DTO do contrato (autoria, event_type, my_role)
  SyncMetrics/Audit      métricas e audit_event sync.conflict_resolved (só nomes de campos)
```

### 9.2 Migração `0002_sync.sql` (propostas R-01..R-04, R-06, R-08, R-09, R-15)

`field_versions jsonb` nas entidades mutáveis; `sync_head(baby)`; políticas por conjunto + índices `(baby_id, id)`; PK `(baby_id, mutation_id)` (+ `resolution`); trigger de sessão viva em `wake_event`; ajuste do guard de sobreposição; (se aprovado) remover `sleep_one_open_uq`; contadores de cota. O runner deve aplicar com o papel dono, nunca `nina_app`.

### 9.3 Push (por grupo de bebê, uma transação `READ COMMITTED`)

1. `set_config` do usuário/dispositivo + vínculo + política de sobreposição em 1 round trip (`NpgsqlBatch`). Sem vínculo ativo: `ACCESS_REVOKED` (vínculo `REVOKED`), `BABY_NOT_FOUND` (nenhum), `FORBIDDEN_ROLE` (`READ_ONLY`); nada gravado.
2. Por mutação: `SAVEPOINT`; lock consultivo por `mutation_id` + busca em `sync_mutation` (1 round trip) → se achou: `DUPLICATE` (ou a rejeição gravada); senão carregar a linha `FOR UPDATE`, validar (tipos, imutáveis, `entity_id`↔`baby_id`, sessão do `WAKE_EVENT`), resolver conflito por campo, `INSERT`/`UPDATE` (a versão vem do trigger), atualizar `field_versions`, calcular avisos (`sleep_overlaps`, `CLIENT_CLOCK_SKEW`), gravar `sync_mutation` **verificando linhas afetadas**, `audit_event` se `resolution != NONE`; `RELEASE`. Erro de banco ⇒ `ROLLBACK TO SAVEPOINT` + mapeamento (`NN002` `ENTITY_DELETED`, `NN006` `SLEEP_OVERLAP`, `23505 *_pkey` `ENTITY_ID_UNAVAILABLE`, `22*/23514` `VALIDATION_FAILED`, `42501` `FORBIDDEN_ROLE`) + gravar a rejeição determinística.
3. `COMMIT`; `40P01`/`40001` ⇒ repetir o grupo (até 5 tentativas); esgotado ⇒ `TRANSIENT`.
4. Resposta na ordem original, com `server_received_at`, `version`, `resolution`, `conflicts`, `warnings` e `entity` (quando `resolution != NONE` ou `DUPLICATE`).

### 9.4 Pull

1. `REPEATABLE READ`; vínculo: `REVOKED` ⇒ `403 ACCESS_REVOKED`, ausente ⇒ `404`; `sync_head`.
2. Cursor: decodificar (`INVALID`), idade/purga (`EXPIRED`), `época` e `seq <= last_sequence`.
3. Sem cursor: snapshot por keyset (ranks do feed) com `H` no cursor; com cursor: `change_log` `seq > c ORDER BY seq LIMIT n+1`, estado atual por tipo, colapso por `version == seq`, tombstone via `deleted_at`.
4. `next_cursor` reemitido a cada resposta (renova `t`).

### 9.5 Testes e operação

- Portar a suíte do spike (46 testes) para o projeto de testes do módulo **rodando como `nina_app`** (SR-018) contra PostgreSQL real (Testcontainers/CI); manter os de contiguidade (escritores + leitor ao vivo), idempotência concorrente, cursores e revogação.
- `pgbench`/bench como gate de regressão de desempenho (snapshot de 20 mil < 1 s, delta de 500 < 20 ms).
- Métricas: latência por endpoint e tamanho de lote, rejeições por `problem.code`, conflitos por `resolution`, `410` por `reason`, resyncs completos, tentativas por deadlock, espera de lock por bebê, tamanho de `change_log`. Logs sem corpo de requisição (C3).
- Parâmetros em `app_parameter` (`sync.*`, `limits.*`); segredo do cursor no cofre, com `kid`.

### 9.6 Decisões que dependem de produto/arquitetura

1. R-05: manter ou remover `sleep_one_open_uq` (e, mantendo, aceitar a heurística de auto-fechamento).
2. Ordem do conflito: manter `ServerArrival` (contrato) ou adotar `ClientClockClamped` (exige ajustar o texto do contrato).
3. R-12: grupos atômicos de campos e `LWW_SERVER_WON` em violação de invariante.
4. R-11: previsão de sono no feed do pull.
5. R-04: custo/benefício de mudar PKs agora (nada em produção).
