using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// EnvDeleterService:编排 env 删除流程(替代 EnvListVM 里的散代码)。
///
/// 步骤:
///   1. 如果 env.Status == "running" → 调 ProcessLauncher.StopEnvAsync(stop 失败静默吞,
///      目标就是删掉,文件锁冲突让 TryDelete 后续兜)
///   2. Directory.Delete(rootPath, recursive) + 3 次重试 + 全 file attribute 归零
///      (跟 NodeOperations.TryDelete 同模式,Windows 上 .git/objects/pack/ 经常
///      是 readonly 不清就删不掉)
///   3. EnvironmentRepository.Delete(envId) — SQLite 行
///
/// 异常分类:
/// - DELETE_DIR_FAILED: 3 次重试 + 清 attribute 都失败,SQLite 行不删(避免指向已
///   删目录的悬挂行)
/// - DELETE_DB_FAILED: 目录已删,但 SQLite DELETE 失败(理论上 SQLite 不会失败,
///   但保留抛点以便上层提示)
/// </summary>
public sealed class EnvDeleterService
{
    private readonly EnvironmentRepository _repo;
    private readonly ProcessLauncher _launcher;

    public EnvDeleterService(EnvironmentRepository repo, ProcessLauncher launcher)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public sealed class DeleteException : Exception
    {
        public string Code { get; }
        public DeleteException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    public async Task DeleteAsync(Environment env, CancellationToken ct = default)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        // 1. 自动 stop running env,避免删时文件锁冲突
        if (string.Equals(env.Status, "running", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _launcher.StopEnvAsync(env, timeoutSeconds: 3, ct).ConfigureAwait(false);
            }
            catch
            {
                // Stop 失败(send signal 失败 / 进程不响应 kill)→ 静默继续。
                // TryDelete 下面会 retry + 清 attribute,通常能扛过去。
            }
        }

        // 2. 删 env 根目录
        if (Directory.Exists(env.RootPath))
        {
            TryDeleteRecursive(env.RootPath);
        }

        // 3. 删 SQLite 行
        try
        {
            _repo.Delete(env.Id);
        }
        catch (Exception ex)
        {
            throw new DeleteException("DELETE_DB_FAILED",
                $"env 目录已删除,但 SQLite 行清理失败:{ex.Message}");
        }
    }

    /// <summary>
    /// TryDeleteRecursive:跟 NodeOperations.TryDelete 同模式 — Windows 上 .git/objects/pack/
    /// 下的 pack/idx 经常是 readonly,Directory.Delete 会"Access denied"。
    /// 先清 attribute 再删,3 次重试。
    /// </summary>
    private static void TryDeleteRecursive(string dir)
    {
        if (!Directory.Exists(dir)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
                }
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
        throw new DeleteException("DELETE_DIR_FAILED",
            $"目录删除失败 (3 次重试用尽):{dir}");
    }
}