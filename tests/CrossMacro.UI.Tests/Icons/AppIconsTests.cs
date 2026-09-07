
namespace CrossMacro.UI.Tests.Icons;

public sealed class AppIconsTests
{
    [Fact]
    public void GetPath_ForEveryDefinedIcon_ReturnsNonEmptyPath()
    {
        foreach (var icon in Enum.GetValues<AppIcon>())
        {
            _ = AppIcons.GetPath(icon).Should().NotBeNullOrWhiteSpace($"{icon} must have a vector path");
        }
    }

    [Fact]
    public void GetPath_WhenIconValueIsUnknown_Throws()
    {
        const AppIcon invalid = (AppIcon)(-1);

        var act = () => AppIcons.GetPath(invalid);

        _ = act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AppIconGeometryConverter_ConvertBack_Throws()
    {
        var act = () => AppIconGeometryConverter.Instance.ConvertBack(value: null, typeof(AppIcon), parameter: null, CultureInfo.InvariantCulture);

        _ = act.Should().Throw<NotSupportedException>();
    }

}
