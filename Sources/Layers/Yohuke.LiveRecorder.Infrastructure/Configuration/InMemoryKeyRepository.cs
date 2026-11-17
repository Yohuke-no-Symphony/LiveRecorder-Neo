using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>让框架默认密钥预热仅使用内存；实例持久密钥由实例上下文管理。</summary>
public sealed class InMemoryKeyRepository : IXmlRepository
{
    private readonly object gate = new();
    private readonly List<XElement> elements = [];

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (gate)
        {
            return elements.Select(element => new XElement(element)).ToArray();
        }
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        lock (gate)
        {
            elements.Add(new(element));
        }
    }
}
