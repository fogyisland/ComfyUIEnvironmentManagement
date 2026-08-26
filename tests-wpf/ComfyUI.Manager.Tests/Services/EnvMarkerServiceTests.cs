using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public class EnvMarkerServiceTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "cmgr-marker-" + Guid.NewGuid().ToString("N")[..8]);

    public EnvMarkerServiceTests()
    {
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_WritesMarkerFileAndSetsHiddenAttribute()
    {
        var envDir = Path.Combine(_workDir, "Env-A");
        Directory.CreateDirectory(envDir);
        var marker = new EnvMarker
        {
            EnvId = "env-aaa11111",
            Name = "Env-A",
            Kind = "ComfyUI",
            TemplateSnapshot = new TemplateConfig { Kind = "ComfyUI", LocalSourceDir = "ComfyUI" },
            CreatedAt = "2026-08-26T00:00:00Z",
        };

        var ok = EnvMarkerService.Write(envDir, marker);

        Assert.True(ok);
        var path = Path.Combine(envDir, EnvMarker.FileName);
        Assert.True(File.Exists(path));
        // FileAttributes.Hidden — Windows 文件管理器默认不显示
        var attrs = File.GetAttributes(path);
        Assert.True((attrs & FileAttributes.Hidden) != 0,
            $"期望 marker 设 Hidden 属性,实际={attrs}");
    }

    [Fact]
    public void Read_AfterWrite_RoundTripsMarker()
    {
        var envDir = Path.Combine(_workDir, "Env-B");
        Directory.CreateDirectory(envDir);
        var marker = new EnvMarker
        {
            EnvId = "env-bbb22222",
            Name = "Env-B",
            Kind = "OpenVoice",
            TemplateSnapshot = new TemplateConfig { Kind = "OpenVoice", LocalSourceDir = "OpenVoice" },
            CreatedAt = "2026-08-26T01:00:00Z",
        };
        EnvMarkerService.Write(envDir, marker);

        var read = EnvMarkerService.Read(envDir);

        Assert.NotNull(read);
        Assert.Equal("env-bbb22222", read!.EnvId);
        Assert.Equal("Env-B", read.Name);
        Assert.Equal("OpenVoice", read.Kind);
        Assert.Equal("2026-08-26T01:00:00Z", read.CreatedAt);
        Assert.NotNull(read.TemplateSnapshot);
        Assert.Equal("OpenVoice", read.TemplateSnapshot!.Kind);
    }

    [Fact]
    public void Read_EnvDirDoesNotExist_ReturnsNull()
    {
        var read = EnvMarkerService.Read(Path.Combine(_workDir, "Env-Nonexistent"));
        Assert.Null(read);
    }

    [Fact]
    public void Read_MarkerFileDoesNotExist_ReturnsNull()
    {
        var envDir = Path.Combine(_workDir, "Env-NoMarker");
        Directory.CreateDirectory(envDir);

        var read = EnvMarkerService.Read(envDir);

        Assert.Null(read);
    }

    [Fact]
    public void Read_MalformedJson_ReturnsNull()
    {
        var envDir = Path.Combine(_workDir, "Env-Malformed");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, EnvMarker.FileName), "{this is not valid json");

        var read = EnvMarkerService.Read(envDir);

        Assert.Null(read);
    }

    [Fact]
    public void Read_WrongSchemaVersion_ReturnsNull()
    {
        var envDir = Path.Combine(_workDir, "Env-OldSchema");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, EnvMarker.FileName),
            "{\"schema_version\":99,\"env_id\":\"x\",\"name\":\"x\",\"kind\":\"x\"}");

        var read = EnvMarkerService.Read(envDir);

        Assert.Null(read);
    }

    [Fact]
    public void Read_MissingEnvId_ReturnsNull()
    {
        var envDir = Path.Combine(_workDir, "Env-NoId");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(Path.Combine(envDir, EnvMarker.FileName),
            $"{{\"schema_version\":1,\"env_id\":\"\",\"name\":\"x\",\"kind\":\"ComfyUI\"}}");

        var read = EnvMarkerService.Read(envDir);

        Assert.Null(read);
    }

    [Fact]
    public void Write_OverwriteExistingMarker_RewritesCleanly()
    {
        var envDir = Path.Combine(_workDir, "Env-Overwrite");
        Directory.CreateDirectory(envDir);
        EnvMarkerService.Write(envDir, new EnvMarker
        {
            EnvId = "env-old1",
            Name = "Old",
            Kind = "ComfyUI",
        });
        EnvMarkerService.Write(envDir, new EnvMarker
        {
            EnvId = "env-new2",
            Name = "New",
            Kind = "ComfyUI",
        });

        var read = EnvMarkerService.Read(envDir);

        Assert.NotNull(read);
        Assert.Equal("env-new2", read!.EnvId);
        Assert.Equal("New", read.Name);
    }
}