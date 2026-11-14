using MongoDB.Driver;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Grouping.Tests;

/// <summary>验证投影继承保存的覆盖值，并要求显式处理配置冲突。</summary>
public sealed class SessionConfigurationGroupingTests
{
    /// <summary>同样的覆盖值可自动归组，拆分后的两部分保持原共享规则。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("^统一：")]
    public async Task GroupAndSplitPreserveMatchingNullableOverrides(string? pattern)
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(3000);
        var configuration = Configuration(fixture);
        await configuration.UpdateAsync(first.Session.Id, new(pattern, 1));
        await configuration.UpdateAsync(second.Session.Id, new(pattern, 1));
        var grouped = await fixture.Service().GroupAsync(first.Session.Id, [second.Session.Id], null);
        Assert.Equal(pattern, grouped.TranslationDanmuPatternOverride);
        var detail = await fixture.Library.GetAsync(grouped.Id);
        var split = await fixture.Service().SplitAsync(grouped.Id, detail.Segments[2].Id);
        Assert.All(split, item => Assert.Equal(pattern, item.TranslationDanmuPatternOverride));
        foreach (var item in split)
        {
            Assert.Equal(pattern, (await configuration.GetAsync(item.Id)).TranslationDanmuPatternOverride);
        }
    }

    /// <summary>继承与显式相同文本仍是不同配置，失败不隐藏来源，明确选择后才归组。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("^合并：")]
    public async Task DifferentSavedRulesRequireAnExplicitValidChoice(string? pattern)
    {
        await using var fixture = await GroupingFixture.CreateAsync();
        var first = await fixture.SeedAsync();
        var second = await fixture.SeedAsync(3000);
        await Configuration(fixture).UpdateAsync(second.Session.Id, new(fixture.Settings.Value.TranslationDanmuPattern, 1));
        var service = fixture.Service();
        var conflict = await Assert.ThrowsAsync<InfrastructureException>(() => service.GroupAsync(first.Session.Id, [second.Session.Id], null));
        Assert.Equal(MessageCode.GROUPING_CONFIGURATION_REQUIRED.ToString(), conflict.Code);
        Assert.Equal(409, conflict.StatusCode);
        await Assert.ThrowsAsync<InfrastructureException>(() => service.GroupAsync(first.Session.Id, [second.Session.Id], null, new SessionGroupConfiguration("[")));
        Assert.Equal(2, await fixture.Mongo.Collection<BroadcastSession>("sessions").CountDocumentsAsync(x => !x.IsHidden));

        var grouped = await service.GroupAsync(first.Session.Id, [second.Session.Id], null, new SessionGroupConfiguration(pattern));
        Assert.Equal(pattern, grouped.TranslationDanmuPatternOverride);
        Assert.Equal(pattern, (await Configuration(fixture).GetAsync(grouped.Id)).TranslationDanmuPatternOverride);
    }

    private static SessionConfigurationService Configuration(GroupingFixture fixture) =>
        new(fixture.Mongo, fixture.Library, fixture.Settings, fixture.Paths);
}
