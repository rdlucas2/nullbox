using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nullbox.Core.Gmail;
using Nullbox.Web.Ui;

namespace Nullbox.Tests.Ui;

public sealed class DashboardStatusProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_reports_the_delete_lock_from_Gmail_options(bool allowDelete)
    {
        using var services = new ServiceCollection()
            .AddSingleton(Options.Create(new GmailOptions { AllowDelete = allowDelete }))
            .BuildServiceProvider();

        var snapshot = await new DashboardStatusProvider(services, TimeProvider.System).GetAsync();

        Assert.Equal(allowDelete, snapshot.DeletesAllowed);
    }

    [Fact]
    public async Task Without_Gmail_options_deletes_count_as_locked()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var snapshot = await new DashboardStatusProvider(services, TimeProvider.System).GetAsync();

        Assert.False(snapshot.DeletesAllowed);
    }
}
