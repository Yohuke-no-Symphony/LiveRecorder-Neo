using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>复用只读仓库的密钥过滤规则，并沿用框架的持久化写入实现。</summary>
public sealed class FileKeyRepository(DirectoryInfo directory, ILoggerFactory loggerFactory) : FileSystemXmlRepository(directory, loggerFactory)
{
    private readonly ReadonlyFileKeyRepository reader = new(directory);

    /// <inheritdoc />
    public override IReadOnlyCollection<XElement> GetAllElements()
    {
        return reader.GetAllElements();
    }
}
