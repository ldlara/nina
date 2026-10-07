# ADR-0001 — Stack
Status: aceita (decisão do usuário).
- iOS: Swift/SwiftUI. Android: Kotlin/Jetpack Compose.
- Backend: .NET (ASP.NET Core) com BFF dedicado aos clientes móveis.
- Banco: PostgreSQL.
Consequências: dois clientes nativos exigem contratos OpenAPI estáveis antes do trabalho paralelo; BFF concentra DTOs e sync.
