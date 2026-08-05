using System.Collections.Generic;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Data;

/// <summary>
/// IEnvironmentRepository:EnvironmentRepository 的抽象接口。
/// v0.6.5.8 引入 — 让 <see cref="ComfyUI.Manager.Services.BaseEnvInstaller"/>
/// 的依赖可以 mock / 包装,无需 unseal EnvironmentRepository(plan G10)。
/// 只暴露 BaseEnvInstaller 当前实际使用的 3 个方法;Delete 等只供其他消费者
/// 用的方法留在 concrete class。
/// </summary>
public interface IEnvironmentRepository
{
    /// <summary>列出所有 env 行。</summary>
    List<Environment> ListAll();

    /// <summary>按 id 取单行;不存在返 null。</summary>
    Environment? Get(string envId);

    /// <summary>插入或按主键更新一行 env。</summary>
    void Upsert(Environment env);
}