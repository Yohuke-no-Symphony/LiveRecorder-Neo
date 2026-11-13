using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Server.Configuration;

/// <summary>未安装时不读取旧实例的认证 Cookie，实例发布后启用标准认证方案。</summary>
public sealed class InstanceAuthenticationSchemeProvider(IOptions<AuthenticationOptions> options, CurrentInstanceStorage storage) : AuthenticationSchemeProvider(options)
{
    /// <inheritdoc />
    public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() =>
        storage.IsInstalled ? base.GetDefaultAuthenticateSchemeAsync() : Task.FromResult<AuthenticationScheme?>(null);

    /// <inheritdoc />
    public override Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() =>
        storage.IsInstalled ? base.GetRequestHandlerSchemesAsync() : Task.FromResult(Enumerable.Empty<AuthenticationScheme>());
}
