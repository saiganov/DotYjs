using BenchmarkDotNet.Running;

// Run all benchmarks:     dotnet run -c Release --project benchmarks/DotYjs.Benchmarks -- --filter '*'
// Run one class:          dotnet run -c Release --project benchmarks/DotYjs.Benchmarks -- --filter '*TextEditing*'
// Quick smoke run:        dotnet run -c Release --project benchmarks/DotYjs.Benchmarks -- --filter '*' --job dry
BenchmarkSwitcher.FromAssembly(typeof(DotYjs.Benchmarks.DocumentFactory).Assembly).Run(args);
