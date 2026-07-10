using ComfyUI.Manager.Tools.Ts2Resx;
using Xunit;

namespace ComfyUI.Manager.Tools.Ts2Resx.Tests;

public class KeyDeriverTests
{
    [Fact]
    public void Derive_BasicContextAndSource()
    {
        var key = KeyDeriver.Derive("CatalogPage", "刷新",
            extraContext: null, isPlural: false);
        Assert.Equal("CatalogPage_刷新", key);
    }

    [Fact]
    public void Derive_WithExtraContext_AppendsSuffix()
    {
        var key = KeyDeriver.Derive("EnvPage", "Save",
            extraContext: "button", isPlural: false);
        Assert.Equal("EnvPage_Save__button", key);
    }

    [Fact]
    public void Derive_SpecialCharsSanitized()
    {
        var key = KeyDeriver.Derive("Page", "Save & Close!",
            extraContext: null, isPlural: false);
        Assert.Equal("Page_Save__Close", key);
    }

    [Fact]
    public void Derive_Plural_AppendsNSuffix()
    {
        var key = KeyDeriver.Derive("Page", "%n items",
            extraContext: null, isPlural: true);
        Assert.Contains("_N", key);
    }
}