using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DocumentItem> Documents => Set<DocumentItem>();
    public DbSet<DocumentFile> Files => Set<DocumentFile>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<DepartmentInfo> Departments => Set<DepartmentInfo>();
    public DbSet<AuditEvent> Audit => Set<AuditEvent>();
    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();
    public DbSet<RoutineDefinition> Routines => Set<RoutineDefinition>();
    public DbSet<RoutineRun> RoutineRuns => Set<RoutineRun>();
    public DbSet<AiUsageLedger> AiUsageLedger => Set<AiUsageLedger>();
    public DbSet<AiBudgetPolicy> AiBudgetPolicies => Set<AiBudgetPolicy>();
    public DbSet<AiBudgetIncident> AiBudgetIncidents => Set<AiBudgetIncident>();
    public DbSet<AgentTrace> AgentTraces => Set<AgentTrace>();
    public DbSet<PageEmbedding> PageEmbeddings => Set<PageEmbedding>();
    public DbSet<RagChunkRecord> RagChunks => Set<RagChunkRecord>();
    public DbSet<McpApiToken> McpApiTokens => Set<McpApiToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DepartmentInfo>().ToTable("Departments", table =>
            table.HasCheckConstraint("valid_quota", "[AllocatedStorageMB] > 0"));
        model.Entity<AppUser>().ToTable("Users", table =>
        {
            table.HasCheckConstraint("valid_role", "[Role] IN ('admin_general','admin_depto','gestor_documental','consultor','auditor')");
            table.HasCheckConstraint("valid_user_status", "[Status] IN ('Activo','Inactivo')");
        });
        model.Entity<DocumentItem>().ToTable("Documents", table =>
        {
            table.HasCheckConstraint("valid_level", "[Confidentiality] IN (N'Público',N'Uso Interno',N'Confidencial',N'Estrictamente Secreto')");
            table.HasCheckConstraint("valid_file_size", "[FileSizeBytes] >= 0");
            table.HasCheckConstraint("valid_page_count", "[PageCount] >= 0 AND [PageCount] <= 500");
            table.HasCheckConstraint("valid_pages_json", "ISJSON([PagesJson]) = 1");
            table.HasCheckConstraint("valid_notes_json", "ISJSON([NotesJson]) = 1");
        });
        model.Entity<DepartmentInfo>().HasAlternateKey(d => d.Name);
        model.Entity<DocumentItem>().HasIndex(d => d.InternalCode).IsUnique();
        model.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        model.Entity<DocumentItem>().Property(d => d.PagesJson).HasColumnType("nvarchar(max)");
        model.Entity<DocumentItem>().Property(d => d.NotesJson).HasColumnType("nvarchar(max)");
        model.Entity<DocumentFile>().Property(f => f.Bytes).HasColumnType("varbinary(max)");
        model.Entity<DocumentItem>().HasOne<DepartmentInfo>().WithMany()
            .HasForeignKey(d => d.Department).HasPrincipalKey(d => d.Name).OnDelete(DeleteBehavior.Restrict);
        model.Entity<AppUser>().HasOne<DepartmentInfo>().WithMany()
            .HasForeignKey(u => u.Department).HasPrincipalKey(d => d.Name).OnDelete(DeleteBehavior.Restrict);
        model.Entity<DocumentFile>().HasOne<DocumentItem>().WithOne()
            .HasForeignKey<DocumentFile>(f => f.DocumentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AuditEvent>().HasIndex(a => a.TimestampUTC);
        model.Entity<AgentTrace>().HasIndex(t => t.StartedUtc);
        model.Entity<PageEmbedding>().ToTable("PageEmbeddings");
        model.Entity<PageEmbedding>().HasIndex(e => new { e.DocumentId, e.PageNumber }).IsUnique();
        model.Entity<PageEmbedding>().Property(e => e.Vector).HasColumnType("varbinary(max)");
        model.Entity<PageEmbedding>().HasOne<DocumentItem>().WithMany()
            .HasForeignKey(e => e.DocumentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<RagChunkRecord>().ToTable("RagFragments");
        model.Entity<RagChunkRecord>().HasIndex(chunk => new { chunk.DocumentId, chunk.ChunkIndex }).IsUnique();
        model.Entity<RagChunkRecord>().Property(chunk => chunk.Vector).HasColumnType("varbinary(max)");
        model.Entity<RagChunkRecord>().Property(chunk => chunk.Text).HasColumnType("nvarchar(max)");
        // Al borrar un expediente, SQL Server elimina también su texto troceado.
        model.Entity<RagChunkRecord>().HasOne<DocumentItem>().WithMany()
            .HasForeignKey(chunk => chunk.DocumentId).OnDelete(DeleteBehavior.Cascade);
        // Gobernanza: bitácora con índices por entidad, cola de aprobaciones y rutinas.
        // Bitácora de solo inserción: un trigger de SQL Server rechaza UPDATE y DELETE (migración Governance).
        model.Entity<AuditEvent>().ToTable("Audit", table => table.HasTrigger("TR_Audit_AppendOnly"));
        model.Entity<AuditEvent>().HasIndex(a => new { a.Entity, a.EntityId });
        model.Entity<ApprovalRequest>().ToTable("ApprovalRequests");
        model.Entity<ApprovalRequest>().HasIndex(a => new { a.Status, a.RequestedUtc });
        model.Entity<ApprovalRequest>().HasIndex(a => new { a.Action, a.EntityId, a.Status });
        model.Entity<RoutineDefinition>().ToTable("Routines");
        model.Entity<RoutineRun>().ToTable("RoutineRuns");
        model.Entity<RoutineRun>().HasIndex(r => new { r.RoutineKey, r.StartedUtc });
        // Consumo de IA: ledger por fecha/periodo y hash; un incidente por (periodo, tipo).
        model.Entity<AiUsageLedger>().ToTable("AiUsageLedger");
        model.Entity<AiUsageLedger>().HasIndex(row => row.CreatedUtc);
        model.Entity<AiUsageLedger>().HasIndex(row => new { row.Period, row.Feature });
        model.Entity<AiUsageLedger>().HasIndex(row => row.RequestHash);
        model.Entity<AiBudgetPolicy>().ToTable("AiBudgetPolicies");
        model.Entity<AiBudgetIncident>().ToTable("AiBudgetIncidents");
        model.Entity<AiBudgetIncident>().HasIndex(item => new { item.Period, item.Kind }).IsUnique();
        model.Entity<McpApiToken>().ToTable("McpApiTokens");
        model.Entity<McpApiToken>().HasIndex(token => token.TokenHash).IsUnique();
        model.Entity<McpApiToken>().HasIndex(token => new { token.UserId, token.RevokedUtc });
        model.Entity<McpApiToken>().HasOne<AppUser>().WithMany()
            .HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
