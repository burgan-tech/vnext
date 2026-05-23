# Dapr-Compatible Timer Examples

This document demonstrates how the enhanced `TimerSchedule` provides full compatibility with Dapr's job scheduling capabilities.

## Complete Dapr Compatibility

Our `TimerSchedule` now supports all Dapr scheduling types:

### 1. DateTime Scheduling

```csharp
public class SpecificDateTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        // Schedule for a specific date and time
        var targetDate = new DateTime(2024, 12, 31, 23, 59, 0, DateTimeKind.Utc);
        return TimerSchedule.FromDateTime(targetDate);
    }
}
```

### 2. Cron Expression Scheduling (String-based)

```csharp
public class CronExpressionTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var scheduleType = context.Instance.Data.scheduleType?.ToString() ?? "daily";
        
        return scheduleType switch
        {
            "every_5_minutes" => TimerSchedule.FromCronExpression("*/5 * * * *"),
            "hourly" => TimerSchedule.FromCronExpression("0 * * * *"),
            "daily_at_6am" => TimerSchedule.FromCronExpression("0 6 * * *"),
            "weekly_friday" => TimerSchedule.FromCronExpression("0 9 * * 5"),
            "monthly_first" => TimerSchedule.FromCronExpression("0 9 1 * *"),
            _ => TimerSchedule.FromCronExpression("0 9 * * *") // Daily default
        };
    }
}
```

### 3. Cron Expression Builder (Fluent API)

```csharp
public class FluentCronTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        // Using Dapr's fluent Cron expression builder
        var cronBuilder = new CronExpressionBuilder()
            .AtMinute(0)
            .AtHour(9)
            .OnDayOfWeek(DayOfWeek.Monday, DayOfWeek.Friday); // Weekdays at 9 AM
        
        return TimerSchedule.FromCronExpression(cronBuilder);
    }
}
```

### 4. Dapr @ Prefixed Expressions

```csharp
public class DaprExpressionTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var frequency = context.Instance.Data.frequency?.ToString() ?? "daily";
        
        return frequency switch
        {
            "yearly" => TimerSchedule.FromExpression("@yearly"),
            "monthly" => TimerSchedule.FromExpression("@monthly"),
            "weekly" => TimerSchedule.FromExpression("@weekly"),
            "daily" => TimerSchedule.FromExpression("@daily"),
            "midnight" => TimerSchedule.FromExpression("@midnight"),
            "hourly" => TimerSchedule.FromExpression("@hourly"),
            "every_10m" => TimerSchedule.FromExpression("@every 10m"),
            "every_2h" => TimerSchedule.FromExpression("@every 2h"),
            _ => TimerSchedule.FromExpression("@daily")
        };
    }
}
```

### 5. Duration-Based Scheduling

```csharp
public class DurationTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var priority = context.Instance.Data.priority?.ToString()?.ToLower() ?? "normal";
        
        return priority switch
        {
            "urgent" => TimerSchedule.FromDuration(TimeSpan.FromMinutes(5)),
            "high" => TimerSchedule.FromDuration(TimeSpan.FromMinutes(30)),
            "normal" => TimerSchedule.FromDuration(TimeSpan.FromHours(2)),
            "low" => TimerSchedule.FromDuration(TimeSpan.FromHours(24)),
            _ => TimerSchedule.FromDuration(TimeSpan.FromHours(2))
        };
    }
}
```

### 6. Predefined Schedules

```csharp
public class PredefinedScheduleTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var frequency = context.Instance.Data.frequency?.ToString() ?? "daily";
        
        return frequency switch
        {
            "yearly" => TimerSchedule.Yearly,
            "monthly" => TimerSchedule.Monthly,
            "weekly" => TimerSchedule.Weekly,
            "daily" => TimerSchedule.Daily,
            "midnight" => TimerSchedule.Midnight,
            "hourly" => TimerSchedule.Hourly,
            "immediate" => TimerSchedule.Immediate(),
            _ => TimerSchedule.Daily
        };
    }
}
```

## Enhanced PaymentDueTimerRule Example

Here's the enhanced payment timer rule that leverages all Dapr capabilities:

```csharp
public class EnhancedPaymentDueTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        try
        {
            var paymentSchedule = context.Instance.Data.paymentSchedule;
            if (paymentSchedule == null)
                return TimerSchedule.Daily; // Default to daily processing

            // Check for specific next payment date
            if (paymentSchedule.nextPaymentDate != null)
            {
                if (DateTime.TryParse(paymentSchedule.nextPaymentDate.ToString(), out DateTime nextPayment))
                    return TimerSchedule.FromDateTime(nextPayment);
            }

            // Check for custom cron expression
            if (paymentSchedule.cronExpression != null)
            {
                return TimerSchedule.FromCronExpression(paymentSchedule.cronExpression.ToString());
            }

            // Check for Dapr @ expression
            if (paymentSchedule.daprExpression != null)
            {
                return TimerSchedule.FromExpression(paymentSchedule.daprExpression.ToString());
            }

            // Calculate schedule based on frequency using different scheduling types
            var frequency = paymentSchedule.frequency?.ToString()?.ToLower() ?? "monthly";
            var businessHours = paymentSchedule.businessHoursOnly?.ToString()?.ToLower() == "true";
            
            return frequency switch
            {
                "immediate" => TimerSchedule.Immediate(),
                
                // Duration-based for short intervals
                "5_minutes" => TimerSchedule.FromDuration(TimeSpan.FromMinutes(5)),
                "30_minutes" => TimerSchedule.FromDuration(TimeSpan.FromMinutes(30)),
                "2_hours" => TimerSchedule.FromDuration(TimeSpan.FromHours(2)),
                
                // Cron expressions for precise scheduling
                "daily" when businessHours => TimerSchedule.FromCronExpression("0 9 * * 1-5"), // Weekdays at 9 AM
                "daily" => TimerSchedule.FromCronExpression("0 9 * * *"), // Daily at 9 AM
                
                "weekly" when businessHours => TimerSchedule.FromCronExpression("0 9 * * 1"), // Monday at 9 AM
                "weekly" => TimerSchedule.Weekly,
                
                "monthly" when businessHours => TimerSchedule.FromCronExpression("0 9 1 * *"), // 1st of month at 9 AM
                "monthly" => TimerSchedule.Monthly,
                
                "quarterly" => TimerSchedule.FromCronExpression("0 9 1 */3 *"), // Quarterly on 1st at 9 AM
                "yearly" => TimerSchedule.Yearly,
                
                // Dapr @ expressions for common patterns
                "hourly" => TimerSchedule.Hourly,
                "midnight" => TimerSchedule.Midnight,
                
                // Custom @ every expressions
                "every_10_minutes" => TimerSchedule.FromExpression("@every 10m"),
                "every_2_hours" => TimerSchedule.FromExpression("@every 2h"),
                "every_6_hours" => TimerSchedule.FromExpression("@every 6h"),
                
                _ => TimerSchedule.Daily // Default
            };
        }
        catch (Exception)
        {
            return TimerSchedule.Daily; // Fallback
        }
    }
}
```

## Fluent Cron Builder Example

```csharp
public class BusinessHoursTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var department = context.Instance.Data.department?.ToString() ?? "general";
        
        return department switch
        {
            "finance" => TimerSchedule.FromCronExpression(
                new CronExpressionBuilder()
                    .AtMinute(0)
                    .AtHour(8)
                    .OnDayOfWeek(DayOfWeek.Monday, DayOfWeek.Friday)
            ),
            
            "sales" => TimerSchedule.FromCronExpression(
                new CronExpressionBuilder()
                    .AtMinute(30)
                    .AtHour(9)
                    .OnDayOfWeek(DayOfWeek.Monday, DayOfWeek.Friday)
            ),
            
            "support" => TimerSchedule.FromCronExpression(
                new CronExpressionBuilder()
                    .AtMinute(0)
                    .AtHour(0, 6, 12, 18) // Every 6 hours
            ),
            
            _ => TimerSchedule.Daily
        };
    }
}
```

## Conditional Complex Scheduling

```csharp
public class ComplexConditionalTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var amount = context.Instance.Data.amount != null 
            ? Convert.ToDecimal(context.Instance.Data.amount) 
            : 0m;
        
        var region = context.Instance.Data.region?.ToString() ?? "default";
        var isWeekend = DateTime.UtcNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        
        // Complex business logic
        if (amount > 100000) // Very high-value transactions
        {
            if (isWeekend)
            {
                // Schedule for Monday morning if it's weekend
                return TimerSchedule.FromCronExpression("0 8 * * 1");
            }
            else
            {
                // Process immediately during business days
                return TimerSchedule.Immediate();
            }
        }
        else if (amount > 10000) // High-value transactions
        {
            return region switch
            {
                "us" => TimerSchedule.FromCronExpression("0 9 * * 1-5"), // US business hours
                "eu" => TimerSchedule.FromCronExpression("0 8 * * 1-5"), // EU business hours
                "asia" => TimerSchedule.FromCronExpression("0 10 * * 1-5"), // Asia business hours
                _ => TimerSchedule.FromDuration(TimeSpan.FromHours(2))
            };
        }
        else // Standard transactions
        {
            // Batch process at midnight
            return TimerSchedule.Midnight;
        }
    }
}
```

## Direct Dapr Conversion

The `ToDaprJobSchedule()` method seamlessly converts to Dapr's format:

```csharp
// All of these convert properly to Dapr format
var schedule1 = TimerSchedule.FromDateTime(DateTime.UtcNow.AddHours(2));
var daprSchedule1 = schedule1.ToDaprJobSchedule(); // DaprJobSchedule.FromDateTime()

var schedule2 = TimerSchedule.FromCronExpression("0 9 * * *");
var daprSchedule2 = schedule2.ToDaprJobSchedule(); // DaprJobSchedule.FromExpression()

var schedule3 = TimerSchedule.FromDuration(TimeSpan.FromMinutes(30));
var daprSchedule3 = schedule3.ToDaprJobSchedule(); // DaprJobSchedule.FromDuration()

var schedule4 = TimerSchedule.FromExpression("@every 10m");
var daprSchedule4 = schedule4.ToDaprJobSchedule(); // DaprJobSchedule.FromExpression()

var schedule5 = TimerSchedule.Hourly;
var daprSchedule5 = schedule5.ToDaprJobSchedule(); // DaprJobSchedule.FromExpression("@hourly")
```

## Benefits

1. **Full Dapr Compatibility**: Support for all Dapr scheduling types
2. **Flexible APIs**: String-based and fluent builder approaches
3. **Predefined Schedules**: Common patterns like @yearly, @monthly, @daily
4. **Business Logic Integration**: Complex conditional scheduling
5. **Seamless Conversion**: Direct translation to DaprJobSchedule
6. **Performance**: Efficient scheduling with appropriate timing strategies
7. **Maintainability**: Clear, readable timer logic with explicit schedule types

The enhanced system now provides complete compatibility with Dapr's job scheduling while maintaining the workflow engine's architecture and business logic capabilities.
