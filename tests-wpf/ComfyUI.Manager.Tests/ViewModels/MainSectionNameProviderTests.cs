using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class MainSectionNameProviderTests
{
    [Fact]
    public void GetName_Dashboard_ReturnsHomepage()
    {
        Assert.Equal("主页", MainSectionNameProvider.GetName(MainSection.Dashboard));
    }

    [Fact]
    public void GetName_Environments_ReturnsEnvironment()
    {
        Assert.Equal("环境", MainSectionNameProvider.GetName(MainSection.Environments));
    }

    [Fact]
    public void GetName_Catalog_ReturnsNodeCatalog()
    {
        Assert.Equal("节点目录", MainSectionNameProvider.GetName(MainSection.Catalog));
    }

    [Fact]
    public void GetName_BaseEnv_ReturnsBaseEnvironment()
    {
        Assert.Equal("基础环境", MainSectionNameProvider.GetName(MainSection.BaseEnv));
    }

    [Fact]
    public void GetName_Settings_ReturnsSettings()
    {
        Assert.Equal("设置", MainSectionNameProvider.GetName(MainSection.Settings));
    }

    [Fact]
    public void GetName_BulkUpdate_ReturnsBulkUpdate()
    {
        Assert.Equal("批量更新", MainSectionNameProvider.GetName(MainSection.BulkUpdate));
    }

    [Fact]
    public void GetName_SystemStatus_ReturnsSystemStatus()
    {
        Assert.Equal("系统状态", MainSectionNameProvider.GetName(MainSection.SystemStatus));
    }
}
