# Enhanced Timer Mapping Examples

This document provides examples of how to use the enhanced Timer Mapping system that supports Dapr-compatible scheduling including DateTime, Cron expressions, Duration, and Immediate execution.

## Overview

The enhanced `ITimerMapping` interface now returns `TimerSchedule` instead of just `DateTime`, providing the same flexibility as Dapr's job scheduling system.

## Basic Interface

```csharp
public interface ITimerMapping
{
    Task<TimerSchedule> Handler(ScriptContext context);
}
```

## Enhanced PaymentDueTimerRule Example

Here's how the original PaymentDueTimerRule can be enhanced to use flexible scheduling:

```csharp
public class EnhancedPaymentDueTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        try
        {
            var paymentSchedule = context.Instance.Data.paymentSchedule;
            if (paymentSchedule == null)
                return TimerSchedule.FromDuration(TimeSpan.FromDays(1)); // Default to 1 day

            // Check for specific next payment date
            if (paymentSchedule.nextPaymentDate != null)
            {
                if (DateTime.TryParse(paymentSchedule.nextPaymentDate.ToString(), out DateTime nextPayment))
                    return TimerSchedule.FromDateTime(nextPayment);
            }

            // Calculate schedule based on frequency using different scheduling types
            var frequency = paymentSchedule.frequency?.ToString().ToLower() ?? "monthly";
            
            return frequency switch
            {
                "daily" => TimerSchedule.FromCronExpression("0 9 * * *"), // Daily at 9 AM
                "weekly" => TimerSchedule.FromCronExpression("0 9 * * 1"), // Weekly on Monday at 9 AM
                "monthly" => TimerSchedule.FromCronExpression("0 9 1 * *"), // Monthly on 1st at 9 AM
                "quarterly" => TimerSchedule.FromCronExpression("0 9 1 */3 *"), // Quarterly on 1st at 9 AM
                "yearly" => TimerSchedule.FromCronExpression("0 9 1 1 *"), // Yearly on Jan 1st at 9 AM
                "immediate" => TimerSchedule.Immediate(),
                _ => TimerSchedule.FromDuration(TimeSpan.FromDays(30)) // Default monthly
            };
        }
        catch (Exception)
        {
            return TimerSchedule.FromDuration(TimeSpan.FromDays(1)); // Fallback
        }
    }
}
```

## Different Scheduling Types Examples

### 1. DateTime-Based Scheduling

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

### 2. Cron Expression Scheduling

```csharp
public class RecurringTimerRule : ITimerMapping
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

### 3. Duration-Based Scheduling

```csharp
public class DynamicDurationTimerRule : ITimerMapping
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

### 4. Immediate Execution

```csharp
public class ImmediateTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        // For testing or when immediate execution is required
        return TimerSchedule.Immediate();
    }
}
```

### 5. Business Logic-Based Scheduling

```csharp
public class BusinessHoursTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var now = DateTime.UtcNow;
        var isWeekend = now.DayOfWeek == DayOfWeek.Saturday || now.DayOfWeek == DayOfWeek.Sunday;
        var hour = now.Hour;
        
        // If it's outside business hours (9 AM - 5 PM) or weekend, schedule for next business day at 9 AM
        if (isWeekend || hour < 9 || hour >= 17)
        {
            return TimerSchedule.FromCronExpression("0 9 * * 1-5"); // Next weekday at 9 AM
        }
        
        // If it's during business hours, execute in 1 hour
        return TimerSchedule.FromDuration(TimeSpan.FromHours(1));
    }
}
```

### 6. Conditional Scheduling

```csharp
public class ConditionalTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var amount = context.Instance.Data.amount != null 
            ? Convert.ToDecimal(context.Instance.Data.amount) 
            : 0m;
        
        if (amount > 10000) // High-value transactions
        {
            // Process immediately for high-value transactions
            return TimerSchedule.Immediate();
        }
        else if (amount > 1000) // Medium-value transactions
        {
            // Process within 30 minutes
            return TimerSchedule.FromDuration(TimeSpan.FromMinutes(30));
        }
        else // Low-value transactions
        {
            // Batch process daily at midnight
            return TimerSchedule.FromCronExpression("0 0 * * *");
        }
    }
}
```

## Backward Compatibility

The system maintains backward compatibility with existing DateTime-based timer implementations. If you return a DateTime from a script, it will be automatically converted to a TimerSchedule:

```csharp
// This will still work - automatically converted to TimerSchedule.FromDateTime()
public class LegacyTimerRule : ITimerMapping
{
    public async Task<TimerSchedule> Handler(ScriptContext context)
    {
        var legacyDateTime = DateTime.UtcNow.AddHours(2);
        return TimerSchedule.FromDateTime(legacyDateTime);
    }
}
```

## Common Cron Expression Patterns

Here are some commonly used Cron expressions:

```
0 * * * *        - Every hour
0 9 * * *        - Daily at 9 AM
0 9 * * 1-5      - Weekdays at 9 AM
0 0 1 * *        - First day of every month at midnight
0 0 1 1 *        - January 1st at midnight (yearly)
*/15 * * * *     - Every 15 minutes
0 */6 * * *      - Every 6 hours
0 9,17 * * 1-5   - 9 AM and 5 PM on weekdays
```

## Benefits of Enhanced Timer System

1. **Flexibility**: Support for absolute times, relative durations, and recurring schedules
2. **Dapr Compatibility**: Direct integration with Dapr's job scheduling capabilities
3. **Business Logic**: Easy implementation of complex business timing rules
4. **Performance**: Efficient scheduling with appropriate timing strategies
5. **Maintainability**: Clear, readable timer logic with explicit schedule types
6. **Backward Compatibility**: Existing DateTime-based timers continue to work

## Migration Guide

To migrate existing timer rules:

1. Change return type from `Task<DateTime>` to `Task<TimerSchedule>`
2. Wrap existing DateTime returns with `TimerSchedule.FromDateTime()`
3. Consider using more appropriate scheduling types (Cron, Duration) where applicable
4. Update unit tests to work with TimerSchedule objects

The enhanced timer system provides powerful scheduling capabilities while maintaining simplicity and backward compatibility.
