using System.Runtime.CompilerServices;

// Hevo.Charting.Tests 跨 assembly 调用 PythonHandlerRegistry.InternalImportModule 等 internal 成员。
[assembly: InternalsVisibleTo("Hevo.Charting.Tests")]
