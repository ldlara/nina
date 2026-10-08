# Manifests OpenShift (ADR-0006)

Aplicar por servico: `oc apply -f infra/openshift/` no namespace alvo (`examples/` fica de fora de proposito).

- Imagens: `REGISTRY/nina-api@sha256:DIGEST` e `REGISTRY/nina-bff@sha256:DIGEST` (placeholders; registry e pendencia do ADR-0006). Implante por digest, `imagePullPolicy: Always`.
- Seguranca de pod: `runAsNonRoot`, sem `runAsUser` fixo (a SCC `restricted-v2` atribui UID arbitrario), `readOnlyRootFilesystem`, sem escalada de privilegio, capabilities `drop: ALL`, seccomp `RuntimeDefault`.
- Rede (`networkpolicy.yaml`): deny-all (ingress+egress) por padrao; permitido apenas Router->BFF, BFF->API, API->PostgreSQL e DNS. **Ajuste o seletor do PostgreSQL** (`app.kubernetes.io/name: postgresql`) ou use `ipBlock` se o banco for gerenciado/externo. Workers/jobs futuros exigem regras proprias.
- Route (`nina-bff.yaml`): TLS edge com redirect, HSTS, rate limit por IP e timeout via annotations haproxy (valores iniciais, ajustar). TLS minimo depende do IngressController (pendencia de plataforma).
- Config: `ConfigMap` nina-config (nao sensivel).
- Segredos: um Secret **por papel de banco** (`nina-db-app`, `nina-db-worker`, `nina-db-config-admin`), apenas **referenciados** via `secretKeyRef`. Moldes em `examples/` (sem valores). Crie-os fora do repositorio, preferencialmente por cofre/External Secrets; evite `--from-literal` (historico de shell):

      oc create secret generic nina-db-app --from-file=ConnectionStrings__Default=./app.conn   # arquivo temporario, apague apos

  A API usa somente `nina-db-app` (papel `nina_app`, sujeito a RLS). Nunca use dono/superusuario na aplicacao.
- Pendencias (decisao do usuario/plataforma): responsavel pela hospedagem (Shadow IT S1), registry, regiao/DPA, backup/KMS, mTLS ou token de servico BFF<->API.
