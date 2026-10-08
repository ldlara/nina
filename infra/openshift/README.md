# Manifests OpenShift (ADR-0006)

Aplicar por servico: `oc apply -f infra/openshift/` no namespace alvo.

- Imagens: `REGISTRY/nina-api:TAG` e `REGISTRY/nina-bff:TAG` (placeholders; registry e pendencia do ADR-0006).
- Seguranca: `runAsNonRoot`, sem `runAsUser` fixo (a SCC `restricted-v2` atribui UID arbitrario), `readOnlyRootFilesystem`, sem escalada de privilegio, capabilities `drop: ALL`, seccomp `RuntimeDefault`.
- Config: `ConfigMap` nina-config (nao sensivel). Segredos: `Secret` nina-secrets e **apenas referenciado** via `secretKeyRef`; crie-o fora do repositorio:

      oc create secret generic nina-secrets \
        --from-literal=ConnectionStrings__Default='Host=...;Database=nina;Username=...;Password=...'

- `examples/secret.example.yaml` e um molde (fora do `apply -f` do diretorio de proposito, para nao sobrescrever o Secret real); nao versione valores reais.
