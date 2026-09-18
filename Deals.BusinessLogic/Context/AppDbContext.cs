using Deals.BusinessLogic.Interfaces;
using Deals.Models.Entities;
using Deals.Models.Models;
using Microsoft.EntityFrameworkCore;

namespace Deals.BusinessLogic.Context;

public class AppDbContext : DbContext
{
    private readonly ICurrentUserService _currentUser;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUserService currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<UserSession> UserSessions { get; set; } = null!;
    public DbSet<SteamGame> SteamGames { get; set; } = null!;
    public DbSet<SteamPriceObservation> SteamPriceObservations { get; set; } = null!;
    public DbSet<GameOffer> GameOffers { get; set; } = null!;
    public DbSet<ExternalBundle> ExternalBundles { get; set; } = null!;
    public DbSet<ExternalBundleGame> ExternalBundleGames { get; set; } = null!;
    public DbSet<FxRate> FxRates { get; set; } = null!;

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditInfo();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAuditInfo();
        return base.SaveChanges();
    }

    private void ApplyAuditInfo()
    {
        var now = DateTime.UtcNow;
        var userName = _currentUser.GetName();

        foreach (var entry in ChangeTracker.Entries<BaseModel>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.Created = now;
                entry.Entity.CreatedBy = userName;
                continue;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(BaseModel.Created)).IsModified = false;
                entry.Property(nameof(BaseModel.CreatedBy)).IsModified = false;
                entry.Entity.Updated = now;
                entry.Entity.UpdatedBy = userName;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasMany(e => e.Sessions).WithOne(e => e.User).HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.RefreshTokenHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<SteamGame>(entity =>
        {
            entity.HasIndex(e => new { e.AppId, e.Region }).IsUnique();
            entity.HasMany(e => e.PriceObservations)
                .WithOne(e => e.SteamGame)
                .HasForeignKey(e => e.SteamGameId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Offers)
                .WithOne(e => e.SteamGame)
                .HasForeignKey(e => e.SteamGameId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.BundleLinks)
                .WithOne(e => e.SteamGame)
                .HasForeignKey(e => e.SteamGameId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalBundle>(entity =>
        {
            entity.HasIndex(e => new { e.Source, e.BundleKey }).IsUnique();
            entity.HasMany(e => e.Links)
                .WithOne(e => e.Bundle)
                .HasForeignKey(e => e.ExternalBundleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalBundleGame>(entity =>
        {
            entity.HasIndex(e => new { e.ExternalBundleId, e.SteamGameId }).IsUnique();
            entity.HasIndex(e => new { e.SteamGameId, e.Source });
        });

        modelBuilder.Entity<GameOffer>(entity =>
        {
            entity.HasIndex(e => new { e.SteamGameId, e.Source, e.OfferKey }).IsUnique();
            entity.HasIndex(e => e.SteamGameId);
        });

        modelBuilder.Entity<SteamPriceObservation>(entity =>
        {
            entity.HasIndex(e => new
            {
                e.SteamGameId,
                e.Currency,
                e.InitialPriceMinor,
                e.CurrentPriceMinor,
                e.DiscountPercent
            });
            entity.HasIndex(e => e.ObservedAt);
        });

        modelBuilder.Entity<FxRate>(entity =>
        {
            entity.HasKey(e => new { e.Base, e.Quote, e.RateDate });
        });

    }
}
