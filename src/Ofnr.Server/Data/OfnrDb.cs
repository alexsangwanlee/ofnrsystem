using Microsoft.EntityFrameworkCore;

namespace Ofnr.Server.Data;

public sealed class OfnrDb(DbContextOptions<OfnrDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<MailMessage> MailMessages => Set<MailMessage>();
    public DbSet<MailDraft> MailDrafts => Set<MailDraft>();
    public DbSet<MailSyncState> MailSyncStates => Set<MailSyncState>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<Activity> Activities => Set<Activity>();

    protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseSnakeCaseNamingConvention();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<ProjectStage>().HaveConversion<string>();
        c.Properties<ProjectStatus>().HaveConversion<string>();
        c.Properties<MailStatus>().HaveConversion<string>();
        c.Properties<JobStatus>().HaveConversion<string>();
        c.Properties<RunStatus>().HaveConversion<string>();
        c.Properties<ArtifactStatus>().HaveConversion<string>();
        c.Properties<ApprovalStatus>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>().HasIndex(p => p.JobNo).IsUnique();
        b.Entity<MailMessage>().HasIndex(m => new { m.Folder, m.UidValidity, m.Uid }).IsUnique();
        b.Entity<MailMessage>().ComplexCollection(m => m.Attachments, a => a.ToJson());
        b.Entity<MailSyncState>().HasKey(s => s.Folder);
        b.Entity<Job>().ComplexProperty(j => j.Result, r => r.ToJson());
        b.Entity<JobRun>().HasIndex(r => new { r.JobId, r.Seq }).IsUnique();
        b.Entity<JobEvent>().HasIndex(e => new { e.RunId, e.Seq }).IsUnique();
    }
}
