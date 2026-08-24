using System;
using CBMSB2BLink.Monitoring.Api;
using Xunit;

namespace CBMSB2BLink.Tests;

public class HealthCalculatorTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsHealthy_RecentSuccess_ReturnsTrue()
    {
        var lastRun = Now.AddMinutes(-10);
        Assert.True(HealthCalculator.IsHealthy(lastRun, "Success", staleThresholdMinutes: 60, nowUtc: Now));
    }

    [Fact]
    public void IsHealthy_RecentNoNewData_ReturnsTrue()
    {
        var lastRun = Now.AddMinutes(-5);
        Assert.True(HealthCalculator.IsHealthy(lastRun, "NoNewData", staleThresholdMinutes: 60, nowUtc: Now));
    }

    [Fact]
    public void IsHealthy_RecentFailed_ReturnsFalse()
    {
        var lastRun = Now.AddMinutes(-1);
        Assert.False(HealthCalculator.IsHealthy(lastRun, "Failed", staleThresholdMinutes: 60, nowUtc: Now));
    }

    [Fact]
    public void IsHealthy_StaleSuccess_ReturnsFalse()
    {
        var lastRun = Now.AddMinutes(-120);
        Assert.False(HealthCalculator.IsHealthy(lastRun, "Success", staleThresholdMinutes: 60, nowUtc: Now));
    }

    [Fact]
    public void IsHealthy_NeverRun_ReturnsFalse()
    {
        Assert.False(HealthCalculator.IsHealthy(null, null, staleThresholdMinutes: 60, nowUtc: Now));
    }

    [Fact]
    public void MinutesSinceLastRun_NullInput_ReturnsNull()
    {
        Assert.Null(HealthCalculator.MinutesSinceLastRun(null, Now));
    }

    [Fact]
    public void MinutesSinceLastRun_ComputesElapsedMinutes()
    {
        var lastRun = Now.AddMinutes(-30);
        Assert.Equal(30, HealthCalculator.MinutesSinceLastRun(lastRun, Now));
    }
}
