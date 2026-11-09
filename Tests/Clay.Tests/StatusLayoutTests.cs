using System.Xml.Linq;

namespace Clay.Tests;

public class StatusLayoutTests
{
    [Fact]
    public void StatusIsBoundedToOneTruncatedLine()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var status = Assert.Single(document.Descendants(), element => (string?)element.Attribute(x + "Name") == "StatusText");

        Assert.Equal("NoWrap", (string?)status.Attribute("TextWrapping"));
        Assert.Equal("CharacterEllipsis", (string?)status.Attribute("TextTrimming"));
        Assert.Equal("1", (string?)status.Attribute("MaxLines"));
    }
}
