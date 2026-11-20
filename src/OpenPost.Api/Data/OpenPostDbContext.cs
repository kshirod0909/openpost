using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenPost.Api.Data.Entities;

namespace OpenPost.Api.Data;

public sealed class OpenPostDbContext : IdentityDbContext<AppUser, AppRole, Guid>
{
    public OpenPostDbContext(DbContextOptions<OpenPostDbContext> options) : base(options) { }

    public DbSet<MailDomain> Domains => Set<MailDomain>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<MailAlias> Aliases => Set<MailAlias>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<StoredMessage> Messages => Set<StoredMessage>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<OutboundMessage> Outbound => Set<OutboundMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<InstanceSetting> Settings => Set<InstanceSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.Signature).HasMaxLength(2000);
            e.HasIndex(x => x.NormalizedEmail);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(88).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.ExpiresAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MailDomain>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(253).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.DkimSelector).HasMaxLength(63);
            e.Property(x => x.DmarcPolicy).HasMaxLength(16);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Mailbox>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LocalPart).HasMaxLength(64).IsRequired();
            e.Property(x => x.Address).HasMaxLength(320).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.HasIndex(x => x.Address).IsUnique();
            e.HasIndex(x => x.DomainId);
            e.HasOne(x => x.Domain).WithMany(d => d.Mailboxes)
                .HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.User).WithMany(u => u.Mailboxes)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MailAlias>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.LocalPart).HasMaxLength(64).IsRequired();
            e.Property(x => x.Address).HasMaxLength(320).IsRequired();
            e.Property(x => x.TargetsJson).HasColumnType("jsonb");
            e.HasIndex(x => x.Address).IsUnique();
        });

        b.Entity<Folder>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.MailboxId, x.Name }).IsUnique();
            e.HasOne(x => x.Mailbox).WithMany(m => m.Folders)
                .HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Label>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(40).IsRequired();
            e.Property(x => x.Color).HasMaxLength(9);
            e.HasIndex(x => new { x.MailboxId, x.Name }).IsUnique();
            e.HasOne(x => x.Mailbox).WithMany(m => m.Labels)
                .HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StoredMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.FromAddress).HasMaxLength(320);
            e.Property(x => x.FromName).HasMaxLength(200);
            e.Property(x => x.RfcMessageId).HasMaxLength(400);
            e.Property(x => x.ThreadId).HasMaxLength(400);
            e.Property(x => x.InReplyTo).HasMaxLength(400);
            e.Property(x => x.References).HasMaxLength(4000);
            e.Property(x => x.Preview).HasMaxLength(400);
            e.Property(x => x.ToJson).HasColumnType("jsonb");
            e.Property(x => x.CcJson).HasColumnType("jsonb");
            e.Property(x => x.BccJson).HasColumnType("jsonb");
            e.Property(x => x.ReplyToJson).HasColumnType("jsonb");
            e.Property(x => x.LabelsJson).HasColumnType("jsonb");
            e.Property(x => x.AuthResultsJson).HasColumnType("jsonb");
            e.Property(x => x.SpamVerdict).HasConversion<string>().HasMaxLength(16);

            e.HasIndex(x => new { x.MailboxId, x.FolderId, x.ReceivedAt });
            e.HasIndex(x => new { x.MailboxId, x.Uid }).IsUnique();
            e.HasIndex(x => new { x.MailboxId, x.ThreadId });
            e.HasIndex(x => new { x.MailboxId, x.IsDraft });

            e.HasOne(x => x.Mailbox).WithMany()
                .HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Folder).WithMany()
                .HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Attachment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(160);
            e.Property(x => x.StoragePath).HasMaxLength(400).IsRequired();
            e.Property(x => x.ContentId).HasMaxLength(260);
            e.HasIndex(x => x.MessageId);
            e.HasIndex(x => new { x.MailboxId, x.MessageId });
            e.HasOne(x => x.Message).WithMany(m => m.Attachments)
                .HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OutboundMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.EnvelopeFrom).HasMaxLength(320).IsRequired();
            e.Property(x => x.EnvelopeTo).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.MimePath).HasMaxLength(400).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(80).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(40);
            e.Property(x => x.TargetId).HasMaxLength(80);
            e.Property(x => x.ActorAddress).HasMaxLength(320);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.DetailJson).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Action, x.CreatedAt });
        });

        b.Entity<InstanceSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(80);
            e.Property(x => x.Value).HasMaxLength(4000);
        });
    }
}
