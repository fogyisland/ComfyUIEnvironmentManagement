using System;
using System.IO;
using System.Threading;
using ComfyUI.Manager.Infrastructure;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// RobustDirectoryDelete 单元测试 —— 锁住 4 个 Windows 真实场景:
/// ReadOnly subdir / Hidden+System subdir / 深嵌套 / long path。
/// 锁住 contract:不存在 no-op;失败 throw IOException 含 InnerException 详情。
/// </summary>
public sealed class RobustDirectoryDeleteTests : IDisposable
{
    private readonly string _tempRoot;

    public RobustDirectoryDeleteTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rdd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Delete_NonExistent_NoOp()
    {
        RobustDirectoryDelete.Delete(Path.Combine(_tempRoot, "ghost"));
        // 没抛即 PASS
    }

    [Fact]
    public void Delete_NullOrEmpty_NoOp()
    {
        RobustDirectoryDelete.Delete(null!);
        RobustDirectoryDelete.Delete("");
        RobustDirectoryDelete.Delete("   ");
    }

    [Fact]
    public void Delete_EmptyDirectory_Removes()
    {
        var dir = Path.Combine(_tempRoot, "empty");
        Directory.CreateDirectory(dir);

        RobustDirectoryDelete.Delete(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_ReadOnlySubdirectory_Removes()
    {
        // 核心 bug 场景:ReadOnly attr 在 subdir,老 Directory.Delete recursive 会
        // "Access denied"。新实现显式 walk 清 attribute 后再删。
        var dir = Path.Combine(_tempRoot, "root");
        var sub = Path.Combine(dir, "readonly-sub");
        Directory.CreateDirectory(sub);
        File.SetAttributes(sub, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(sub, "inside.txt"), "");

        RobustDirectoryDelete.Delete(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_HiddenAndSystemSubdirectory_Removes()
    {
        var dir = Path.Combine(_tempRoot, "root");
        var sub = Path.Combine(dir, "hidden-sub");
        Directory.CreateDirectory(sub);
        File.SetAttributes(sub, FileAttributes.Hidden | FileAttributes.System);
        File.WriteAllText(Path.Combine(sub, "inside.txt"), "");

        RobustDirectoryDelete.Delete(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_DeepNestedReadOnlyFiles_Removes()
    {
        // 多层嵌套 ReadOnly file,模拟 git clone 后的 .git/objects/pack 结构。
        var dir = Path.Combine(_tempRoot, "root");
        var deep = dir;
        for (var i = 0; i < 5; i++)
        {
            deep = Path.Combine(deep, $"level-{i}");
            Directory.CreateDirectory(deep);
        }
        var file = Path.Combine(deep, "readonly.idx");
        File.WriteAllText(file, "");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        RobustDirectoryDelete.Delete(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_DirectoryLockedByHandle_RetriesAndThrows()
    {
        // 独占 handle 锁住目录里某个文件 → 重试 3 次后 throw IOException
        // (不再 silent swallow,让 caller 看到异常详情)。
        var dir = Path.Combine(_tempRoot, "root");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "locked.txt");
        File.WriteAllText(file, "");
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => RobustDirectoryDelete.Delete(dir));
            Assert.Contains("已重试 3 次", ex.Message);
            Assert.NotNull(ex.InnerException);  // 真实 OS 错误透传
        }
        // handle 释放后能删掉
        RobustDirectoryDelete.Delete(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_LongPath_Removes()
    {
        // MAX_PATH (260) 边界 — git clone 深嵌套或长文件名。
        // .NET 加 \\?\ 前缀走 NTFS long path API,绕过 PathTooLongException。
        var dir = Path.Combine(_tempRoot, "root");
        Directory.CreateDirectory(dir);
        var segment = new string('x', 30);
        var deep = dir;
        for (var i = 0; i < 4; i++)
        {
            deep = Path.Combine(deep, segment);
            Directory.CreateDirectory(deep);
        }
        File.WriteAllText(Path.Combine(deep, "marker.py"), "");

        RobustDirectoryDelete.Delete(dir);

        Assert.False(Directory.Exists(dir));
    }
}