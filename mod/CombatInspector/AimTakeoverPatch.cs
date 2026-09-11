using Unity.Entities;

namespace CombatInspector
{
    /// <summary>
    /// 接管瞄准的 Harmony 补丁：在 MouseInputSystem 写完 MouseTarget 之后，把它改写成
    /// 建议层算出的拦截点。
    ///
    /// 目标是 <c>MouseInputSystem.__codegen__OnUpdate(IntPtr self, IntPtr state)</c>
    /// （Entities 为 unmanaged ISystem 生成的入口，是个 internal static 方法，所以 postfix
    /// 不涉及 struct 实例装箱）。选它而不是自己注册 ECS 系统，原因见 AimOverrideSystem 的注释：
    /// 托管 SystemBase 在这个世界里不被驱动。
    ///
    /// 时机正确性：MouseInputSystem 在自己的 OnUpdate 里已经调用过
    /// <c>CompleteDependencyBeforeRW&lt;MouseTarget&gt;()</c>，所以 postfix 执行时没有未完成的
    /// job 持有 MouseTarget，用 EntityManager 直接写是安全的。
    ///
    /// 补丁的挂载与失败报告在 Plugin.TryInstallAimPatch 里，找不到目标方法会明确写日志。
    /// </summary>
    internal static class AimTakeoverPatch
    {
        /// <summary>Harmony postfix。参数留空即可，我们不需要原方法的入参。</summary>
        public static void Postfix()
        {
            if (!AimOverrideSystem.Active) return;

            AimOverrideSystem.PatchRunCount++;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;

            AimOverrideSystem.ApplyAim(world.EntityManager);
        }
    }
}
