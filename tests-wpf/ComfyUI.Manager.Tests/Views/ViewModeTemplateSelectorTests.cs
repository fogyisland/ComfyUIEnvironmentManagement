using System.Windows;
using ComfyUI.Manager.Views.LocalModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Views;

public class ViewModeTemplateSelectorTests
{
    private static ViewModeTemplateSelector CreateSelector()
    {
        return new ViewModeTemplateSelector
        {
            CardsTemplate = new DataTemplate(),
            ListTemplate = new DataTemplate(),
            ThumbnailsTemplate = new DataTemplate()
        };
    }

    [Fact]
    public void SelectTemplate_Cards_ReturnsCardsTemplate()
    {
        var sel = CreateSelector();
        var result = sel.SelectTemplate(ViewMode.Cards, null!);
        Assert.Same(sel.CardsTemplate, result);
    }

    [Fact]
    public void SelectTemplate_List_ReturnsListTemplate()
    {
        var sel = CreateSelector();
        var result = sel.SelectTemplate(ViewMode.List, null!);
        Assert.Same(sel.ListTemplate, result);
    }

    [Fact]
    public void SelectTemplate_Thumbnails_ReturnsThumbnailsTemplate()
    {
        var sel = CreateSelector();
        var result = sel.SelectTemplate(ViewMode.Thumbnails, null!);
        Assert.Same(sel.ThumbnailsTemplate, result);
    }

    [Fact]
    public void SelectTemplate_UnknownItem_ReturnsCardsAsDefault()
    {
        var sel = CreateSelector();
        var result = sel.SelectTemplate("UnknownString", null!);
        Assert.Same(sel.CardsTemplate, result);
    }
}