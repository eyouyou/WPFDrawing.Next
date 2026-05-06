global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;

// 暴露 internal API 给周边工程项目(benchmark 直调避免反射污染数据,test 内省缓存等)。
// 其他外部消费方仍受 internal 约束。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hevo.Charting.Benchmarks")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Hevo.Charting.Tests")]
