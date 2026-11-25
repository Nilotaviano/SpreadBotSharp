using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;

namespace SpreadBot.Infrastructure.Persistence
{
    /// <summary>
    /// The bot's own database: the configurations bots ran with, every bot (its state while active; when and with what profit it
    /// finished), every state of every order a bot placed (append-only), and the dust recorded per market.
    /// </summary>
    internal class SpreadBotDbContext : DbContext
    {
        public SpreadBotDbContext(DbContextOptions<SpreadBotDbContext> options) : base(options) { }

        public DbSet<SpreadConfiguration> Configurations => Set<SpreadConfiguration>();
        public DbSet<BotRow> Bots => Set<BotRow>();
        public DbSet<OrderData> Orders => Set<OrderData>();
        public DbSet<DustRow> Dust => Set<DustRow>();
        public DbSet<EquitySnapshotRow> EquitySnapshots => Set<EquitySnapshotRow>();
        public DbSet<PushSubscriptionRow> PushSubscriptions => Set<PushSubscriptionRow>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            //SQLite stores dates as text without a kind; every date the bot keeps is UTC, and the unknown-order lifetime compares against UtcNow
            configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
            configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();

            //By name, so reordering an enum can't change what a stored row means
            configurationBuilder.Properties<BotState>().HaveConversion<string>();
            configurationBuilder.Properties<BotStrategyType>().HaveConversion<string>();
            configurationBuilder.Properties<OrderStatus>().HaveConversion<string>();
            configurationBuilder.Properties<OrderDirection>().HaveConversion<string>();
            configurationBuilder.Properties<OrderType>().HaveConversion<string>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SpreadConfiguration>(configuration =>
            {
                configuration.ToTable("Configurations");
                configuration.Property<int>("Id");
                configuration.HasKey("Id");
                //One row per configuration, by the fields its equality compares
                configuration.HasIndex(c => new
                {
                    c.Strategy,
                    c.BaseMarket,
                    c.MaxPercentChangeFromPreviousDay,
                    c.MinimumSpreadPercentage,
                    c.MinimumQuoteVolume,
                    c.AllocatedAmountOfBaseCurrency,
                    c.SpreadThresholdBeforeCancelingCurrentOrder,
                    c.MinutesForLoss,
                    c.MinimumPrice,
                    c.MinimumNegotiatedAmount
                }).IsUnique();
            });

            modelBuilder.Entity<BotRow>(bot =>
            {
                bot.ToTable("Bots");
                bot.HasKey(b => b.Id);
                bot.HasOne(b => b.Configuration).WithMany().HasForeignKey(b => b.ConfigurationId).OnDelete(DeleteBehavior.Restrict);
                //The exact stored state the bot holds: the database can't let a bot name an order it doesn't have
                bot.HasOne(b => b.CurrentOrder).WithMany().HasForeignKey(b => b.CurrentOrderRecordId).OnDelete(DeleteBehavior.Restrict);
                bot.HasIndex(b => b.FinishedAt);
                bot.HasIndex(b => b.MarketSymbol);
            });

            modelBuilder.Entity<OrderData>(order =>
            {
                order.ToTable("Orders");
                order.Property<long>("RecordId");
                order.HasKey("RecordId");
                order.Property(o => o.Id).HasColumnName("ExchangeOrderId");
                order.Property<Guid>("BotId");
                order.HasOne<BotRow>().WithMany().HasForeignKey("BotId").OnDelete(DeleteBehavior.Restrict);
                order.Property<DateTime>("RecordedAt");
                order.HasIndex(o => o.ClientOrderId);
                order.HasIndex("BotId");
            });

            modelBuilder.Entity<DustRow>(dust =>
            {
                dust.ToTable("Dust");
                dust.HasKey(d => d.MarketSymbol);
            });

            modelBuilder.Entity<EquitySnapshotRow>(snapshot =>
            {
                snapshot.ToTable("EquitySnapshots");
                snapshot.HasKey(s => s.Id);
                snapshot.HasIndex(s => new { s.BaseMarket, s.At });
            });

            modelBuilder.Entity<PushSubscriptionRow>(subscription =>
            {
                subscription.ToTable("PushSubscriptions");
                subscription.HasKey(s => s.Endpoint);
            });
        }

        private class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
        {
            //An unspecified kind is taken as UTC (e.g. an order's unset ClosedAt): converting it as local would shift it
            public UtcDateTimeConverter() : base(value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc)) { }
        }
    }

    /// <summary>
    /// A bot: what <see cref="BotContext"/> needs to resume it while it's active (<see cref="FinishedAt"/> null), and how it ended.
    /// </summary>
    internal class BotRow
    {
        public Guid Id { get; set; }
        public int ConfigurationId { get; set; }
        public SpreadConfiguration Configuration { get; set; }
        public string MarketSymbol { get; set; }
        public BotState State { get; set; }
        public decimal Balance { get; set; }
        public decimal HeldAmount { get; set; }
        public decimal BoughtPrice { get; set; }
        public DateTime? BuyFilledAt { get; set; }
        public long? CurrentOrderRecordId { get; set; }
        public OrderData CurrentOrder { get; set; }

        //The market as the bot last saw it, until the restored bot's first update
        public decimal? BidRate { get; set; }
        public decimal? AskRate { get; set; }
        public decimal? LastTradeRate { get; set; }
        public decimal? PriceTick { get; set; }

        public DateTime AllocatedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        //Balance minus the allocation, once it finished; null for a bot a later session didn't resume
        public decimal? Profit { get; set; }
    }

    internal class DustRow
    {
        public string MarketSymbol { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// The dashboard's equity curve: what a base market's money was worth, sampled every few minutes.
    /// </summary>
    internal class EquitySnapshotRow
    {
        public long Id { get; set; }
        public DateTime At { get; set; }
        public string BaseMarket { get; set; }
        public decimal TotalValue { get; set; }
        public decimal Unallocated { get; set; }
        public int Bots { get; set; }
    }

    /// <summary>
    /// A browser that asked the dashboard for push notifications, and which kinds (comma-separated categories).
    /// </summary>
    internal class PushSubscriptionRow
    {
        public string Endpoint { get; set; }
        public string P256dh { get; set; }
        public string Auth { get; set; }
        public string Categories { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// For <c>dotnet ef migrations add</c>, which builds the model without connecting.
    /// </summary>
    internal class DesignTimeSpreadBotDbContextFactory : IDesignTimeDbContextFactory<SpreadBotDbContext>
    {
        public SpreadBotDbContext CreateDbContext(string[] args) =>
            new SpreadBotDbContext(new DbContextOptionsBuilder<SpreadBotDbContext>().UseSqlite("Data Source=design-time.db").Options);
    }
}
