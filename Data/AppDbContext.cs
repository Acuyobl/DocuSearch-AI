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
    public DbSet<AgentTrace> AgentTraces => Set<AgentTrace>();

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
    }
}
