using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Floresta.Data;

public class FlorestaDbContext : DbContext
{
    public FlorestaDbContext(DbContextOptions<FlorestaDbContext> options) : base(options) { }

    public DbSet<TrackedWallet> TrackedWallets { get; set; }
    public DbSet<TrackedAddress> TrackedAddresses { get; set; }
    public DbSet<TrackedUtxo> Utxos { get; set; }
    public DbSet<TrackedTransaction> Transactions { get; set; }
    public DbSet<SyncState> SyncStates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("floresta");

        modelBuilder.Entity<TrackedWallet>(b =>
        {
            b.HasKey(e => e.Id);
            b.ToTable("tracked_wallets");
            b.Property(e => e.DescriptorHash).HasMaxLength(64);
        });

        modelBuilder.Entity<TrackedAddress>(b =>
        {
            b.HasKey(e => e.Scripthash);
            b.ToTable("tracked_addresses");
            b.HasOne(e => e.Wallet).WithMany().HasForeignKey(e => e.WalletId);
            b.HasIndex(e => e.WalletId);
        });

        modelBuilder.Entity<TrackedUtxo>(b =>
        {
            b.HasKey(e => e.Outpoint);
            b.ToTable("utxos");
            b.HasOne(e => e.Wallet).WithMany().HasForeignKey(e => e.WalletId);
            b.HasOne(e => e.TrackedAddress).WithMany().HasForeignKey(e => e.Scripthash);
            b.HasIndex(e => e.WalletId).HasFilter("NOT \"IsSpent\"");
            b.HasIndex(e => e.Scripthash);
        });

        modelBuilder.Entity<TrackedTransaction>(b =>
        {
            b.HasKey(e => new { e.Txid, e.WalletId });
            b.ToTable("transactions");
            b.HasOne(e => e.Wallet).WithMany().HasForeignKey(e => e.WalletId);
            b.HasIndex(e => e.WalletId);
        });

        modelBuilder.Entity<SyncState>(b =>
        {
            b.HasKey(e => e.Key);
            b.ToTable("sync_state");
        });
    }
}
