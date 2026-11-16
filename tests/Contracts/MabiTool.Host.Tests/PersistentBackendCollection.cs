using Xunit;

namespace MabiTool.Host.Tests;

// 故障注入与数据根测试依赖 fsync 及进程阶段截止时间，统一串行以隔离磁盘竞争。
[CollectionDefinition("Persistent backend", DisableParallelization = true)]
public sealed class PersistentBackendCollection;
