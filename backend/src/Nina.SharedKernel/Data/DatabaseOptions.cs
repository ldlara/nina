namespace Nina.SharedKernel.Data;

/// <summary>Configuração do banco (seção <c>Database</c> e <c>ConnectionStrings</c>).</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Quando verdadeiro, aplica as migrações pendentes na inicialização (usa <c>ConnectionStrings:Migrations</c>).</summary>
    public bool ApplyMigrations { get; set; }

    /// <summary>Diretório com os arquivos <c>NNNN_*.sql</c> (por padrão <c>backend/db/migrations</c> no repositório).</summary>
    public string? MigrationsPath { get; set; }
}
