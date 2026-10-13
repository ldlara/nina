# Spike de sync (ARCH-003) — arquivado

Este spike cumpriu seu objetivo (ver `specs/sync-spike.md`). Após a remediação do banco (SR-001..012, R-04 etc.), o schema mudou (PKs escopadas) e **39 dos 46 testes do spike deixaram de passar** (`42P10`). Ele não faz parte de `Nina.sln` nem do CI. A lógica de push/pull deve ser portada para o módulo de Tracking no BE-004, contra o schema atual.
