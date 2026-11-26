using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Server.Configuration;

/// <summary>实例就绪后启用标准认证方案。</summary>
public sealed class InstanceAuthenticationSchemeProvider(IOptions<AuthenticationOptions> options, CurrentInstanceStorage storage) : AuthenticationSchemeProvider(options)
{
    /// <inheritdoc />
    public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() =>
        storage.IsReady ? base.GetDefaultAuthenticateSchemeAsync() : Task.FromResult<AuthenticationScheme?>(null);

    /// <inheritdoc />
    public override Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() =>
        storage.IsReady ? base.GetRequestHandlerSchemesAsync() : Task.FromResult(Enumerable.Empty<AuthenticationScheme>());
}
