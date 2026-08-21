using System.Text.Json;
using ComfyUI.Manager.Services.ModelSources;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class ModelScopeDtoTests
{
    [Fact]
    public void Deserialize_ModelsList_MapsAllFields()
    {
        // v0.6.22.x:ModelScope /api/v1/models response — Data envelope + Model.Models[] array
        // 加 Unicode 中文名 + 空 Tags + null Owner(覆盖边界)。
        var json = """
        {
          "Code": 200,
          "Data": {
            "Model": {
              "PageNumber": 1,
              "PageSize": 2,
              "TotalCount": 47,
              "Models": [
                {
                  "Id": 12345,
                  "Name": "AI-ModelScope/foo",
                  "ChineseName": "测试模型",
                  "Tags": ["stable-diffusion", "lora"],
                  "Downloads": 100,
                  "Stars": 5,
                  "Likes": 10,
                  "Description": "test desc",
                  "Task": "text-to-image",
                  "Owner": null,
                  "DefaultRevision": "master"
                },
                {
                  "Id": 67890,
                  "Name": "bar",
                  "ChineseName": null,
                  "Tags": [],
                  "Downloads": 0,
                  "Stars": 0,
                  "Likes": 0,
                  "Description": null,
                  "Task": null,
                  "Owner": { "Name": "user1", "DisplayName": "User One" },
                  "DefaultRevision": "v1.0"
                }
              ]
            }
          }
        }
        """;
        var resp = JsonSerializer.Deserialize<ModelScopeDtos.ModelsResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(resp);
        Assert.Equal(200, resp!.Code);
        Assert.Equal(47, resp.Data!.Model!.TotalCount);
        Assert.Equal(2, resp.Data.Model.Models.Count);
        var a = resp.Data.Model.Models[0];
        Assert.Equal(12345L, a.Id);
        Assert.Equal("AI-ModelScope/foo", a.Name);
        Assert.Equal("测试模型", a.ChineseName);
        Assert.Equal(new[] { "stable-diffusion", "lora" }, a.Tags);
        Assert.Equal(100, a.Downloads);
        Assert.Null(a.Owner);
        Assert.Equal("master", a.DefaultRevision);
        var b = resp.Data.Model.Models[1];
        Assert.Null(b.ChineseName);
        Assert.Empty(b.Tags);
        Assert.NotNull(b.Owner);
        Assert.Equal("User One", b.Owner!.DisplayName);
    }
}