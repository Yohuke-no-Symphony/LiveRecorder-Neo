using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>只读加载现有密钥 XML，不创建目录或写入任何密钥文件。</summary>
public sealed class ReadonlyFileKeyRepository(DirectoryInfo directory) : IXmlRepository
{
    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory.FullName, "*.xml", SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        var elements = new List<XElement>();
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
                elements.Add(XElement.Load(reader));
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        return elements;
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        throw new NotSupportedException("只读密钥仓库不支持写入。");
    }
}
