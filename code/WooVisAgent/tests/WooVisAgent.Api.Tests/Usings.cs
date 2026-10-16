global using Xunit;

// 集成测试会启动真实宿主：各宿主通过进程环境变量注入配置，
// 并且 EF InMemory 的库名按宿主唯一，因此需要串行执行，避免宿主之间的相互干扰。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
