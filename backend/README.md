# Backend (.NET)
Solução `Nina.sln` com `src/Nina.Api` (monólito modular) e `src/Nina.Bff`. O scaffold será criado na Onda 2 (CLOUD-001/ARCH-001) dentro do container:

    docker compose run --rm dotnet dotnet new sln -n Nina

Subir o ambiente: `cp .env.example .env && docker compose up --build`.
