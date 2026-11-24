using System.Xml.Linq;

namespace Clay.Tests;

public class StatusLayoutTests
{
    [Fact]
    public void StatusIsBoundedToOneTruncatedLine()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var status = document.Descendants()
            .Where(element => (string?)element.Attribute(x + "Name") == "StatusText")
            .ShouldHaveSingleItem();

        ((string?)status.Attribute("TextWrapping")).ShouldBe("NoWrap");
        ((string?)status.Attribute("TextTrimming")).ShouldBe("CharacterEllipsis");
        ((string?)status.Attribute("MaxLines")).ShouldBe("1");
    }
}
