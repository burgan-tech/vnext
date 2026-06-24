# Inbox & Outbox Pattern

## Overview

Ensures reliable message delivery in distributed systems. **Outbox** guarantees events are
published to the broker even if it is temporarily unavailable, by writing them inside the
same database transaction as business data. **Inbox** provides idempotent message processing
with automatic retry and dead-letter support.

## Provider support

The **lease mechanism** (row-level locking that lets workers compete for messages) is
provider-specific and lives in the database provider package:

| Provider | Lease Strategy | Status |
|---|---|---|
| `BBT.Aether.Npgsql` | `FOR UPDATE SKIP LOCKED` | ✅ Supported |
| `BBT.Aether.SqlServer` | `UPDLOCK, READPAST, ROWLOCK` | 🔜 Next phase |

`BBT.Aether.Infrastructure` is provider-agnostic. It contains the EF Core stores, processors,
background services, and options — but no database-specific SQL.

---

## DbContext Setup

```csharp
public class MyDbContext : AetherDbContext<MyDbContext>, IHasEfCoreOutbox, IHasEfCoreInbox
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureOutbox();
        modelBuilder.ConfigureInbox();
    }
}
```

---

## Service Registration

Register the provider first, then the inbox/outbox:

```csharp
// 1. Register the PostgreSQL provider (also wires lease stores automatically)
services.AddAetherNpgsql<MyDbContext>(connectionString);

// 2. Register the outbox — withHostedService: true starts the background polling loop
services.AddAetherOutbox<MyDbContext>(options =>
{
    options.Schema         = "sys_queues";       // schema hosting OutboxMessages table
    options.BatchSize      = 100;
    options.LeaseDuration  = TimeSpan.FromSeconds(30);
    options.MaxRetryCount  = 5;
}, withHostedService: true);

// 3. Register the inbox
services.AddAetherInbox<MyDbContext>(options =>
{
    options.Schema               = "sys_queues";
    options.ProcessingBatchSize  = 100;
    options.LeaseDuration        = TimeSpan.FromSeconds(30);
    options.MaxRetryCount        = 5;
}, withHostedService: true);
```

`withHostedService: false` (the default) means you manage the polling lifecycle yourself —
useful when you want custom scheduling or plan to trigger processing from a Dapr job.

---

## Outbox Usage

### Publishing domain events (recommended)

```csharp
public class Order : AuditedAggregateRoot<Guid>
{
    public void Place()
    {
        Status = OrderStatus.Placed;
        AddDistributedEvent(new OrderPlacedEvent(Id)); // written to outbox at UoW commit
    }
}
```

The event is serialized and stored in `OutboxMessages` inside the same transaction that
commits the `Order`. No broker call happens during the request.

### Publishing directly

```csharp
[UnitOfWork]
public async Task CreateOrderAsync(CreateOrderDto dto)
{
    var order = new Order(dto);
    await _orderRepository.InsertAsync(order);
    await _eventBus.PublishAsync(new OrderCreatedEvent(order.Id), useOutbox: true);
}
```

---

## Inbox Usage

### EventsController (automatic idempotency)

```csharp
[ApiController]
[Route("api/events")]
public class MyEventsController : EventsController
{
    public MyEventsController(
        IDistributedEventInvokerRegistry invokerRegistry,
        IInboxStore inboxStore,
        IUnitOfWorkManager unitOfWorkManager,
        IServiceProvider serviceProvider,
        IEventSerializer serializer,
        AetherInboxOptions inboxOptions,
        ICurrentSchema currentSchema,
        ILogger<EventsController> logger)
        : base(invokerRegistry, inboxStore, unitOfWorkManager,
               serviceProvider, serializer, inboxOptions, currentSchema, logger)
    { }

    [HttpPost("{name}/v{version}")]
    public async Task<IActionResult> HandleEvent(string name, int version, CancellationToken ct)
        => await ProcessEventAsync(name, version, ct);
}
```

Processing flow:

1. Receives event (e.g. from Dapr pubsub)
2. Checks `InboxMessages` for a duplicate event ID — returns 200 early if already processed (idempotency)
3. Stores as `Pending` in `InboxMessages`
4. Background `InboxProcessor` picks it up, invokes the registered handler
5. Marks as `Processed` on success; on failure applies retry backoff or transitions to `DeadLetter`

---

## Configuration Options

### AetherOutboxOptions

```csharp
new AetherOutboxOptions
{
    Schema               = "sys_queues",                    // Required — schema hosting the table
    BatchSize            = 100,                             // Messages leased per cycle
    LeaseDuration        = TimeSpan.FromSeconds(30),        // Lock expiry per lease
    MaxRetryCount        = 5,                               // Failed attempts before DeadLetter
    RetryBaseDelay       = TimeSpan.FromMinutes(1),         // Exponential backoff base
    RetentionPeriod      = TimeSpan.FromDays(7),            // Processed messages kept for
    BusyPollingInterval  = TimeSpan.FromMilliseconds(100),  // Delay when batch was non-empty
    IdlePollingInterval  = TimeSpan.FromSeconds(5),         // Starting delay when batch is empty
    MaxPollingInterval   = TimeSpan.FromSeconds(60),        // Backoff ceiling
}
```

### AetherInboxOptions

```csharp
new AetherInboxOptions
{
    Schema               = "sys_queues",
    ProcessingBatchSize  = 100,
    LeaseDuration        = TimeSpan.FromSeconds(30),
    MaxRetryCount        = 5,
    RetryBaseDelay       = TimeSpan.FromMinutes(1),
    RetentionPeriod      = TimeSpan.FromDays(7),
    CleanupInterval      = TimeSpan.FromHours(1),
    CleanupBatchSize     = 1000,
    BusyPollingInterval  = TimeSpan.FromMilliseconds(100),
    IdlePollingInterval  = TimeSpan.FromSeconds(5),
    MaxPollingInterval   = TimeSpan.FromSeconds(60),
}
```

---

## Status Enums

### OutboxMessageStatus

| Value | Meaning |
|---|---|
| `Pending = 0` | Waiting to be published |
| `Processing = 1` | Currently held by a worker lease |
| `Processed = 2` | Successfully published to the broker |
| `DeadLetter = 3` | Max retry count exhausted — requires manual intervention |

### IncomingEventStatus

| Value | Meaning |
|---|---|
| `Pending = 0` | Stored, waiting for processing |
| `Processing = 1` | Currently held by a worker lease |
| `Processed = 2` | Handler executed successfully |
| `Discarded = 3` | No handler found or deserialization failed — not retried |
| `DeadLetter = 4` | Max retry count exhausted — requires manual intervention |

> `DeadLetter` messages are excluded from lease queries. They will not be retried automatically
> and must be re-queued or resolved manually (e.g. via a support tool or migration script).

---

## Monitoring Dead Letters

Dead-letter messages can be identified with a direct query:

```sql
-- Outbox dead letters
SELECT * FROM "sys_queues"."OutboxMessages" WHERE "Status" = 3;

-- Inbox dead letters
SELECT * FROM "sys_queues"."InboxMessages" WHERE "Status" = 4;
```

Set up an alert or dashboard query on these — a growing count signals a systematic
integration failure that needs investigation.

---

## Entity Models

### OutboxMessage

```csharp
public class OutboxMessage : Entity<Guid>
{
    public string EventName { get; }
    public byte[] EventData { get; }        // Serialized CloudEventEnvelope
    public OutboxMessageStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string? LockedBy { get; set; }   // WorkerIdentity value + "/outbox"
    public DateTime? LockedUntil { get; set; }
    public ExtraPropertyDictionary ExtraProperties { get; set; }
}
```

### InboxMessage

```csharp
public class InboxMessage : Entity<string>   // Id = CloudEvent.id (idempotency key)
{
    public string EventName { get; }
    public byte[] EventData { get; }
    public IncomingEventStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? HandledTime { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryTime { get; set; }
    public string? LockedBy { get; set; }    // WorkerIdentity value + "/inbox"
    public DateTime? LockedUntil { get; set; }
    public ExtraPropertyDictionary ExtraProperties { get; set; }
}
```

---

## Best Practices

1. **Always use outbox for critical events** — ensures at-least-once delivery even when the broker is temporarily unavailable
2. **Set `Schema`** — leaving it null causes the processor to skip all runs with a warning
3. **Tune polling intervals** — `BusyPollingInterval` = throughput; `MaxPollingInterval` = idle DB load; defaults are sensible for most workloads
4. **Monitor `DeadLetter` count** — a rising count signals handler bugs, schema mismatches, or broker routing failures
5. **Don't bypass `EventsController`** — it handles idempotency and inbox storage; re-implementing these leads to subtle race conditions
6. **Use `withHostedService: true`** in production — or manage the background loop yourself if you need precise lifecycle control (e.g. jobs-based trigger)

---

## Related

- [Internals & Provider Guide](INTERNALS.md) — architecture, lease mechanism, how to add a new DB provider
- [Distributed Events](../distributed-events/README.md)
- [Unit of Work](../unit-of-work/README.md)
- [Domain Events](../domain-events/README.md)
