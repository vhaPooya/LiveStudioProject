using LiveStudio.Domain.Entities;
using Xunit;

namespace LiveStudio.Domain.Tests;

public sealed class BroadcastTests
{
    [Fact]
    public void NewBroadcast_HasGeneratedId_AndCreatedStatus()
    {
        var broadcast = new LiveBroadcast();
        Assert.NotEqual(Guid.Empty, broadcast.Id);
        Assert.Equal(BroadcastStatus.Created, broadcast.Status);
    }
}
